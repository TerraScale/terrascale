#!/usr/bin/env python3
"""Set only a synthetic benchmark organization's plan through authenticated NetBird administration."""
import argparse
from pathlib import Path
import re
import subprocess
import sys

# Runs on the admitted service host. The peer credential stays on that host and never enters argv or stdout.
REMOTE = r'''
import json
import os
import subprocess
import sys
import tempfile

def read(args):
    return subprocess.check_output(args)

def refusal(result):
    if result.returncode:
        return 'transport (curl_exit ' + str(result.returncode) + ')'
    body, _, status = result.stdout.rpartition(b'\n')
    if status == b'200':
        return None
    status = status.decode('ascii') if len(status) == 3 and status.isdigit() and 100 <= int(status) <= 599 else 'unclassified'
    code = None
    try:
        value = json.loads(body)
        error = value.get('error') if isinstance(value, dict) else None
        code = error.get('code') if isinstance(error, dict) else error
    except (ValueError, TypeError):
        pass
    known = {'invalid_request', 'invalid_key', 'not_found', 'forbidden_scope', 'storage_busy', 'catalog_stale',
             'confirmation_busy', 'disk_capacity', 'copy_faulted', 'capacity', 'remote_region_unavailable',
             'auth_unavailable', 'internal_error', 'unavailable'}
    code = code if isinstance(code, str) and code in known else 'other'
    return 'HTTP ' + status + ' (' + code + ')'

organization = sys.argv[1]
tasks = {row.split()[0]: row.split()[1] for row in read(["sudo", "-n", "ctr", "-n", "terrascale", "tasks", "ls"]).decode().splitlines()[1:]
         if len(row.split()) >= 3 and row.split()[2] == "RUNNING"}
matches = [row.split()[0] for row in read(["sudo", "-n", "ctr", "-n", "terrascale", "containers", "ls"]).decode().splitlines()[1:]
           if len(row.split()) >= 2 and "/terrabase@" in row.split()[1] and row.split()[0] in tasks]
if len(matches) != 1:
    sys.exit("Expected one running TerraBase container")
root = "/proc/" + tasks[matches[0]]
environment = dict(part.split(b"=", 1) for part in read(["sudo", "-n", "cat", root + "/environ"]).split(b"\0") if b"=" in part)
secret_path = environment.get(b"TERRABASE_PEER_SECRET_FILE")
credential = read(["sudo", "-n", "cat", root + "/root" + secret_path.decode()]).strip() if secret_path else environment[b"TERRABASE_PEER_SECRET"]
peers = read(["sudo", "-n", "cat", root + "/root" + environment[b"TS_PEERS_FILE"].decode()]).decode().splitlines()
node = environment[b"TS_NODE"].decode()
addresses = [line.split()[2] for line in peers if len(line.split()) == 3 and line.split()[0] == node]
if len(addresses) != 1:
    sys.exit("Local node is ambiguous in admitted peers")
with tempfile.NamedTemporaryFile() as header:
    header.write(b"X-TerraBase-Peer-Credential: " + credential + b"\n")
    header.flush()
    result = subprocess.run(["curl", "--silent", "--show-error", "--http2-prior-knowledge", "--max-time", "30",
        "--header", "@" + header.name, "--header", "Content-Type: application/json", "--request", "PUT",
        "--data-binary", "@-", "--write-out", "\n%{http_code}",
        "http://" + addresses[0] + ":8081/admin/organizations/" + organization + "/plan"],
        input=json.dumps({"plan": "enterprise"}).encode(), capture_output=True)
failure = refusal(result)
if failure is not None:
    sys.exit("Synthetic plan assignment failed: " + failure)
print("Synthetic organization plan assigned; normal auth, quotas and process safety remain active.")
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ts-orch", type=Path, required=True)
    parser.add_argument("--vault-id", required=True)
    parser.add_argument("--netbird", required=True)
    parser.add_argument("--known-hosts", type=Path, required=True)
    parser.add_argument("--identity", type=Path, required=True)
    parser.add_argument("--node", required=True)
    parser.add_argument("organization")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-7][0-9A-HJKMNP-TV-Z]{25}", args.organization):
        parser.error("organization must be this run's canonical ULID")
    subprocess.run([str(args.ts_orch), "netbird", "ssh", "--vault-id", args.vault_id, "--netbird", args.netbird, "--",
        "--known-hosts", str(args.known_hosts), "--identity", str(args.identity), "ubuntu@" + args.node,
        "python3", "-", args.organization], input=REMOTE.encode(), check=True)


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError:
        print("Synthetic plan helper failed.", file=sys.stderr)
        sys.exit(1)
