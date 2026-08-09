import re
import shutil
import tempfile
from dataclasses import dataclass
from pathlib import Path

from lxml import etree

_INVALID_PROVIDER_TAG = re.compile(
    rb"<(?P<tag>[0-9][A-Za-z0-9._-]*id)>(?P<value>[^<]*)</(?P=tag)>",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class NfoIssue:
    code: str
    message: str
    severity: str = "warning"
    fixable: bool = False


@dataclass(frozen=True)
class NfoAuditResult:
    path: Path | None
    issues: tuple[NfoIssue, ...]

    @property
    def valid(self) -> bool:
        return not any(issue.severity == "error" for issue in self.issues)

    @property
    def fixable(self) -> bool:
        return any(issue.fixable for issue in self.issues)


def _strict_parser() -> etree.XMLParser:
    return etree.XMLParser(resolve_entities=False, no_network=True, recover=False, remove_blank_text=False)


def _replace_invalid_provider_tags(content: bytes) -> tuple[bytes, bool]:
    changed = False

    def replace(match: re.Match[bytes]) -> bytes:
        nonlocal changed
        changed = True
        tag = match.group("tag")
        provider = tag[:-2]
        value = match.group("value")
        return b'<uniqueid type="' + provider + b'">' + value + b"</uniqueid>"

    return _INVALID_PROVIDER_TAG.sub(replace, content), changed


def audit_nfo_bytes(content: bytes, path: Path | None = None) -> NfoAuditResult:
    issues: list[NfoIssue] = []
    try:
        root = etree.fromstring(content, _strict_parser())
    except (etree.XMLSyntaxError, ValueError) as error:
        repaired, repairable = _replace_invalid_provider_tags(content)
        if repairable:
            try:
                etree.fromstring(repaired, _strict_parser())
            except (etree.XMLSyntaxError, ValueError):
                repairable = False
        issues.append(NfoIssue("xml_parse_error", f"XML 无法解析：{error}", "error", repairable))
        return NfoAuditResult(path, tuple(issues))

    if root.tag != "movie":
        issues.append(NfoIssue("invalid_root", f"根节点应为 <movie>，当前为 <{root.tag}>", "error"))

    title = root.findtext("title", "").strip()
    if not title:
        issues.append(NfoIssue("missing_title", "缺少非空的 <title>，Jellyfin 无法正确显示标题", "error"))

    if not root.findtext("num", "").strip():
        issues.append(NfoIssue("missing_number", "缺少 MDCx 使用的 <num> 番号字段"))

    actors = root.findall("actor")
    if not actors:
        issues.append(NfoIssue("missing_actor", "没有演员节点；若爬虫已获取演员，这通常表示 NFO 生成不完整"))
    for index, actor in enumerate(actors, start=1):
        name = actor.findtext("name", "").strip()
        direct_name = (actor.text or "").strip()
        if not name:
            issues.append(
                NfoIssue(
                    "invalid_actor",
                    f"第 {index} 个 <actor> 缺少非空的 <name> 子节点",
                    "error",
                    bool(direct_name),
                )
            )

    for unique_id in root.findall("uniqueid"):
        if not (unique_id.get("type") or "").strip() or not (unique_id.text or "").strip():
            issues.append(NfoIssue("invalid_uniqueid", "<uniqueid> 必须同时包含 type 属性和非空值", "error"))

    return NfoAuditResult(path, tuple(issues))


def audit_nfo_file(path: Path) -> NfoAuditResult:
    try:
        return audit_nfo_bytes(path.read_bytes(), path)
    except OSError as error:
        return NfoAuditResult(path, (NfoIssue("read_error", f"无法读取 NFO：{error}", "error"),))


def repair_nfo_bytes(content: bytes) -> tuple[bytes, tuple[str, ...]]:
    content, provider_tags_changed = _replace_invalid_provider_tags(content)
    repairs: list[str] = []
    if provider_tags_changed:
        repairs.append("已将非法来源标签转换为 <uniqueid type=\"...\">")

    try:
        root = etree.fromstring(content, _strict_parser())
    except (etree.XMLSyntaxError, ValueError):
        return content, tuple(repairs)

    actor_changed = False
    for actor in root.findall("actor"):
        if actor.find("name") is not None:
            continue
        direct_name = (actor.text or "").strip()
        if direct_name:
            actor.text = None
            name = etree.Element("name")
            name.text = direct_name
            actor.insert(0, name)
            actor_changed = True
    if actor_changed:
        repairs.append("已将演员文本转换为 Jellyfin 识别的 <actor><name>...</name></actor>")

    if not repairs:
        return content, ()
    repaired = etree.tostring(root, encoding="UTF-8", xml_declaration=True, pretty_print=True)
    return repaired, tuple(repairs)


def repair_nfo_file(path: Path) -> tuple[NfoAuditResult, tuple[str, ...], Path | None]:
    original = path.read_bytes()
    repaired, repairs = repair_nfo_bytes(original)
    if not repairs or repaired == original:
        return audit_nfo_bytes(original, path), repairs, None

    result = audit_nfo_bytes(repaired, path)
    if not result.valid:
        return result, repairs, None

    backup = path.with_suffix(path.suffix + ".bak")
    if not backup.exists():
        shutil.copy2(path, backup)
    with tempfile.NamedTemporaryFile(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent, delete=False) as file:
        temp_path = Path(file.name)
        file.write(repaired)
    temp_path.replace(path)
    return result, repairs, backup
