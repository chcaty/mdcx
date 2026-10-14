"""Offline bundled-runtime smoke. Every HTTP response comes from MockTransport."""
from __future__ import annotations

import argparse
import asyncio
import importlib
import importlib.metadata
import json
import os
import socket
import sys
from contextlib import ExitStack
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch


def verify(package_root: Path, *, require_isolated: bool = True) -> dict:
    expected_site = (package_root / "python/Lib/site-packages").resolve()
    lock = json.loads((package_root / "licenses/python/python-dependencies.json").read_text(encoding="utf-8"))
    if require_isolated:
        assert sys.flags.isolated == 1 and "site" not in sys.modules, "Bundled Python isolation disabled"
    modules = {"httpx": "httpx", "httpcore": "httpcore", "anyio": "anyio", "h11": "h11", "idna": "idna",
               "certifi": "certifi", "typing_extensions": "typing_extensions"}
    for package in lock["packages"]:
        assert importlib.metadata.version(package["name"]) == package["version"], "Wrong bundled dependency version"
        module = importlib.import_module(modules[package["name"]])
        assert Path(module.__file__).resolve().is_relative_to(expected_site), "Dependency escaped bundled site path"
    import httpx
    import certifi
    from mdcx_worker.javbus_provider import JavBusProvider
    from mdcx_worker.models import Request, WorkerError
    from mdcx_worker.runtime import build_runtime
    from mdcx_worker.source_settings import SourceSettings, load_source_settings
    assert Path(certifi.where()).resolve().is_relative_to(expected_site), "CA bundle escaped bundled site path"
    assert Path(certifi.where()).stat().st_size > 10_000, "CA bundle missing"
    sample = load_source_settings(package_root / "source-settings.example.json")
    assert sample == SourceSettings() and sample.network_enabled is False, "Source sample must remain disabled"
    hits = []

    def respond(request):
        assert request.method == "GET" and str(request.url) == "https://www.javbus.com/DEMO-001"
        hits.append(str(request.url))
        body = (package_root / "worker/fixtures/javbus/detail-demo-001.html").read_bytes()
        return httpx.Response(200, content=body, headers={"Content-Type": "text/html; charset=utf-8"})

    async def smoke():
        disabled = build_runtime(sample, http_backend=httpx.MockTransport(respond))
        assert isinstance(disabled.matcher, JavBusProvider) and disabled.sources == ("javbus",)
        try:
            await disabled.transport.fetch(Request("javbus", "https://www.javbus.com/DEMO-001"))
            raise AssertionError("Disabled metadata network unexpectedly accepted a request")
        except WorkerError as error:
            assert error.code == "live_unavailable"
        finally:
            await disabled.aclose()
        assert hits == [] and disabled.transport._client is None
        runtime = build_runtime(replace(sample, network_enabled=True), http_backend=httpx.MockTransport(respond))
        try:
            candidates = await runtime.matcher.match("DEMO-001", ("javbus",))
            assert len(candidates) == 1 and candidates[0].id == "DEMO-001"
            assert candidates[0].source == "javbus" and candidates[0].provenance["fixture"] == "true"
            assert candidates[0].provenance["requiresReview"] == "true"
            assert hits == ["https://www.javbus.com/DEMO-001"]
        finally:
            await runtime.aclose()
    # Initialize Windows' event-loop wakeup mechanism before installing egress
    # guards. All source activity below must use the in-process mock transport.
    with asyncio.Runner() as runner:
        runner.get_loop()
        with ExitStack() as guards:
            guards.enter_context(patch.dict(os.environ, {
                "HTTP_PROXY": "http://127.0.0.1:1", "HTTPS_PROXY": "http://127.0.0.1:1",
                "ALL_PROXY": "http://127.0.0.1:1", "NO_PROXY": "",
                "http_proxy": "http://127.0.0.1:1", "https_proxy": "http://127.0.0.1:1",
                "all_proxy": "http://127.0.0.1:1", "no_proxy": "",
                "SSL_CERT_FILE": str(package_root / "must-not-read-ambient-certs.pem"),
                "SSL_CERT_DIR": str(package_root / "must-not-read-ambient-certs"),
            }))
            for target in ("getaddrinfo", "create_connection"):
                guards.enter_context(patch.object(socket, target, side_effect=AssertionError("Network forbidden in package smoke")))
            for target in ("connect", "connect_ex"):
                guards.enter_context(patch.object(socket.socket, target, side_effect=AssertionError("Network forbidden in package smoke")))
            runner.run(smoke())
    return {"schemaVersion": 1, "status": "passed", "httpxVersion": httpx.__version__,
            "dependenciesBundled": True, "sourceConfigDisabled": True, "javbusWired": True,
            "mockMetadataMatched": True, "environmentIgnored": True, "sourceNetworkRequests": 0, "mockRequests": len(hits)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = verify(args.package_root)
    text = json.dumps(result, indent=2) + "\n"
    if args.report:
        args.report.write_text(text, encoding="utf-8")
    print(text, end="")
