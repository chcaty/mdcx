"""Build-time only: verify and unpack reviewed pure wheels without installing pip.

Not shipped as executable runtime code. A lock edit is required to change a
version, URL, hash, license, or wheel layout. All archive bytes are verified
before any package is extracted; wheel setup hooks and entrypoints never run.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import re
import shutil
import stat
import urllib.request
import zipfile
from email.parser import BytesParser
from pathlib import Path, PurePosixPath
from urllib.parse import urlsplit

MAX_ARCHIVE_BYTES = 10 * 1024 * 1024
MAX_UNPACKED_BYTES = 30 * 1024 * 1024


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def load_lock(path: Path) -> dict:
    lock = json.loads(path.read_text(encoding="utf-8"))
    require(lock["schemaVersion"] == 1 and lock["targetPython"] == "3.13", "Unsupported dependency lock")
    names = set()
    for package in lock["packages"]:
        name, version = package["name"], package["version"]
        require(bool(re.fullmatch(r"[a-z][a-z0-9_]*", name)), "Invalid package name")
        require(bool(re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", version)), "Invalid package version")
        require(name not in names, "Duplicate dependency")
        names.add(name)
        filename = f"{name}-{version}-py3-none-any.whl"
        require(package["filename"] == filename, "Only reviewed py3-none-any wheels are allowed")
        url = urlsplit(package["url"])
        require(url.scheme == "https" and url.netloc == "files.pythonhosted.org"
                and url.path.startswith("/packages/") and url.path.endswith("/" + filename)
                and not url.query and not url.fragment, "Only exact official PyPI wheel URLs are allowed")
        require(bool(re.fullmatch(r"[0-9a-f]{64}", package["sha256"])), "Invalid wheel hash")
        require(type(package["size"]) is int and 0 < package["size"] <= MAX_ARCHIVE_BYTES, "Invalid wheel size")
        require(package["licenseFiles"] and package["metadataFile"].endswith(".dist-info/METADATA"), "Missing wheel provenance")
    require(names == {"httpx", "httpcore", "anyio", "h11", "idna", "certifi", "typing_extensions"},
            "Dependency closure differs from the reviewed HTTP/1 runtime")
    return lock


def check_requirements(lock: dict, path: Path) -> None:
    expected = [f"{p['name']} @ {p['url']} --hash=sha256:{p['sha256']}" for p in lock["packages"]]
    content = path.read_text(encoding="utf-8").replace("\\\n", " ")
    actual = [" ".join(line.split()) for line in content.splitlines() if line.strip() and not line.startswith("#")]
    require(actual == expected, "requirements-live.txt differs from the reviewed wheel lock")


def verify_archive(path: Path, package: dict) -> list[str]:
    require(path.stat().st_size == package["size"], "Wheel size mismatch")
    require(hashlib.sha256(path.read_bytes()).hexdigest() == package["sha256"], "Wheel SHA-256 mismatch")
    with zipfile.ZipFile(path) as archive:
        files = []
        names = set()
        total = 0
        for entry in archive.infolist():
            name = entry.filename
            components = PurePosixPath(name).parts
            require(name and not name.startswith("/") and "\\" not in name and ":" not in name
                    and ".." not in components and "." not in name.split("/"), "Unsafe wheel archive path")
            require(not stat.S_ISLNK(entry.external_attr >> 16), "Wheel symlinks are not allowed")
            require(not any(part.endswith(".data") for part in components), "Wheel relocation data is not supported")
            require(not name.lower().endswith((".pth", ".pyd", ".dll", ".exe", ".so", ".pyc")),
                    "Executable hooks and native wheel payloads are not supported")
            if entry.is_dir():
                continue
            require(name.casefold() not in names, "Duplicate wheel archive path")
            names.add(name.casefold())
            total += entry.file_size
            require(total <= MAX_UNPACKED_BYTES, "Wheel uncompressed-size limit exceeded")
            files.append(name)
        metadata_name = package["metadataFile"]
        wheel_name = metadata_name.removesuffix("METADATA") + "WHEEL"
        require(metadata_name in files and wheel_name in files, "Wheel metadata missing")
        metadata = BytesParser().parsebytes(archive.read(metadata_name))
        normalized = metadata["Name"].lower().replace("-", "_")
        require(normalized == package["name"] and metadata["Version"] == package["version"], "Wheel identity mismatch")
        require(sorted(metadata.get_all("Requires-Dist", [])) == sorted(package["requiresDist"]), "Wheel dependency metadata mismatch")
        wheel = BytesParser().parsebytes(archive.read(wheel_name))
        require(wheel["Root-Is-Purelib"].lower() == "true" and wheel.get_all("Tag") == ["py3-none-any"],
                "Wheel is not a pure universal Python package")
        for license_name in package["licenseFiles"]:
            require(license_name in files and len(archive.read(license_name)) > 100, "Declared license missing or empty")
        return files


class OfficialWheelRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file_pointer, code, message, headers, newurl):
        # Reject all redirects: every approved URL already names an immutable blob.
        raise ValueError("Official wheel download unexpectedly redirected")


def bundle(lock_path: Path, destination: Path, licenses: Path, cache: Path, *, offline: bool = False) -> dict:
    lock = load_lock(lock_path)
    require(not destination.exists() or not any(destination.iterdir()), "Dependency destination must be empty")
    require(not destination.is_symlink() and not licenses.is_symlink() and not cache.is_symlink(), "Symlink destination refused")
    cache.mkdir(parents=True, exist_ok=True)
    verified = []
    all_paths = set()
    for package in lock["packages"]:
        path = cache / package["filename"]
        if not path.exists():
            require(not offline, f"Offline wheel missing: {package['filename']}")
            opener = urllib.request.build_opener(OfficialWheelRedirects())
            with opener.open(package["url"], timeout=60) as response:
                content = response.read(MAX_ARCHIVE_BYTES + 1)
            require(len(content) <= MAX_ARCHIVE_BYTES, "Wheel download exceeds size limit")
            path.write_bytes(content)
        files = verify_archive(path, package)
        require(not all_paths.intersection(name.casefold() for name in files), "Package paths collide")
        all_paths.update(name.casefold() for name in files)
        verified.append((package, path, files))
    # No extraction until the complete closure has passed hashes and layout checks.
    destination.mkdir(parents=True, exist_ok=True)
    licenses.mkdir(parents=True, exist_ok=True)
    inventory = []
    for package, path, files in verified:
        with zipfile.ZipFile(path) as archive:
            for name in files:
                target = destination / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(name))
        package_license = licenses / f"{package['name']}-{package['version']}"
        for name in [package["metadataFile"], *package["licenseFiles"]]:
            target = package_license / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(destination / name, target)
        inventory.append({**package, "installedFiles": files})
    manifest = {"schemaVersion": 1, "targetPython": lock["targetPython"],
                "lockSha256": hashlib.sha256(lock_path.read_bytes()).hexdigest(), "packages": inventory}
    (licenses / "inventory.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    shutil.copyfile(lock_path, licenses / "python-dependencies.json")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=Path(__file__).with_name("python-dependencies.json"))
    parser.add_argument("--requirements", type=Path)
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--check-installed", action="store_true")
    parser.add_argument("--destination", type=Path)
    parser.add_argument("--licenses", type=Path)
    parser.add_argument("--cache", type=Path)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    lock = load_lock(args.lock)
    if args.requirements:
        check_requirements(lock, args.requirements)
    if args.check_installed:
        for package in lock["packages"]:
            try:
                version = importlib.metadata.version(package["name"])
            except importlib.metadata.PackageNotFoundError:
                version = None
            require(version == package["version"],
                    f"Missing or mismatched {package['name']}; install worker/requirements-live.txt with --require-hashes --only-binary=:all: --no-deps")
    if not args.check_only:
        require(all((args.destination, args.licenses, args.cache)), "Destination, licenses, and cache are required")
        manifest = bundle(args.lock, args.destination, args.licenses, args.cache, offline=args.offline)
        print(f"Verified and bundled {len(manifest['packages'])} official PyPI wheels")
    else:
        print("Dependency lock and requirements verified")


if __name__ == "__main__":
    main()
