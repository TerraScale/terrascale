#!/usr/bin/env python3
"""Run the ignored SDK tests against a verified C# release, including restart.

Requires Python 3.11+, openssl, cargo, and HTTPS release access (or authenticated fj).
TERRABASE_BINARY overrides the download, but never the pinned SHA-256 check.
Prefix this command with nice -n 19 locally; CI inherits full CPU priority.
"""

import hashlib
import json
import os
from pathlib import Path
import platform
import secrets
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parents[2]
PIN = json.loads(Path(__file__).with_name("release.json").read_text())
CHILDREN = set()
# Explicitly bypass ambient proxies for all loopback/bootstrap calls.
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def stop(child):
    if child.poll() is None:
        os.killpg(child.pid, signal.SIGTERM)
        try:
            child.wait(timeout=10)
        except subprocess.TimeoutExpired:
            os.killpg(child.pid, signal.SIGKILL)
            child.wait(timeout=5)
    # Commands may leave grandchildren behind even after the leader exits.
    try:
        os.killpg(child.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    CHILDREN.discard(child)


def run(args, timeout=300, **kwargs):
    child = subprocess.Popen(args, start_new_session=True, **kwargs)
    CHILDREN.add(child)
    try:
        status = child.wait(timeout=timeout)
        if status:
            raise RuntimeError(f"{args[0]} exited {status}")
    finally:
        stop(child)


def verify(path, digest):
    with path.open("rb") as binary:
        actual = hashlib.file_digest(binary, "sha256").hexdigest()
    if actual != digest:
        raise RuntimeError(f"{path}: SHA-256 {actual} differs from pin {digest}")
    return path


def binary():
    arch = {"x86_64": "amd64", "aarch64": "arm64"}.get(platform.machine())
    if platform.system() != "Linux" or arch not in PIN["assets"]:
        raise RuntimeError("the pinned C# lane requires Linux amd64 or arm64")
    digest = PIN["assets"][arch]
    if os.environ.get("TERRABASE_BINARY"):
        path = verify(Path(os.environ["TERRABASE_BINARY"]).resolve(), digest)
        if not os.access(path, os.X_OK):
            raise RuntimeError(f"{path}: supplied binary must be executable")
        return path
    cache = Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache"))
    path = cache / "ts-rust-terrabase" / PIN["tag"] / f"terrabase-linux-{arch}"
    if path.exists():
        verify(path, digest)
        path.chmod(0o755)
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    # Unique partial file: concurrent runs never consume unfinished downloads.
    with tempfile.TemporaryDirectory(dir=path.parent) as download_dir:
        partial = Path(download_dir) / path.name
        url = f'{PIN["forgejo"]}/{PIN["repository"]}/releases/download/{PIN["tag"]}/{path.name}'
        headers = {}
        token = os.environ.get("TERRABASE_RELEASE_TOKEN")
        if token:
            headers["Authorization"] = f"token {token}"
        # No redirects: an authorization token must stay on the pinned host.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                return None
        try:
            opener = urllib.request.build_opener(NoRedirect())
            with opener.open(urllib.request.Request(url, headers=headers), timeout=30) as response, partial.open("wb") as output:
                deadline = time.monotonic() + 300
                while chunk := response.read(1 << 20):
                    if time.monotonic() > deadline:
                        raise TimeoutError("release download exceeded 300 seconds")
                    output.write(chunk)
        except (OSError, urllib.error.URLError):
            if not shutil.which("fj"):
                raise RuntimeError("release download failed; install/login fj or set TERRABASE_RELEASE_TOKEN or TERRABASE_BINARY") from None
            partial.unlink(missing_ok=True)
            run(["fj", "release", "asset", "download", "-H", "git.terrascale.tech", "-r", PIN["repository"], PIN["tag"], path.name, "-o", str(partial)])
        verify(partial, digest)
        partial.chmod(0o755)
        partial.replace(path)
    return path


def port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def api(origin, path, body=None, headers=None, timeout=30):
    request = urllib.request.Request(origin + path, headers=headers or {},
                                     data=None if body is None else json.dumps(body).encode())
    if body is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with HTTP.open(request, timeout=timeout) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        # Key issuance errors are safe to print; successful secrets never are.
        raise RuntimeError(f"{path}: HTTP {error.code}: {error.read(4096).decode()}") from None


def main():
    executable = binary()
    print(f'C# producer {PIN["commit"]}: verified {executable.name}', flush=True)
    with tempfile.TemporaryDirectory(prefix="ts-rust-csharp-") as temporary:
        work = Path(temporary)
        ca, ca_key = work / "ca.pem", work / "ca-key.pem"
        cert, key, csr = work / "cert.pem", work / "key.pem", work / "server.csr"
        extensions = work / "server.ext"
        extensions.write_text("basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:localhost,IP:127.0.0.1\n")
        quiet = {"timeout": 30, "stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL}
        run(["openssl", "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes", "-keyout", str(ca_key), "-out", str(ca), "-days", "1", "-subj", "/CN=Rust SDK integration CA", "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,cRLSign"], **quiet)
        run(["openssl", "req", "-new", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes", "-keyout", str(key), "-out", str(csr), "-subj", "/CN=localhost"], **quiet)
        run(["openssl", "x509", "-req", "-in", str(csr), "-CA", str(ca), "-CAkey", str(ca_key), "-CAcreateserial", "-out", str(cert), "-days", "1", "-extfile", str(extensions)], **quiet)
        credential = secrets.token_hex(32)
        credential_file = work / "management-credential"
        credential_file.write_text(credential)
        credential_file.chmod(0o600)
        admin_port, client_port = port(), port()
        while client_port == admin_port:
            client_port = port()
        admin = f"http://127.0.0.1:{admin_port}"
        # Do not inherit unrelated producer configuration from the host.
        env = {k: v for k, v in os.environ.items() if not k.startswith("TERRABASE_")}
        env.update(DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(work / "bundle"), DOTNET_ENVIRONMENT="Production")
        args = [str(executable), f"--admin-port={admin_port}", f"--client-port={client_port}", f"--client-certificate={cert}", f"--client-private-key={key}", f"--data-directory={work / 'data'}", f"--management-credential-file={credential_file}", "--region=rust-ci"]
        with (work / "producer.log").open("w+") as log:
            def start():
                child = subprocess.Popen(args, env=env, stdout=log, stderr=log, start_new_session=True)
                CHILDREN.add(child)
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    if child.poll() is not None:
                        raise RuntimeError(f"C# producer exited {child.returncode}")
                    try:
                        ready = api(admin, "/readyz", timeout=2)
                        if ready["status"] == "ready" and ready["region"] == "rust-ci":
                            return child
                    except (OSError, urllib.error.URLError, RuntimeError):
                        pass
                    time.sleep(0.1)
                raise TimeoutError("C# producer readiness exceeded 30 seconds")
            try:
                child = start()
                headers = {"Authorization": f"Bearer {credential}", "X-TerraBase-User-Id": str(uuid.uuid4())}
                organizations = "/v1/management/organizations"
                org = api(admin, organizations, {"name": "Rust binary integration"}, headers)["id"]
                projects = f"{organizations}/{org}/projects"
                project = api(admin, projects, {"name": "SDK"}, headers)["id"]
                data_key = api(admin, "/admin/api-keys", {"name": "rust-ci", "scopes": ["documents:read", "documents:write"], "organizationId": org, "projectId": project})["secret"]
                test_env = {**env, "TB_CSHARP_URL": f"https://127.0.0.1:{client_port}", "TB_CSHARP_KEY": data_key, "TB_CSHARP_PROJECT": project, "TB_CSHARP_CA_FILE": str(ca), "TB_CSHARP_REGION": "rust-ci"}
                cargo = ["cargo", "test", "--locked", "--test", "csharp_live"]
                run(cargo + ["csharp_live_contract", "--", "--ignored", "--nocapture"], timeout=600, cwd=ROOT, env=test_env)
                stop(child)
                print("C# producer stopped; restarting the same isolated data directory", flush=True)
                child = start()
                run(cargo + ["csharp_live_restart", "--", "--ignored", "--nocapture"], timeout=600, cwd=ROOT, env=test_env)
                print("C# release integration and durable restart passed", flush=True)
            except BaseException:
                log.flush()
                log.seek(0)
                print(log.read()[-12000:])
                raise
            finally:
                for child in list(CHILDREN):
                    stop(child)


def interrupted(signum, _frame):
    raise KeyboardInterrupt(f"interrupted by signal {signum}")


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        main()
    finally:
        for child in list(CHILDREN):
            stop(child)
