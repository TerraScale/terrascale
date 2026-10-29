#!/usr/bin/env bash
set -euo pipefail
[ "$#" -eq 2 ] || { echo 'usage: smoke.sh <container-engine> <qualified-image>' >&2; exit 2; }
python3 - "$1" "$2" <<'PYTHON'
import base64
import json
from pathlib import Path
import queue
import secrets
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

engine, image = sys.argv[1:]
containers = []
followers = []
http = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def command(*arguments, timeout=60):
    return subprocess.run([engine, *arguments], check=True, text=True, capture_output=True, timeout=timeout).stdout.strip()


def start(identity=None):
    arguments = ["run", "--detach", "--publish", "127.0.0.1::8080",
                 "--env", "ASPNETCORE_ENVIRONMENT=Production", "--env", "DOTNET_ENVIRONMENT=Production",
                 "--env", "TS_REGION=gru", "--env", "TS_DASHBOARD_TERRABASE_URL=http://127.0.0.1:1",
                 "--add-host", "auth.terrascale.tech:127.0.0.1", "--add-host", "api.terrascale.tech:127.0.0.1"]
    if identity is not None:
        arguments.extend(["--env-file", str(identity)])
    container = command(*arguments, image)
    containers.append(container)
    return container


def listen(container):
    events = queue.Queue()
    follower = subprocess.Popen([engine, "logs", "--follow", container], text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    followers.append(follower)

    def read():
        for line in follower.stdout:
            events.put(line)
        events.put(None)

    threading.Thread(target=read, daemon=True).start()
    deadline = time.monotonic() + 60
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError("Production container did not announce a listening server within its startup deadline")
        line = events.get(timeout=remaining)
        if line is None:
            raise RuntimeError("Production container exited before announcing its listening server")
        if "Now listening on:" in line or "Application started." in line:
            return


def request(origin, path):
    try:
        with http.open(origin + path, timeout=10) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as response:
        return response.code, response.read()


try:
    missing = start()
    code = command("wait", missing)
    if not code.isdigit() or int(code) == 0:
        raise RuntimeError("Production image accepted startup without its required identity")
    captured = subprocess.run([engine, "logs", missing], check=True, text=True, capture_output=True, timeout=30)
    logs = captured.stdout + captured.stderr
    if "Dashboard secret TS_AUTH_DASHBOARD_CLIENT_SECRET is not set." not in logs:
        raise RuntimeError("Production image failed before reaching its required-identity check")
    with tempfile.TemporaryDirectory(prefix="dashboard-runtime-smoke-") as temporary:
        identity = Path(temporary) / "synthetic-identity.env"
        identity.touch(mode=0o600)
        identity.write_text("\n".join([
            "TS_AUTH_DASHBOARD_CLIENT_SECRET=" + secrets.token_hex(32),
            "TS_DASHBOARD_SESSION_ENCRYPTION_KEY=" + base64.b64encode(secrets.token_bytes(32)).decode(),
            "STRIPE_SECRET_KEY=sk_test_" + secrets.token_hex(24),
            "STRIPE_WEBHOOK_SECRET=whsec_" + secrets.token_hex(24),
            "TERRABASE_PEER_SECRET=" + secrets.token_hex(32),
        ]) + "\n")
        serving = start(identity)
        listen(serving)
        mapping = command("port", serving, "8080/tcp").splitlines()
        if len(mapping) != 1 or not mapping[0].startswith("127.0.0.1:"):
            raise RuntimeError("Runtime smoke requires one isolated loopback port")
        origin = "http://" + mapping[0]
        status, body = request(origin, "/health/live")
        health = json.loads(body)
        if status != 200 or health.get("status") != "ok" or health.get("region") != "gru":
            raise RuntimeError("Production liveness did not match the running application")
        status, body = request(origin, "/health/ready")
        health = json.loads(body)
        if status != 503 or health.get("status") != "unavailable" or health.get("dependencies", {}).get("terrabase") != "unavailable":
            raise RuntimeError("Production readiness did not refuse the unavailable state backend")
        status, _ = request(origin, "/")
        if status != 503:
            raise RuntimeError("Production admitted application traffic before state initialization")
    print("Qualified runtime: required identity, production liveness, unavailable-state readiness and traffic refusal.")
finally:
    for container in reversed(containers):
        subprocess.run([engine, "rm", "--force", "--volumes", container], capture_output=True, timeout=30, check=False)
    for follower in followers:
        if follower.poll() is None:
            follower.terminate()
        try:
            follower.wait(timeout=10)
        except subprocess.TimeoutExpired:
            follower.kill()
            follower.wait(timeout=10)
PYTHON
