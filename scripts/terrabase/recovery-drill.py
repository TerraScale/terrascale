#!/usr/bin/env python3
"""Host-local certified-recovery drill, limited to FR1's approved throwaway project.

Run prepare, arm, retry separately as described in the companion runbook. Credentials
are consumed only from a private header file; stdout contains non-secret evidence.
No service endpoint, grant, fault bypass, SYSTEM write, restart or process kill is added.
"""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import stat
import struct
import subprocess
import sys
import tempfile
import time

ORGANIZATION = "01M5N3V3EEA0QS2S7BT4PF1RF7"
PROJECT = "01M5N3V3V78DCEHHRX1M5KZKAM"
OWNER = "5H1K4FE8MBAF6ENA73DF6F9JK6"
EXISTING_DATABASE = "01M5N3VACKAVW73F70AN7RF5WJ"
HOSTS = {"gru-1-ctl-1": "10.200.165.148", "ts-sjc-1": "10.200.109.193", "ts-fra-1": "10.200.40.238"}
ROOT = Path("/var/lib/ts-data/terrabase/data")
COLLECTION = "recovery_drill"
ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"


class Refused(Exception):
    pass


def require(condition, message):
    if not condition:
        raise Refused(message)


def ulid(value):
    return isinstance(value, str) and len(value) == 26 and value[0] <= "7" and all(c in ALPHABET for c in value)


def new_id():
    number = (int(time.time() * 1000) << 80) | secrets.randbits(80)
    return "".join(ALPHABET[(number >> shift) & 31] for shift in range(125, -1, -5))


def private_read(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd) as file:
        info = os.fstat(file.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0,
                "input must be an owned private regular file")
        text = file.read(65537)
        require(len(text) <= 65536, "input exceeds bound")
        return text


def save(path, manifest, exclusive=False):
    # The manifest contains no credentials. Persist before each step that can leave a fixture pending.
    data = json.dumps(manifest, sort_keys=True) + "\n"
    if exclusive:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "w") as file:
            file.write(data)
            file.flush()
            os.fsync(file.fileno())
    else:
        private_read(path)
        with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, prefix=".recovery-drill-", delete=False) as file:
            pending = Path(file.name)
            file.write(data)
            file.flush()
            os.fsync(file.fileno())
        os.replace(pending, path)
    directory = os.open(path.parent, os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def validate(manifest):
    require(manifest.get("organization") == ORGANIZATION and manifest.get("project") == PROJECT
            and manifest.get("owner") == OWNER, "manifest is outside the approved throwaway tenant")
    require(ulid(manifest.get("nonce")) and manifest.get("name") == "recovery_drill_" + manifest["nonce"].lower(), "invalid fixture identity")
    require(manifest.get("host") == socket.gethostname() and manifest["host"] in HOSTS, "fixture belongs to another host")
    for key in ("database", "live", "deleted"):
        require(ulid(manifest.get(key)), "invalid fixture identifier")
    require(manifest["database"] not in (EXISTING_DATABASE, PROJECT, ORGANIZATION), "original tenant database is never faulted")
    return manifest


class Client:
    def __init__(self, header_path):
        headers = {}
        for line in private_read(header_path).splitlines():
            name, separator, value = line.partition(":")
            require(separator and name.lower() not in headers, "invalid or duplicate credential header")
            headers[name.lower()] = value.strip()
        require(set(headers) == {"authorization", "x-terrabase-peer-credential", "x-terrabase-user-id"}
                and headers["x-terrabase-user-id"] == OWNER
                and headers["authorization"].startswith("Bearer ") and 7 < len(headers["authorization"]) <= 8199
                and 0 < len(headers["x-terrabase-peer-credential"]) <= 8192, "expected existing peer, owner and sign-in proof")
        self.headers = headers

    def request(self, host, method, path, body=None, authority=False, grpc=False):
        require(host in HOSTS, "only approved NetBird targets are allowed")
        require(not authority or host == socket.gethostname() and path.endswith("/recovery/retry"), "authority is local recovery only")
        headers = {"x-terrabase-peer-credential": self.headers["x-terrabase-peer-credential"]}
        if authority:
            headers.update(self.headers)
        args = ["curl", "--silent", "--show-error", "--http2-prior-knowledge", "--max-time", "45",
                "--max-filesize", "131072", "--proto", "=http", "--header", "@/dev/stdin", "--request", method,
                "--write-out", "\n%{http_code}", "http://" + HOSTS[host] + ":8081" + path]
        with tempfile.TemporaryDirectory(prefix="recovery-drill-request-") as temporary:
            if body is not None:
                payload = Path(temporary) / "body"
                payload.write_bytes(body if grpc else json.dumps(body).encode())
                args += ["--header", "Content-Type: " + ("application/grpc" if grpc else "application/json"), "--data-binary", "@" + str(payload)]
            result = subprocess.run(args, input="".join(k + ": " + v + "\n" for k, v in headers.items()).encode(), capture_output=True)
        require(result.returncode == 0, "internal request failed; inspect target health without retrying issuance")
        payload, code = result.stdout.rsplit(b"\n", 1)
        return int(code), payload if grpc else json.loads(payload) if payload else None

    def data(self, manifest, host, method, suffix="", body=None):
        return self.request(host, method, f"/v1/projects/{PROJECT}/databases/{manifest['name']}" + suffix, body)

    def recovery(self, database, incarnation):
        return self.request(socket.gethostname(), "POST", f"/v1/management/organizations/{ORGANIZATION}/projects/{PROJECT}/databases/{database}/recovery/retry",
                            {"faultedIncarnation": incarnation, "reason": "Approved isolated recovery drill"}, authority=True)

    def fresh(self, database):
        # This actual endpoint checks trusted gate and <=30s proofs before owner authorization and the ledger lookup.
        status, body = self.recovery(database, "00000000000000000000000001")
        if status == 503 and body.get("error", {}).get("code") == "catalog_stale":
            raise Refused("503 catalog_stale is the correct fail-closed result; no fault or blocker release is authorized")
        require(status == 409 and body.get("error", {}).get("code") == "no_matching_rebuild",
                f"fresh authorized preflight failed (HTTP {status}); no fault or blocker release is authorized")


def document_path(manifest, key):
    return f"/collections/{COLLECTION}/documents/by-key?id={manifest[key]}"


def verify_peers(client, manifest, deleted):
    for host in HOSTS:
        for key in ("live", "deleted"):
            status, body = client.data(manifest, host, "GET", document_path(manifest, key))
            if key == "deleted" and deleted:
                require(status == 404 and body.get("error", {}).get("code") == "not_found", "deleted fixture document is not absent on every peer")
            else:
                require(status == 200 and body.get("document") == {"recovery_drill": manifest["nonce"], "kind": key}, "an intact peer lacks the exact synthetic fixture")


def database_path(manifest):
    validate(manifest)
    path = ROOT / ORGANIZATION / PROJECT / (manifest["database"] + ".db")
    require(all(not part.is_symlink() for part in (ROOT, ROOT / ORGANIZATION, path.parent, path)), "database path must not follow links")
    require(path.is_file(), "fixture copy is absent")
    return path


@contextmanager
def database(path, **kwargs):
    connection = sqlite3.connect(path, **kwargs)
    try:
        with connection:
            yield connection
    finally:
        connection.close()


def read_copy(path):
    with database(path.as_uri() + "?mode=ro", uri=True) as db:
        return db.execute("SELECT incarnation FROM commit_offset WHERE id=1").fetchone()[0]


def pending(manifest):
    # Read-only inspection: the server is the only writer of SYSTEM and its durable rebuild records.
    path = ROOT / "_system.db"
    require(not path.is_symlink() and path.is_file(), "SYSTEM path is not a regular catalog")
    with database(path.as_uri() + "?mode=ro", uri=True) as db:
        return db.execute("SELECT old_incarnation,phase,new_incarnation FROM _tb_rebuild WHERE organization=? AND project=? AND database=?",
                          (ORGANIZATION, PROJECT, manifest["database"])).fetchone()


def prepare(client, manifest_path):
    require(not manifest_path.exists(), "never repeat provisioning with an existing manifest")
    client.fresh(EXISTING_DATABASE)
    nonce = new_id()
    manifest = {"organization": ORGANIZATION, "project": PROJECT, "owner": OWNER, "nonce": nonce,
                "name": "recovery_drill_" + nonce.lower(), "host": socket.gethostname(), "live": new_id(), "deleted": new_id(), "stage": "creating"}
    save(manifest_path, manifest, exclusive=True)
    status, body = client.request(manifest["host"], "POST", f"/v1/projects/{PROJECT}/databases", {"name": manifest["name"], "allRegions": True})
    require(status == 201 and ulid(body.get("id")), "fresh database creation failed; inspect manifest, never overwrite an existing database")
    manifest["database"] = body["id"]
    save(manifest_path, manifest)
    status, _ = client.data(manifest, manifest["host"], "POST", "/collections", {"name": COLLECTION})
    require(status == 201, "fixture collection creation failed")
    for key in ("live", "deleted"):
        status, _ = client.data(manifest, manifest["host"], "PUT", document_path(manifest, key), {"recovery_drill": nonce, "kind": key})
        require(status == 201, "synthetic document creation failed")
    verify_peers(client, manifest, deleted=False)
    status, _ = client.data(manifest, manifest["host"], "DELETE", document_path(manifest, "deleted"))
    require(status in (200, 204), "fixture deletion failed")
    verify_peers(client, manifest, deleted=True)
    manifest.update(stage="prepared", incarnation=read_copy(database_path(manifest)))
    require(ulid(manifest["incarnation"]), "invalid copy incarnation")
    save(manifest_path, manifest)
    return manifest


def arm(client, manifest_path, manifest, confirmation):
    require(manifest["stage"] == "prepared" and confirmation == manifest["nonce"], "arm requires the exact prepared fixture confirmation")
    path = database_path(manifest)
    verify_peers(client, manifest, deleted=True)
    client.fresh(manifest["database"])
    require(pending(manifest) is None, "a rebuild already exists; do not arm twice")
    require(ulid(manifest["incarnation"]), "invalid fault incarnation")
    blocker = path.parent / ("." + manifest["database"] + "." + manifest["incarnation"] + ".rebuild")
    # Blocking the exact side path keeps the real worker from completing before the explicit retry. No runtime switch.
    os.mkdir(blocker, 0o700)
    manifest["stage"] = "arming"
    save(manifest_path, manifest)
    with database(path.as_uri() + "?mode=rw", uri=True, timeout=5) as db:
        db.execute("BEGIN IMMEDIATE")
        require(db.execute("SELECT incarnation FROM commit_offset WHERE id=1").fetchone()[0] == manifest["incarnation"], "copy changed before fault injection")
        rows = db.execute("SELECT collection,id,live FROM _tb_state").fetchall()
        allowed = {("_collections", COLLECTION, 1), (COLLECTION, manifest["live"], 1), (COLLECTION, manifest["deleted"], 0)}
        require(set(rows).issubset(allowed) and (COLLECTION, manifest["live"], 1) in rows and ("_collections", COLLECTION, 1) in rows,
                "copy contains data outside this synthetic fixture")
        # Only derived Merkle metadata of the new synthetic copy is damaged; documents and state hashes stay intact.
        db.execute("INSERT INTO _tb_merkle(bucket,h1,h2) VALUES(-1,0,0)")
    fields = b"".join(bytes([number * 8 + 2, len(value)]) + value.encode() for number, value in enumerate((ORGANIZATION, PROJECT, manifest["database"]), 1))
    message = bytes([10, len(fields)]) + fields
    client.request(manifest["host"], "POST", "/terrabase.v1.Replication/Digest", b"\0" + struct.pack(">I", len(message)) + message, grpc=True)
    require(pending(manifest) == (manifest["incarnation"], 0, None), "server did not persist the expected pending rebuild; keep the blocker for inspection")
    manifest["stage"] = "pending"
    save(manifest_path, manifest)
    return manifest


def retry(client, manifest_path, manifest):
    require(manifest["stage"] == "pending", "only this fixture's pending rebuild may be retried")
    path = database_path(manifest)
    require(pending(manifest) == (manifest["incarnation"], 0, None), "pending rebuild changed")
    client.fresh(manifest["database"])
    blocker = path.parent / ("." + manifest["database"] + "." + manifest["incarnation"] + ".rebuild")
    require(not blocker.is_symlink() and blocker.is_dir() and not any(blocker.iterdir()), "owned blocker is not an empty directory")
    os.rmdir(blocker)
    status, _ = client.recovery(manifest["database"], manifest["incarnation"])
    manifest["retry_status"] = status
    manifest["stage"] = "retry_returned"
    save(manifest_path, manifest)
    require(status == 204, "retry did not return204; preserve fixture and inspect (automatic recovery may have won); do not claim an authenticated204")
    require(pending(manifest) is None and not blocker.exists(), "rebuild retirement or side-copy cleanup is incomplete")
    require(read_copy(path) != manifest["incarnation"], "rebuild kept the old incarnation")
    with database(path.as_uri() + "?mode=ro", uri=True) as db:
        certificates = db.execute("SELECT origin,incarnation,head FROM _tb_refill").fetchall()
    require(certificates and all(ulid(row[1]) and row[2] >= 0 for row in certificates), "certified refill evidence is absent")
    verify_peers(client, manifest, deleted=True)
    client.fresh(manifest["database"])
    manifest.update(stage="certified", new_incarnation=read_copy(path), refill=certificates)
    save(manifest_path, manifest)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "arm", "retry"))
    parser.add_argument("--headers-file", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--confirm")
    args = parser.parse_args()
    try:
        require(os.geteuid() == 0 and socket.gethostname() in HOSTS, "run on an approved OCI target as its existing operator")
        client = Client(args.headers_file)
        if args.action == "prepare":
            result = prepare(client, args.manifest)
        else:
            manifest = validate(json.loads(private_read(args.manifest)))
            result = arm(client, args.manifest, manifest, args.confirm) if args.action == "arm" else retry(client, args.manifest, manifest)
        print(json.dumps(result, sort_keys=True))
        return 0
    except Refused as error:
        print(str(error), file=sys.stderr)
        return 1
    except (OSError, ValueError, sqlite3.Error, KeyError):
        # No exception rendering: HTTP and filesystem libraries can include credential-bearing input.
        print("Drill refused or incomplete; preserve the private manifest and any blocker. See runbook; no freshness bypass or automatic rerun.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
