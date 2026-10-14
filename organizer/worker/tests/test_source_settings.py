import json
import math
import sys
import tempfile
import unittest
from dataclasses import FrozenInstanceError, replace
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mdcx_worker.models import MAX_BODY_BYTES, Request, WorkerError
from mdcx_worker.source_settings import (
    JAVBUS_ORIGIN,
    MAX_CONFIG_BYTES,
    SourceSettings,
    javbus_detail_identity,
    load_source_settings,
    validate_javbus_origin,
    validate_javbus_request,
)


class SourceSettingsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "metadata.json"
        for name in ("socket.getaddrinfo", "socket.create_connection", "socket.socket.connect", "socket.socket.connect_ex"):
            guard = patch(name, side_effect=AssertionError("No networking is permitted in configuration tests"))
            guard.start()
            self.addCleanup(guard.stop)

    def load(self, content):
        self.path.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))
        return load_source_settings(self.path)

    def test_defaults_are_disabled_and_immutable(self):
        settings = SourceSettings()
        self.assertFalse(settings.network_enabled)
        self.assertEqual(settings.origin, JAVBUS_ORIGIN)
        self.assertIsNone(settings.proxy)
        with self.assertRaises(FrozenInstanceError):
            settings.network_enabled = True
        self.assertEqual(self.load("{}"), settings)

    def test_explicit_json_values_and_scheduler_mapping(self):
        settings = self.load(json.dumps({
            "network_enabled": True, "source": "javbus", "origin": JAVBUS_ORIGIN,
            "connect_timeout_seconds": 1, "read_timeout_seconds": 2,
            "pool_timeout_seconds": 3, "request_timeout_seconds": 4,
            "attempts": 1, "global_limit": 2, "per_host_limit": 1,
            "min_interval_seconds": 0.5, "max_retry_delay_seconds": 8,
            "max_body_bytes": 1024,
        }))
        self.assertTrue(settings.network_enabled)
        self.assertEqual(settings.scheduler_kwargs(), {
            "global_limit": 2, "per_host_limit": 1, "attempts": 1,
            "timeout": 4, "min_interval": 0.5, "max_retry_delay": 8,
        })
        settings.scheduler_kwargs()["attempts"] = 99
        self.assertEqual(settings.attempts, 1)

    def test_environment_cannot_enable_network_change_origin_or_add_credentials(self):
        with patch.dict("os.environ", {
            "MDCX_NETWORK_ENABLED": "true", "JAVBUS_ORIGIN": "https://evil.invalid",
            "HTTP_PROXY": "http://secret:password@evil.invalid:80", "NETRC": "/secret/file",
        }):
            self.assertEqual(self.load("{}"), SourceSettings())

    def test_types_bounds_nonfinite_values_and_foreign_origins_fail_closed(self):
        invalid = [
            ("network_enabled", 1), ("network_enabled", "true"), ("network_enabled", None),
            ("source", "xcity"), ("source", None), ("origin", "https://javbus.com"),
            ("origin", "http://www.javbus.com"), ("origin", JAVBUS_ORIGIN + "/"),
            ("origin", "https://www.javbus.com:443"), ("origin", "https://127.0.0.1"),
            ("connect_timeout_seconds", 0), ("connect_timeout_seconds", 61),
            ("read_timeout_seconds", math.nan), ("pool_timeout_seconds", math.inf),
            ("request_timeout_seconds", -math.inf), ("request_timeout_seconds", "15"),
            ("attempts", 0), ("attempts", 4), ("attempts", True), ("attempts", 1.0),
            ("global_limit", 5), ("global_limit", []), ("per_host_limit", 3),
            ("max_body_bytes", 0), ("max_body_bytes", MAX_BODY_BYTES + 1), ("max_body_bytes", True),
            ("min_interval_seconds", 0), ("min_interval_seconds", 61),
            ("max_retry_delay_seconds", -1), ("max_retry_delay_seconds", 61),
            ("read_timeout_seconds", 10 ** 1000),
        ]
        for key, value in invalid:
            with self.subTest(key=key, value=str(value)[:30]):
                with self.assertRaises(WorkerError) as error:
                    SourceSettings(**{key: value})
                self.assertEqual(error.exception.code, "invalid_config")
                self.assertFalse(error.exception.retryable)
        with self.assertRaises(WorkerError):
            SourceSettings(global_limit=1, per_host_limit=2)

    def test_strict_json_and_bounded_file(self):
        invalid = [
            "[]", "null", '"config"', "true", "{", "{\"network_enabled\":true,\"network_enabled\":false}",
            '{"headers":{"Authorization":"Bearer secret"}}', '{"credential_ref":"secret"}',
            '{"cookies": "secret"}', '{"verify":false}', '{"follow_redirects":true}',
            '{"attempts":NaN}', '{"read_timeout_seconds":Infinity}', '{"unknown":1}',
            b'\xff', b'\xef\xbb\xbf{}', b" " * MAX_CONFIG_BYTES + b"{}",
            "[" * 1500 + "]" * 1500,
        ]
        for value in invalid:
            with self.subTest(value=str(value)[:45]):
                with self.assertRaises(WorkerError) as error:
                    self.load(value)
                self.assertEqual(error.exception.code, "invalid_config")
                self.assertEqual(str(error.exception), "Metadata configuration is invalid")

    def test_missing_file_is_safe_and_does_not_disclose_path(self):
        with self.assertRaises(WorkerError) as error:
            load_source_settings(self.path.parent / "private-secret-filename.json")
        self.assertEqual(error.exception.code, "invalid_config")
        self.assertNotIn("private", str(error.exception))

    def test_credential_free_explicit_proxy_only(self):
        for proxy in ("http://127.0.0.1:8080", "http://localhost:8080/", "https://proxy.example:8443", "http://[::1]:8080"):
            with self.subTest(proxy=proxy):
                settings = SourceSettings(proxy=proxy)
                self.assertEqual(settings.proxy, proxy)
                self.assertNotIn(proxy, repr(settings))
        for proxy in (
            "http://user:secret@proxy.example:80", "http://@proxy.example:80", "http://user@proxy.example:80",
            "http://proxy.example", "http://proxy.example:0", "http://proxy.example:65536",
            "socks5://proxy.example:1080", "file:///tmp/proxy", "http://proxy.example:80/path",
            "http://proxy.example:80?q=secret", "http://proxy.example:80#secret", "http://proxy.example:80?",
            "http://proxy.example:80#", "http://proxy.example:80\n", "http://proxy%2eexample:80", 12, {},
        ):
            with self.subTest(proxy=proxy):
                with self.assertRaises(WorkerError) as error:
                    SourceSettings(proxy=proxy)
                self.assertEqual(str(error.exception), "Metadata configuration is invalid")


class SourceRoutePolicyTests(unittest.TestCase):
    def test_exact_origin_and_bounded_detail_identity(self):
        self.assertEqual(validate_javbus_origin(JAVBUS_ORIGIN), JAVBUS_ORIGIN)
        for token in ("DEMO-001", "demo001", "DEMO_001a", "BF-658"):
            request = Request("javbus", JAVBUS_ORIGIN + "/" + token)
            self.assertEqual(validate_javbus_request(request), ("detail", token))
            self.assertEqual(javbus_detail_identity(request.url).value, token)
            self.assertEqual(javbus_detail_identity(request.url).source, "javbus")
        self.assertEqual(validate_javbus_request(Request("javbus", JAVBUS_ORIGIN + "/search/DEMO-001&type=&parent=ce")),
                         ("search", "DEMO-001"))

    def test_no_arbitrary_url_or_route_can_reach_transport(self):
        bad_urls = [
            "http://www.javbus.com/DEMO-001", "https://127.0.0.1/DEMO-001",
            "https://localhost/DEMO-001", "https://169.254.169.254/DEMO-001",
            "https://www.javbus.com.evil.invalid/DEMO-001", "https://evil.invalid/DEMO-001",
            "https://evil.invalid@www.javbus.com/DEMO-001", "https://www.javbus.com:443/DEMO-001",
            "https://www.javbus.com./DEMO-001", "https://WWW.JAVBUS.COM/DEMO-001",
            JAVBUS_ORIGIN + "/", JAVBUS_ORIGIN + "/DEMO-001?x=secret", JAVBUS_ORIGIN + "/DEMO-001#secret",
            JAVBUS_ORIGIN + "/DEMO-001?", JAVBUS_ORIGIN + "/DEMO-001#", JAVBUS_ORIGIN + "/DEMO-001/",
            JAVBUS_ORIGIN + "/DEMO%2d001", JAVBUS_ORIGIN + "/../DEMO-001", JAVBUS_ORIGIN + "/foo/DEMO-001",
            JAVBUS_ORIGIN + "//DEMO-001", JAVBUS_ORIGIN + "/DEMO-001\n", JAVBUS_ORIGIN + "/DEMO-001\\x",
            JAVBUS_ORIGIN + "/search/DEMO-001", JAVBUS_ORIGIN + "/search/DEMO-001?type=&parent=ce",
            JAVBUS_ORIGIN + "/search/DEMO-001&type=&parent=uc", JAVBUS_ORIGIN + "/search/demo001&type=&parent=ce",
            JAVBUS_ORIGIN + "/DEMO-" + "1" * 13, JAVBUS_ORIGIN + "/" + "A" * 13 + "-001", None,
        ]
        for url in bad_urls:
            with self.subTest(url=url):
                with self.assertRaises(WorkerError) as error:
                    validate_javbus_request(Request("javbus", url))
                self.assertEqual(error.exception.code, "policy_blocked")
                self.assertNotIn("secret", str(error.exception))

    def test_headers_auth_source_and_method_cannot_override_policy(self):
        request = Request("javbus", JAVBUS_ORIGIN + "/DEMO-001")
        for change in (
            {"source": "xcity"}, {"method": "POST"}, {"method": "HEAD"},
            {"credential_ref": "private-secret"}, {"headers": (("Cookie", "private-secret"),)},
            {"headers": (("Authorization", "Bearer private-secret"),)},
            {"headers": (("User-Agent", "browser impersonation"),)},
        ):
            with self.subTest(change=change):
                with self.assertRaises(WorkerError) as error:
                    validate_javbus_request(replace(request, **change))
                self.assertEqual(error.exception.code, "policy_blocked")
                self.assertNotIn("private-secret", str(error.exception))


if __name__ == "__main__":
    unittest.main()
