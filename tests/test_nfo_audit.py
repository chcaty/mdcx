from pathlib import Path

from lxml import etree

from mdcx.tools.nfo_audit import audit_nfo_bytes, audit_nfo_file, repair_nfo_bytes, repair_nfo_file


def test_valid_jellyfin_nfo_passes_audit():
    content = b"""<?xml version="1.0" encoding="UTF-8"?>
<movie><title>SNOS-157 title</title><num>SNOS-157</num>
<actor><name>Actor A</name><type>Actor</type></actor>
<uniqueid type="javbus">SNOS-157</uniqueid></movie>"""

    result = audit_nfo_bytes(content)

    assert result.valid
    assert result.issues == ()


def test_audit_detects_missing_title_and_invalid_actor():
    result = audit_nfo_bytes(b"<movie><num>SNOS-157</num><actor /></movie>")

    assert not result.valid
    assert {issue.code for issue in result.issues} == {"missing_title", "invalid_actor"}


def test_repair_converts_legacy_provider_tag_and_direct_actor_text():
    content = b"""<movie><title>Title</title><num>SNOS-157</num>
<actor>Actor A</actor><7mmtvid>abc-123</7mmtvid></movie>"""

    before = audit_nfo_bytes(content)
    repaired, repairs = repair_nfo_bytes(content)
    after = audit_nfo_bytes(repaired)
    root = etree.fromstring(repaired)

    assert not before.valid
    assert before.fixable
    assert len(repairs) == 2
    assert after.valid
    assert root.findtext("actor/name") == "Actor A"
    assert root.xpath('string(uniqueid[@type="7mmtv"])') == "abc-123"


def test_file_repair_creates_backup(tmp_path: Path):
    path = tmp_path / "movie.nfo"
    original = b"<movie><title>Title</title><num>ABC-001</num><actor>Actor A</actor></movie>"
    path.write_bytes(original)

    result, repairs, backup = repair_nfo_file(path)

    assert result.valid
    assert repairs
    assert backup == tmp_path / "movie.nfo.bak"
    assert backup.read_bytes() == original
    assert audit_nfo_file(path).valid
