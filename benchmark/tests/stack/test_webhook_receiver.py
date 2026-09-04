#!/usr/bin/env python3
"""Static contract checks for the no-dependency E-15 webhook receiver.

These tests deliberately do not bind a port or launch a service. Runtime
delivery is exercised by the real stack, while this gate catches accidental
route/response regressions in config-only CI.
"""

from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "scripts" / "stack-webhook-receiver.py").read_text(encoding="utf-8")


def test_receiver_is_loopback_only_and_stdlib() -> None:
    assert 'ThreadingHTTPServer(("127.0.0.1", args.port), Receiver)' in SOURCE
    assert "from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer" in SOURCE
    assert "import requests" not in SOURCE


def test_receiver_health_and_delivery_contract() -> None:
    assert 'if self.path == "/health":' in SOURCE
    assert 'self._send_json(200, {"ok": True})' in SOURCE
    assert 'if not self.path.startswith("/hooks"):' in SOURCE
    assert 'self._send_json(200, {"received": True})' in SOURCE
    assert 'handle.write(json.dumps(record) + "\\n")' in SOURCE


if __name__ == "__main__":
    test_receiver_is_loopback_only_and_stdlib()
    test_receiver_health_and_delivery_contract()
    print("webhook receiver config tests passed")
