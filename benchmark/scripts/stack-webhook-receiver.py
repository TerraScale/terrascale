#!/usr/bin/env python3
"""E-15 local webhook receiver (development only, never shipped).

Listens on 127.0.0.1:7090 and records every POST under /hooks as one JSON
line in the deliveries log, so the P-3 journey can prove webhook delivery
against `make stack` without any external service.

Routes:
  GET  /health -> {"ok": true}
  POST /hooks[/...] -> {"received": true}, appends one line per delivery

Usage:
  python3 scripts/stack-webhook-receiver.py [--port 7090] [--log PATH]

Only the standard library is used; there are no dependencies.
"""

import argparse
import datetime
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_BODY_BYTES = 1024 * 1024


class Receiver(BaseHTTPRequestHandler):
    log_path = ""

    def log_message(self, *args):  # keep stdout for fatal errors only
        pass

    def _send_json(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802 - http.server naming
        if self.path == "/health":
            self._send_json(200, {"ok": True})
        else:
            self._send_json(404, {"error": {"code": "not_routed"}})

    def do_POST(self):  # noqa: N802 - http.server naming
        if not self.path.startswith("/hooks"):
            self._send_json(404, {"error": {"code": "not_routed"}})
            return
        length = self.headers.get("content-length")
        try:
            size = int(length) if length else 0
        except ValueError:
            size = 0
        if size < 0 or size > MAX_BODY_BYTES:
            self._send_json(413, {"error": {"code": "body_too_large"}})
            return
        raw = self.rfile.read(size) if size else b""
        try:
            payload = json.loads(raw.decode("utf-8")) if raw else None
        except (UnicodeDecodeError, ValueError):
            payload = {"_raw_bytes": len(raw)}
        record = {
            "at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "path": self.path,
            "delivery_id": self.headers.get("x-terrascale-delivery"),
            "event": self.headers.get("x-terrascale-event"),
            "payload": payload,
        }
        with open(self.log_path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(record) + "\n")
        self._send_json(200, {"received": True})


def main():
    parser = argparse.ArgumentParser(description="E-15 local webhook receiver")
    parser.add_argument("--port", type=int, default=7090)
    parser.add_argument("--log", default="")
    args = parser.parse_args()
    log_path = args.log or os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", ".terrascale", "stack", "webhooks.log"
    )
    os.makedirs(os.path.dirname(os.path.abspath(log_path)), exist_ok=True)
    Receiver.log_path = os.path.abspath(log_path)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Receiver)
    print(f"stack webhooks on 127.0.0.1:{args.port} -> {Receiver.log_path}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
