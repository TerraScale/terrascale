#!/usr/bin/env python3
"""Verify public readiness and the deployed dashboard source revision."""
import json
import re
import sys
import urllib.request

revision = sys.argv[1]
if not re.fullmatch(r"[0-9a-f]{40}", revision):
    raise SystemExit("A full source revision is required")
url = "https://dash.terrascale.tech/readyz"
with urllib.request.urlopen(url, timeout=30) as response:
    body = json.load(response)
    deployed = response.headers.get("X-TerraScale-Revision")
    if response.status != 200 or body.get("status") != "ok":
        raise SystemExit(f"Dashboard is not ready: {body}")
    if deployed != revision:
        raise SystemExit(f"Dashboard revision mismatch: expected {revision}, received {deployed}")
    print(json.dumps({"url": url, "revision": deployed, "readiness": body}, sort_keys=True))
