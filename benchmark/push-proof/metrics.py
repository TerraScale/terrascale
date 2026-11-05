"""Snapshot push lag on a serving host: ssh ts-sjc-1 python3 - < metrics.py.

The existing runtime peer credential stays on the host and reaches curl on stdin.
"""

import datetime
import json
import subprocess


def ctr(*args):
    return subprocess.check_output(["sudo", "-n", "ctr", "-n", "terrascale", *args])


def read(path):
    return subprocess.check_output(["sudo", "-n", "cat", path])


tasks = [line.split() for line in ctr("tasks", "list").decode().splitlines()[1:]]
running = {row[0]: row[1] for row in tasks if row[2] == "RUNNING"}
containers = [line.split() for line in ctr("containers", "list").decode().splitlines()[1:]]
container, image = next((row[0], row[1]) for row in containers
                        if row[0] in running and row[1].startswith("ghcr.io/terrascale/terrabase@"))
pid = running[container]
environment = dict(entry.split(b"=", 1) for entry in read(f"/proc/{pid}/environ").split(b"\0") if b"=" in entry)
root = f"/proc/{pid}/root"
credential = read(root + environment[b"TERRABASE_PEER_SECRET_FILE"].decode()).decode().strip()
peers = [line.split() for line in read(root + environment[b"TS_PEERS_FILE"].decode()).decode().splitlines()]
address = next(row[2] for row in peers if row[0] == environment[b"TS_NODE"].decode())
config = f'header = "X-TerraBase-Peer-Credential: {credential}"\n'


def get(path):
    return subprocess.check_output(["curl", "--http2-prior-knowledge", "-fsS", "--max-time", "10",
                                    "-K", "-", f"http://{address}:8081/{path}"], input=config.encode())


health = json.loads(get("healthz"))
lines = [line for line in get("metrics").decode().splitlines() if line.startswith("terrabase_push_")]
print(json.dumps({"captured_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  "image": image, "health": health, "metrics": lines}, indent=2))
