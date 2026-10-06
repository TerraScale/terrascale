#!/usr/bin/env python3
"""Run the pinned release locally over verified TLS; never contact production."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import secrets
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import urllib.parse

ROOT = Path(__file__).resolve().parents[1]
# Every driver workload; the committed baselines ran the first, third, sixth, seventh and eighth.
WORKLOADS = ["point_read", "point_write", "update", "delete", "query_filter_sort", "list_page", "transactions", "mixed_90_10", "mixed_80_20"]


class DriverFailure(subprocess.CalledProcessError):
    """The workload process exited nonzero; its receipt may already count errors."""


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def benchmark_worktree_dirty(path=ROOT):
    return bool(subprocess.run(["git", "status", "--porcelain", "--untracked-files=normal"], cwd=path, text=True, stdout=subprocess.PIPE, check=True).stdout)


def benchmark_source_state(path=ROOT):
    return {
        "benchmark_worktree_dirty": benchmark_worktree_dirty(path),
        "benchmark_revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=path, text=True).strip(),
    }


class ReleaseRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        target = urllib.parse.urlsplit(newurl)
        if target.scheme != "https" or target.netloc != "git.terrascale.tech":
            raise RuntimeError("release redirect left the trusted host")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def validate_pin(pin):
    fields = ("sha256", "version", "url", "application_version", "source_revision")
    if not isinstance(pin, dict) or any(not isinstance(pin.get(field), str) or not pin[field].strip() for field in fields):
        raise ValueError("pin must be an object with required nonempty string fields")
    if not re.fullmatch(r"[0-9a-f]{64}", pin["sha256"]):
        raise ValueError("pin must hold a lowercase SHA-256")
    version = pin["version"]
    if not re.fullmatch(r"[a-zA-Z0-9_.-]+", version):
        raise ValueError("invalid release version")
    expected = f"https://git.terrascale.tech/TerraScale/terrabase/releases/download/{version}/terrabase-linux-amd64"
    if pin["url"] != expected:
        raise ValueError("pin must identify a TerraBase release asset on the trusted host")


def download(pin, destination):
    # The committed hash, not the server's checksum file, authorizes execution.
    validate_pin(pin)
    token = os.environ.get("ORG_READ_TOKEN")
    if token:
        request = urllib.request.Request(pin["url"], headers={"Authorization": "token " + token})
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), ReleaseRedirect())
        with opener.open(request, timeout=120) as response:
            data = response.read()
    else:
        # Local runs reuse fj's existing login; CI uses ORG_READ_TOKEN above.
        subprocess.run(["fj", "release", "asset", "download", "-r", "TerraScale/terrabase", pin["version"], "terrabase-linux-amd64", "-o", str(destination)], check=True, stdout=subprocess.DEVNULL)
        data = destination.read_bytes()
    if hashlib.sha256(data).hexdigest() != pin["sha256"]:
        raise RuntimeError("release SHA-256 mismatch; refusing execution")
    destination.write_bytes(data)
    destination.chmod(0o700)


def build_driver():
    """Builds the release driver; Cargo's JSON artifact path honors target-directory and build-target overrides."""
    built = subprocess.run(["cargo", "build", "--release", "--locked", "-j", "2", "--message-format=json-render-diagnostics"], cwd=ROOT / "https-document", text=True, stdout=subprocess.PIPE, check=True)
    binaries = [item["executable"] for line in built.stdout.splitlines() if (item := json.loads(line)).get("reason") == "compiler-artifact" and item.get("executable")]
    return binaries[-1]


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def document_id(number):
    return f"00000000-0000-4000-8000-{number:012x}"


def request(opener, origin, path, method="GET", body=None, headers=None, expected=(200,)):
    data = None if body is None else json.dumps(body).encode()
    request_headers = {"Content-Type": "application/json", **(headers or {})}
    req = urllib.request.Request(origin + path, data=data, headers=request_headers, method=method)
    try:
        with opener.open(req, timeout=10) as response:
            status, data = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, data = error.code, error.read()
    if status not in expected:
        # No body is printed: a key-creation response could contain a secret.
        raise RuntimeError(f"{method} {path}: expected {expected}, got {status}")
    return json.loads(data) if data else None


def run(args):
    pin = json.loads(args.pin.read_text())
    validate_pin(pin)
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "amd64"):
        raise RuntimeError("committed release pin is Linux amd64")
    if args.output.exists():
        raise RuntimeError("output must be a fresh evidence directory")
    source_state = benchmark_source_state()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["make", "-C", str(ROOT / "https-document"), "fmt", "lint", "test"], check=True)
    driver = build_driver()
    with tempfile.TemporaryDirectory(prefix="ts500-") as scratch:
        work = Path(scratch)
        binary = work / "terrabase"
        download(pin, binary)
        certificate, private_key = work / "tls.pem", work / "tls.key"
        ca, ca_key = work / "ca.pem", work / "ca.key"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=TS500 local CA", "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-keyout", str(ca_key), "-out", str(ca)], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(["openssl", "req", "-new", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=localhost", "-keyout", str(private_key), "-out", str(work / "tls.csr")], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        (work / "tls.ext").write_text("basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=IP:127.0.0.1,DNS:localhost\n")
        subprocess.run(["openssl", "x509", "-req", "-days", "1", "-in", str(work / "tls.csr"), "-CA", str(ca), "-CAkey", str(ca_key), "-CAcreateserial", "-extfile", str(work / "tls.ext"), "-out", str(certificate)], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        credential = secrets.token_hex(32)
        (work / "dashboard.key").write_text(credential)
        (work / "dashboard.key").chmod(0o600)
        port, admin_port = free_port(), free_port()
        while admin_port == port:
            admin_port = free_port()
        origin, admin = f"https://127.0.0.1:{port}", f"http://127.0.0.1:{admin_port}"
        context = ssl.create_default_context(cafile=str(ca))
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
        # Inherited product credentials/configuration never reach this isolated server.
        env = {key: value for key, value in os.environ.items() if not key.startswith("TERRABASE_") and key != "ORG_READ_TOKEN"}
        process = subprocess.Popen([str(binary), f"--data-directory={work / 'data'}", f"--admin-port={admin_port}", f"--client-port={port}", f"--client-certificate={certificate}", f"--client-private-key={private_key}", f"--dashboard-credential-file={work / 'dashboard.key'}"], env=env, cwd=work, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 30
            while True:
                if process.poll() is not None:
                    raise RuntimeError("pinned TerraBase exited during startup")
                try:
                    request(opener, admin, "/healthz")
                    break
                except (OSError, RuntimeError):
                    if time.monotonic() > deadline:
                        raise RuntimeError("pinned TerraBase was not ready after 30 seconds") from None
                    time.sleep(0.1)
            health = request(opener, admin, "/healthz")
            if health["version"] != pin["application_version"]:
                raise RuntimeError("release runtime version differs from pin")
            management = {"Authorization": "Bearer " + credential, "X-TerraBase-User-Id": "ts500-local"}
            org = request(opener, origin, "/v1/management/organizations", "POST", {"name": "TS500 acceptance"}, management, (201,))
            management["X-TerraBase-Organization-Id"] = org["id"]
            projects = f"/v1/management/organizations/{org['id']}/projects"
            project = request(opener, origin, projects, "POST", {"name": "Baseline"}, management, (201,))
            database = request(opener, origin, f"{projects}/{project['id']}/databases", "POST", {"name": "baseline"}, management, (201,))
            key = request(opener, admin, "/admin/api-keys", "POST", {"name": "TS500 documents", "scopes": ["documents:read", "documents:write"], "organizationId": org["id"], "projectId": project["id"], "databaseId": database["id"]}, expected=(201,))
            if not key["secret"].startswith("tb_"):
                raise RuntimeError("release returned an obsolete API key")
            token_file = work / "document.key"
            token_file.write_text(key["secret"])
            token_file.chmod(0o600)
            auth = {"Authorization": "Bearer " + key["secret"]}
            db_path = f"/v1/projects/{project['id']}/databases/{database['name']}"
            collection = db_path + "/collections/baseline"
            request(opener, origin, db_path + "/collections", "POST", {"name": "baseline"}, auth, (201,))
            for rank in range(32):
                request(opener, origin, f"{collection}/documents/{document_id(rank)}", "PUT", {"rank": rank, "group": rank % 2, "payload": "ts500"}, auth, (201,))
            # A second write fails after the first write; the entire transaction must roll back.
            rollback_id = document_id(999)
            request(opener, origin, db_path + "/transactions", "POST", {"ops": [
                {"op": "put", "collection": "baseline", "id": rollback_id, "document": {"rollback": True}},
                {"op": "put", "collection": "baseline", "id": document_id(0), "document": {"should_not_commit": True}, "if_none_match": "*"},
            ]}, auth, (412,))
            request(opener, origin, f"{collection}/documents/{rollback_id}", headers=auth, expected=(404,))
            config = {"endpoint_override": origin, "atlas_url": "https://atlas.terrascale.tech", "project": project["id"], "database": database["name"], "collection": "baseline", "ca_pem_path": str(ca), "server_binary_path": str(binary), "server_version": pin["version"], "server_sha256": pin["sha256"], "concurrency": [1, 4, 16], "operations_per_worker": 10 if args.smoke else 100, "timeout_ms": 10000, "workloads": WORKLOADS}
            write_json(work / "workload.json", config)
            try:
                result = subprocess.run([driver, "--config", str(work / "workload.json"), "--token-file", str(token_file), "--output", str(args.output.resolve())], check=False)
                if result.returncode:
                    raise DriverFailure(result.returncode, result.args)
            finally:
                if args.output.exists():
                    write_json(args.output / "environment.json", {"release": pin, "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), **source_state, "cpu_model": next((line.split(":", 1)[1].strip() for line in Path("/proc/cpuinfo").read_text().splitlines() if line.startswith("model name")), platform.processor()), "memory_total": Path("/proc/meminfo").read_text().splitlines()[0], "platform": platform.platform(), "cpu_count": os.cpu_count(), "affinity": sorted(os.sched_getaffinity(0)), "priority": os.getpriority(os.PRIO_PROCESS, 0), "mode": "smoke" if args.smoke else "baseline", "fixture": {"seed_documents": 32, "generator": "rank 0..31, group rank%2, payload ts500", "rollback_verified": True}, "server_options": {"tls": "verified local CA and server leaf", "storage": "fresh local directory", "admin": "loopback", "shutdown_grace_seconds": 10}})
        finally:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--pin", type=Path, default=ROOT / "https-document/release.json")
    output = parser.add_mutually_exclusive_group(required=True)
    output.add_argument("--output", type=Path)
    output.add_argument("--output-parent", type=Path)
    args = parser.parse_args(argv)
    if args.output_parent is not None:
        args.output = args.output_parent / ("run-" + secrets.token_hex(16))
    print(f"output: {args.output}", flush=True)
    fresh_output = not args.output.exists()
    try:
        run(args)
    except Exception as error:
        # Preserve failures for the always-upload CI artifact without overwriting prior runs.
        if fresh_output:
            args.output.mkdir(parents=True, exist_ok=True)
            receipt_path = args.output / "receipt.json"
            receipt = json.loads(receipt_path.read_text()) if receipt_path.exists() else {"schema_version": 1, "errors": 0}
            driver_reported_failure = isinstance(error, DriverFailure) and receipt.get("status") == "failed" and receipt.get("errors", 0) > 0
            if not driver_reported_failure:
                receipt.update(status="failed", errors=receipt.get("errors", 0) + 1, failure=type(error).__name__, harness_failure=type(error).__name__)
            write_json(receipt_path, receipt)
            summary_path = args.output / "summary.md"
            previous = summary_path.read_text() if summary_path.exists() else ""
            summary_path.write_text("# C# acceptance failed\n\n| Status | Errors | Failure |\n| --- | ---: | --- |\n" + f"| failed | {receipt['errors']} | {type(error).__name__} |\n\n" + previous)
        # Unexpected shape errors may contain response data, including API secrets.
        diagnostic = str(error) if isinstance(error, (OSError, ValueError, RuntimeError, subprocess.CalledProcessError)) else type(error).__name__
        parser.exit(1, f"acceptance: {diagnostic}\n")


if __name__ == "__main__":
    main()
