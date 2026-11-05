"""Read-only regional proof snapshot. Run as root on an owned service host.

The existing process credential is consumed through curl's stdin, never printed.
"""

import datetime
import json
import pathlib
import subprocess


pids = subprocess.check_output(["pgrep", "-x", "terrabase"], text=True).split()
if len(pids) != 1:
    raise RuntimeError("expected one serving TerraBase process")
root = pathlib.Path("/proc") / pids[0]
environment = dict(item.split(b"=", 1) for item in (root / "environ").read_bytes().split(b"\0") if b"=" in item)
node = environment[b"TS_NODE"].decode()
peers = root / "root" / environment[b"TS_PEERS_FILE"].decode().lstrip("/")
address = next(parts[2] for line in peers.read_text().splitlines()
               if len(parts := line.split()) == 3 and parts[0] == node)
if b"TERRABASE_PEER_SECRET_FILE" in environment:
    secret_file = root / "root" / environment[b"TERRABASE_PEER_SECRET_FILE"].decode().lstrip("/")
    credential = secret_file.read_text().rstrip("\r\n")
else:
    credential = environment[b"TERRABASE_PEER_SECRET"].decode()
if len(credential) != 64 or any(c not in "0123456789abcdefABCDEF" for c in credential):
    raise RuntimeError("invalid existing internal credential")
config = f'url = "http://{address}:8081/healthz"\nheader = "X-TerraBase-Peer-Credential: {credential}"\n'
response = subprocess.run(["curl", "--http2-prior-knowledge", "--silent", "--show-error", "--fail", "--config", "-"],
                          input=config, text=True, capture_output=True, timeout=15)
if response.returncode:
    raise RuntimeError("internal health request failed")
print(json.dumps({"captured_at": datetime.datetime.now(datetime.timezone.utc).isoformat(), "health": json.loads(response.stdout)}))
