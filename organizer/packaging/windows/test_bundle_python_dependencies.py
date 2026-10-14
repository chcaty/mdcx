"""Offline packaging regressions; all wheels here are generated inert ZIP fixtures."""
import copy
import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from bundle_python_dependencies import bundle, check_requirements, load_lock, verify_archive

ROOT = Path(__file__).resolve().parent


class DependencyPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.cache = self.root / "cache"
        self.cache.mkdir()
        self.lock = load_lock(ROOT / "python-dependencies.json")
        for package in self.lock["packages"]:
            self.make_wheel(package)
        self.lock_path = self.root / "dependencies.json"
        self.save_lock()
        self.destination = self.root / "site-packages"
        self.licenses = self.root / "licenses"
        guard = patch("urllib.request.OpenerDirector.open", side_effect=AssertionError("No network allowed in packaging tests"))
        guard.start()
        self.addCleanup(guard.stop)

    def save_lock(self):
        self.lock_path.write_text(json.dumps(self.lock), encoding="utf-8")

    def make_wheel(self, package, extra=None, *, pure="true", tag="py3-none-any", name=None):
        path = self.cache / package["filename"]
        metadata = f"Name: {name or package['name']}\nVersion: {package['version']}\n"
        metadata += "".join(f"Requires-Dist: {value}\n" for value in package["requiresDist"])
        wheel = f"Wheel-Version: 1.0\nRoot-Is-Purelib: {pure}\nTag: {tag}\n"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(package["metadataFile"], metadata)
            archive.writestr(package["metadataFile"].removesuffix("METADATA") + "WHEEL", wheel)
            archive.writestr(f"{package['name']}/__init__.py", "# inert synthetic fixture\n")
            for license_name in package["licenseFiles"]:
                archive.writestr(license_name, "Synthetic packaging test license. " * 8)
            if extra:
                for filename, content in extra:
                    # Passing an adversarial string to ZipInfo's constructor
                    # would sanitize it before writing on Windows (and truncate
                    # NULs on every platform). Assign after construction so both
                    # ZIP headers retain the actual adversarial name bytes.
                    entry = zipfile.ZipInfo("inert-fixture-placeholder")
                    entry.filename = filename
                    entry.orig_filename = filename
                    archive.writestr(entry, content)
        if extra:
            payload = path.read_bytes()
            with zipfile.ZipFile(path) as archive:
                originals = [entry.orig_filename for entry in archive.infolist()]
            for filename, _ in extra:
                self.assertIn(filename, originals)
                self.assertEqual(payload.count(filename.encode("utf-8")), 2,
                                 "Adversarial name must survive in local and central ZIP headers")
        package["size"] = path.stat().st_size
        package["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        return path

    def test_checked_in_requirements_match_lock(self):
        lock = load_lock(ROOT / "python-dependencies.json")
        check_requirements(lock, ROOT.parents[1] / "worker/requirements-live.txt")

    def test_requirements_hash_drift_rejected(self):
        path = self.root / "requirements.txt"
        path.write_text("httpx==0.28.1\n")
        with self.assertRaisesRegex(ValueError, "differs"):
            check_requirements(self.lock, path)

    def test_all_wheels_extract_with_metadata_licenses_and_inventory(self):
        result = bundle(self.lock_path, self.destination, self.licenses, self.cache, offline=True)
        self.assertEqual(len(result["packages"]), 7)
        self.assertEqual(result["lockSha256"], hashlib.sha256(self.lock_path.read_bytes()).hexdigest())
        for package in self.lock["packages"]:
            self.assertTrue((self.destination / package["metadataFile"]).is_file())
            for license_name in package["licenseFiles"]:
                self.assertEqual((self.destination / license_name).read_bytes(),
                                 (self.licenses / f"{package['name']}-{package['version']}" / license_name).read_bytes())
        self.assertTrue((self.licenses / "inventory.json").is_file())

    def test_bad_last_hash_leaves_nothing_extracted(self):
        self.lock["packages"][-1]["sha256"] = "0" * 64
        self.save_lock()
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            bundle(self.lock_path, self.destination, self.licenses, self.cache, offline=True)
        self.assertFalse(self.destination.exists())
        self.assertFalse(self.licenses.exists())

    def test_size_mismatch_rejected(self):
        package = self.lock["packages"][0]
        package["size"] += 1
        with self.assertRaisesRegex(ValueError, "size mismatch"):
            verify_archive(self.cache / package["filename"], package)

    def test_unreviewed_origin_or_dependency_set_rejected(self):
        for change in ("origin", "extra", "duplicate"):
            with self.subTest(change=change):
                lock = copy.deepcopy(self.lock)
                if change == "origin":
                    lock["packages"][0]["url"] = lock["packages"][0]["url"].replace("files.pythonhosted.org", "attacker.invalid")
                elif change == "extra":
                    lock["packages"].pop()
                else:
                    lock["packages"].append(copy.deepcopy(lock["packages"][0]))
                self.lock_path.write_text(json.dumps(lock))
                with self.assertRaises(ValueError):
                    load_lock(self.lock_path)

    def test_path_traversal_hooks_native_and_relocation_rejected(self):
        cases = (
            ("../outside.py", "Unsafe wheel archive path"),
            ("/outside.py", "Unsafe wheel archive path"),
            ("C:/outside.py", "Unsafe wheel archive path"),
            ("package\\bad.py", "Unsafe wheel archive path|normalized or truncated"),
            ("evil.pth", "Executable hooks and native wheel payloads"),
            ("native.pyd", "Executable hooks and native wheel payloads"),
            ("native.dll", "Executable hooks and native wheel payloads"),
            ("runtime.exe", "Executable hooks and native wheel payloads"),
            ("archive.data/scripts/tool", "Wheel relocation data"),
        )
        for filename, message in cases:
            with self.subTest(filename=filename):
                package = self.lock["packages"][0]
                path = self.make_wheel(package, [(filename, "not executable")])
                with self.assertRaisesRegex(ValueError, message):
                    verify_archive(path, package)

    def test_raw_backslash_rejected_without_name_normalization(self):
        package = self.lock["packages"][0]
        filename = "package\\bad.py"
        path = self.make_wheel(package, [(filename, "inert")])
        with patch.object(zipfile.os, "sep", "/"), patch.object(zipfile.os, "altsep", None):
            with zipfile.ZipFile(path) as archive:
                entry = next(entry for entry in archive.infolist() if entry.orig_filename == filename)
                self.assertEqual(entry.filename, filename)
                self.assertEqual(archive.read(entry), b"inert")
            with self.assertRaisesRegex(ValueError, "Unsafe wheel archive path"):
                verify_archive(path, package)

    def test_raw_backslash_rejected_after_windows_name_normalization(self):
        package = self.lock["packages"][0]
        filename = "package\\bad.py"
        path = self.make_wheel(package, [(filename, "inert")])
        # Exercise Windows' ZipInfo behavior on Linux too, rather than letting
        # the host platform decide whether the raw-name check gets coverage.
        with patch.object(zipfile.os, "sep", "\\"), patch.object(zipfile.os, "altsep", "/"):
            with zipfile.ZipFile(path) as archive:
                entry = next(entry for entry in archive.infolist() if entry.orig_filename == filename)
                self.assertEqual(entry.filename, "package/bad.py")
                self.assertEqual(archive.read(entry), b"inert")
            with self.assertRaisesRegex(ValueError, "normalized or truncated"):
                verify_archive(path, package)

    def test_nul_name_truncation_rejected_before_any_extraction(self):
        for filename in ("package/good.py\x00hidden", "\x00hidden.py", "package\\bad.py\x00.pth"):
            with self.subTest(filename=filename):
                package = self.lock["packages"][0]
                path = self.make_wheel(package, [(filename, "inert")])
                self.save_lock()
                with zipfile.ZipFile(path) as archive:
                    entry = next(entry for entry in archive.infolist() if entry.orig_filename == filename)
                    self.assertNotEqual(entry.filename, entry.orig_filename)
                    self.assertNotIn("\x00", entry.filename)
                    self.assertEqual(archive.read(entry), b"inert")
                with self.assertRaisesRegex(ValueError, "normalized or truncated"):
                    bundle(self.lock_path, self.destination, self.licenses, self.cache, offline=True)
                self.assertFalse(self.destination.exists())
                self.assertFalse(self.licenses.exists())

    def test_case_colliding_paths_rejected(self):
        package = self.lock["packages"][0]
        path = self.make_wheel(package, [(f"{package['name'].upper()}/__init__.py", "collision")])
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            verify_archive(path, package)

    def test_native_tags_and_impure_wheels_rejected(self):
        package = self.lock["packages"][0]
        for options in ({"pure": "false"}, {"tag": "cp313-cp313-win_amd64"}):
            with self.subTest(options=options):
                path = self.make_wheel(package, **options)
                with self.assertRaisesRegex(ValueError, "pure universal"):
                    verify_archive(path, package)

    def test_metadata_identity_and_license_mismatch_rejected(self):
        package = self.lock["packages"][0]
        path = self.make_wheel(package, name="unreviewed")
        with self.assertRaisesRegex(ValueError, "identity"):
            verify_archive(path, package)
        self.make_wheel(package)
        package["licenseFiles"] = ["missing-license.txt"]
        with self.assertRaisesRegex(ValueError, "license"):
            verify_archive(path, package)

    def test_nonempty_destination_refused(self):
        self.destination.mkdir()
        sentinel = self.destination / "user-owned.txt"
        sentinel.write_text("untouched")
        with self.assertRaisesRegex(ValueError, "empty"):
            bundle(self.lock_path, self.destination, self.licenses, self.cache, offline=True)
        self.assertEqual(sentinel.read_text(), "untouched")

    def test_offline_missing_wheel_refused(self):
        (self.cache / self.lock["packages"][0]["filename"]).unlink()
        with self.assertRaisesRegex(ValueError, "Offline wheel missing"):
            bundle(self.lock_path, self.destination, self.licenses, self.cache, offline=True)
        self.assertFalse(self.destination.exists())

    def test_package_collision_rejected_before_extraction(self):
        self.make_wheel(self.lock["packages"][0], [("shared/collision.py", "one")])
        self.make_wheel(self.lock["packages"][1], [("shared/collision.py", "two")])
        self.save_lock()
        with self.assertRaisesRegex(ValueError, "collide"):
            bundle(self.lock_path, self.destination, self.licenses, self.cache, offline=True)
        self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    unittest.main()
