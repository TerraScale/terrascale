#!/usr/bin/env python3
"""Provision, measure, and clean one run-owned HTTPS document fixture.

Do not run until the product Fast suite has passed and the coordinator opens
the build/run slot. This script never builds product or benchmark binaries.
"""

from __future__ import annotations

import atexit
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import time
from typing import Any


MEMORY_MAX_BYTES = 8 * 1024 * 1024 * 1024
PROCESS_MEMORY_LIMIT_BYTES = 256 * 1024 * 1024
PROCESS_MEMORY_HEADROOM_BYTES = 16 * 1024 * 1024
INTER_REQUEST_DELAY_MS = 20
ADMIN_READY_TIMEOUT_SECONDS = 20
SERVER_STOP_TIMEOUT_SECONDS = 30

TENANT = "11111111-1111-1111-1111-111111111111"
DATABASE = "22222222-2222-2222-2222-222222222222"
SHARD = "7"
COLLECTION = "61616161-6161-6161-6161-616161616161"
DOCUMENT = "71717171-7171-7171-7171-717171717171"


class FixtureError(RuntimeError):
    pass


def sha256_bytes(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def canonical_json(value: Any) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def write_private(path: Path, content: bytes) -> None:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "wb", closefd=False) as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
    finally:
        os.close(descriptor)


def write_private_json(path: Path, value: Any) -> bytes:
    content = canonical_json(value)
    write_private(path, content)
    return content


def atomic_private_json(path: Path, value: Any) -> bytes:
    temporary = path.with_name(path.name + ".tmp")
    content = canonical_json(value)
    write_private(temporary, content)
    os.replace(temporary, path)
    return content


def command_output(argv: list[str], *, cwd: Path | None = None, timeout: int = 60) -> bytes:
    try:
        result = subprocess.run(
            argv,
            cwd=cwd,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise FixtureError(f"{Path(argv[0]).name} could not complete") from error
    if result.returncode != 0:
        raise FixtureError(f"{Path(argv[0]).name} failed with exit code {result.returncode}")
    return result.stdout


def git_output(repository: Path, *args: str) -> bytes:
    return command_output(["git", "-C", str(repository), *args], timeout=120)


def diff_sha256(repository: Path) -> str:
    process = subprocess.Popen(
        ["git", "-C", str(repository), "diff", "--binary", "HEAD"],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    assert process.stdout is not None
    digest = hashlib.sha256()
    for block in iter(lambda: process.stdout.read(1024 * 1024), b""):
        digest.update(block)
    stderr = process.stderr.read() if process.stderr else b""
    status = process.wait()
    if status != 0:
        raise FixtureError(f"git diff failed ({status}): {stderr[:160]!r}")
    return digest.hexdigest()


def untracked_hashes(repository: Path) -> list[dict[str, str]]:
    raw_paths = git_output(repository, "ls-files", "--others", "--exclude-standard", "-z")
    entries: list[dict[str, str]] = []
    for raw_path in raw_paths.split(b"\0"):
        if not raw_path:
            continue
        relative = os.fsdecode(raw_path)
        if relative.startswith(("target/", "bench/")):
            continue
        path = repository / relative
        if path.is_symlink():
            entries.append(
                {"path": relative, "kind": "symlink", "target": os.readlink(path)}
            )
        elif path.is_file():
            entries.append(
                {"path": relative, "kind": "file", "sha256": sha256_file(path)}
            )
        elif path.is_dir():
            entries.append({"path": relative, "kind": "directory"})
    return sorted(entries, key=lambda entry: entry["path"])


def repository_identity(repository: Path) -> dict[str, Any]:
    head = git_output(repository, "rev-parse", "HEAD").decode().strip()
    status = git_output(repository, "status", "--porcelain=v1", "-z")
    submodules = git_output(repository, "submodule", "status", "--recursive")
    return {
        "repository": str(repository),
        "head": head,
        "status_sha256": sha256_bytes(status),
        "status_entries": [entry.decode(errors="replace") for entry in status.split(b"\0") if entry],
        "diff_sha256": diff_sha256(repository),
        "untracked": untracked_hashes(repository),
        "submodule_status": submodules.decode(errors="replace").splitlines(),
    }


def clean_environment(run_dir: Path) -> dict[str, str]:
    temporary = run_dir / "tmp"
    temporary.mkdir(mode=0o700, exist_ok=True)
    return {
        "PATH": "/run/current-system/sw/bin:/usr/bin:/bin",
        "HOME": str(run_dir),
        "TMPDIR": str(temporary),
        "LC_ALL": "C",
    }


def require_executable(path: Path, label: str, *, static_musl: bool) -> dict[str, str]:
    resolved = path.resolve(strict=True)
    if not resolved.is_file() or not os.access(resolved, os.X_OK):
        raise FixtureError(f"{label} must be an executable regular file: {resolved}")
    description = command_output(["file", "-b", str(resolved)]).decode(errors="replace").strip()
    if static_musl and "static-pie linked" not in description:
        raise FixtureError(f"{label} is not the expected static musl PIE executable")
    return {"path": str(resolved), "sha256": sha256_file(resolved), "file": description}


def allocate_loopback_ports() -> tuple[int, int, list[socket.socket]]:
    reservations: list[socket.socket] = []
    ports: list[int] = []
    try:
        for _ in range(2):
            reservation = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            reservation.bind(("127.0.0.1", 0))
            reservations.append(reservation)
            ports.append(int(reservation.getsockname()[1]))
        if ports[0] == ports[1]:
            raise FixtureError("could not reserve distinct loopback ports")
        return ports[0], ports[1], reservations
    except BaseException:
        for reservation in reservations:
            reservation.close()
        raise


def initialize_shard(
    init_binary: Path,
    fixture_recipe: Path,
    run_dir: Path,
    data_dir: Path,
    local_config: Path,
    env: dict[str, str],
) -> dict[str, str]:
    input_path = run_dir / "initializer-input.json"
    shutil.copyfile(fixture_recipe, input_path)
    os.chmod(input_path, 0o600)
    argv = [
        str(init_binary),
        "--input",
        str(input_path),
        "--local-v1-config",
        str(local_config),
        "--data-dir",
        str(data_dir),
        "--memory-limit-bytes",
        str(PROCESS_MEMORY_LIMIT_BYTES),
        "--memory-headroom-bytes",
        str(PROCESS_MEMORY_HEADROOM_BYTES),
    ]
    try:
        result = subprocess.run(
            argv,
            cwd=run_dir,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=120,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise FixtureError("packaged terrabase-init did not complete") from error
    if result.returncode != 0 or not data_dir.is_dir() or not local_config.is_file():
        raise FixtureError(f"packaged terrabase-init failed with exit code {result.returncode}")
    if (data_dir.stat().st_mode & 0o777) != 0o700:
        raise FixtureError("initializer data directory is not owner-only")
    if (local_config.stat().st_mode & 0o777) != 0o600:
        raise FixtureError("initializer local-v1 config is not owner-only")
    return {"argv": argv, "data_dir": str(data_dir), "local_v1_config": str(local_config)}


def create_tls_material(run_dir: Path, env: dict[str, str]) -> dict[str, Path]:
    ca_key = run_dir / "client-ca-key.pem"
    ca_cert = run_dir / "client-ca.pem"
    server_key = run_dir / "server-key.pem"
    server_csr = run_dir / "server.csr"
    server_cert = run_dir / "server-cert.pem"
    chain = run_dir / "server-chain.pem"
    extensions = run_dir / "server-cert-extensions.cnf"

    command_output(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(ca_key)], cwd=run_dir)
    command_output(
        [
            "openssl", "req", "-x509", "-new", "-key", str(ca_key), "-out", str(ca_cert),
            "-days", "2", "-sha256", "-subj", f"/CN=TerraBase HTTPS fixture {run_dir.name}",
            "-addext", "basicConstraints=critical,CA:TRUE,pathlen:0",
            "-addext", "keyUsage=critical,keyCertSign,cRLSign",
        ],
        cwd=run_dir,
    )
    command_output(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(server_key)], cwd=run_dir)
    command_output(
        ["openssl", "req", "-new", "-key", str(server_key), "-out", str(server_csr), "-subj", "/CN=127.0.0.1"],
        cwd=run_dir,
    )
    write_private(
        extensions,
        b"basicConstraints=critical,CA:FALSE\n"
        b"keyUsage=critical,digitalSignature\n"
        b"extendedKeyUsage=serverAuth\n"
        b"subjectAltName=IP:127.0.0.1\n",
    )
    command_output(
        [
            "openssl", "x509", "-req", "-in", str(server_csr), "-CA", str(ca_cert),
            "-CAkey", str(ca_key), "-CAcreateserial", "-out", str(server_cert),
            "-days", "2", "-sha256", "-extfile", str(extensions),
        ],
        cwd=run_dir,
    )
    command_output(["openssl", "verify", "-CAfile", str(ca_cert), str(server_cert)], cwd=run_dir)
    chain_content = server_cert.read_bytes() + ca_cert.read_bytes()
    write_private(chain, chain_content)
    ca_key.unlink()
    server_csr.unlink()
    extensions.unlink()
    serial = run_dir / "client-ca.srl"
    if serial.exists():
        serial.unlink()
    for path in (ca_cert, server_key, server_cert, chain):
        os.chmod(path, 0o600)
    return {"ca": ca_cert, "server_key": server_key, "server_cert": server_cert, "chain": chain}


def generate_key(
    keygen_binary: Path,
    run_dir: Path,
    token_name: str,
    permissions: list[str],
    *,
    rate_per_second: int,
    burst: int,
) -> tuple[str, dict[str, Any]]:
    try:
        result = subprocess.run(
            [str(keygen_binary)],
            cwd=run_dir,
            env=clean_environment(run_dir),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=20,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise FixtureError("packaged terrabase-keygen did not complete") from error
    if result.returncode != 0:
        raise FixtureError(f"packaged terrabase-keygen failed with exit code {result.returncode}")
    try:
        lines = result.stdout.decode("utf-8").splitlines()
        token = lines[0].removeprefix("API key (copy once): ")
        template_line = next(line for line in lines if line.startswith("{"))
        entry = json.loads(template_line)
    except (UnicodeDecodeError, IndexError, StopIteration, json.JSONDecodeError) as error:
        raise FixtureError("packaged terrabase-keygen output did not match its source contract") from error
    if not token.startswith("tbk1.") or not entry.get("id") or not entry.get("verifier_sha256"):
        raise FixtureError("packaged terrabase-keygen returned an invalid key shape")
    entry.update(
        {
            "tenant": TENANT,
            "database": DATABASE,
            "shard": SHARD,
            "collections": [COLLECTION],
            "permissions": permissions,
            "rate_per_second": rate_per_second,
            "burst": burst,
            "max_in_flight": 1,
            "revoked": False,
        }
    )
    token_path = run_dir / token_name
    write_private(token_path, (token + "\n").encode())
    return token, entry


def write_registry(path: Path, entries: list[dict[str, Any]]) -> str:
    ordered = sorted(entries, key=lambda entry: entry["id"])
    content = atomic_private_json(path, {"version": 1, "keys": ordered})
    return sha256_bytes(content)


def write_server_config(
    path: Path,
    run_dir: Path,
    client_port: int,
    tls: dict[str, Path],
    registry: Path,
) -> dict[str, Any]:
    config = {
        "version": 1,
        "listen_addr": f"127.0.0.1:{client_port}",
        "certificate_chain": str(tls["chain"]),
        "private_key": str(tls["server_key"]),
        "api_key_registry": str(registry),
        "handshake_timeout_ms": 1000,
    }
    write_private_json(path, config)
    return config


def systemd_property(unit: str, name: str) -> str:
    value = command_output(
        ["systemctl", "--user", "show", "--property", name, "--value", unit], timeout=15
    ).decode(errors="replace").strip()
    if not value:
        raise FixtureError(f"systemd did not report {name} for the run-owned server scope")
    return value


def run_cgroup_readings(unit: str) -> dict[str, str]:
    control_group = systemd_property(unit, "ControlGroup")
    if not control_group.startswith("/") or ".." in Path(control_group).parts:
        raise FixtureError("systemd returned an invalid run-owned cgroup path")
    cgroup = Path("/sys/fs/cgroup") / control_group.lstrip("/")
    try:
        memory_max = (cgroup / "memory.max").read_text().strip()
        cpu_max = (cgroup / "cpu.max").read_text().strip()
    except OSError as error:
        raise FixtureError("could not read the server scope cgroup limits") from error
    if memory_max != str(MEMORY_MAX_BYTES):
        raise FixtureError(f"server cgroup memory.max is not the requested {MEMORY_MAX_BYTES} bytes")
    return {"control_group": control_group, "memory_max": memory_max, "cpu_max": cpu_max}


class ServerScope:
    def __init__(self, unit: str, launcher: subprocess.Popen[bytes], log_path: Path):
        self.unit = unit
        self.launcher = launcher
        self.log_path = log_path
        self.cgroup: dict[str, str] | None = None


def cleanup_process_resources(units: list[str], token_paths: list[Path]) -> None:
    for unit in reversed(units):
        subprocess.run(
            ["systemctl", "--user", "stop", unit],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=20,
            check=False,
        )
    for token_path in token_paths:
        try:
            token_path.unlink()
        except FileNotFoundError:
            pass


def start_server(
    server: dict[str, str],
    data_dir: Path,
    local_config: Path,
    client_config: Path,
    admin_port: int,
    run_dir: Path,
    stage: str,
    cleanup_units: list[str],
) -> ServerScope:
    unit = f"tb-https-doc-{run_dir.name}-{stage}.scope"
    existing = subprocess.run(
        ["systemctl", "--user", "show", "--property", "ActiveState", "--value", unit],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=15,
        check=False,
    )
    if existing.returncode == 0 and existing.stdout.strip() in (b"active", b"activating", b"reloading"):
        raise FixtureError("run-owned server scope name is already active")
    log_path = run_dir / f"server-{stage}-stderr.txt"
    log_file = log_path.open("xb")
    env = clean_environment(run_dir)
    env_args = ["/usr/bin/env", "-i", *[f"{key}={value}" for key, value in env.items()]]
    server_args = [
        server["path"],
        "--data-dir",
        str(data_dir),
        "--memory-limit-bytes",
        str(PROCESS_MEMORY_LIMIT_BYTES),
        "--memory-headroom-bytes",
        str(PROCESS_MEMORY_HEADROOM_BYTES),
        "--port",
        str(admin_port),
        "--local-v1-config",
        str(local_config),
        "--client-config",
        str(client_config),
    ]
    argv = [
        "systemd-run",
        "--user",
        "--scope",
        "--quiet",
        f"--unit={unit}",
        f"--property=MemoryMax={MEMORY_MAX_BYTES}",
        "--",
        *env_args,
        *server_args,
    ]
    try:
        launcher = subprocess.Popen(
            argv,
            cwd=run_dir,
            stdin=subprocess.DEVNULL,
            stdout=log_file,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
    except OSError as error:
        log_file.close()
        raise FixtureError("could not start the run-owned user systemd scope") from error
    log_file.close()
    cleanup_units.append(unit)
    scope = ServerScope(unit, launcher, log_path)
    try:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if launcher.poll() is not None:
                raise FixtureError(f"server systemd launcher exited early ({launcher.returncode})")
            try:
                scope.cgroup = run_cgroup_readings(unit)
                break
            except FixtureError:
                time.sleep(0.1)
        if scope.cgroup is None:
            raise FixtureError("server user systemd scope did not expose its cgroup limits")
        scope.argv = server_args
        return scope
    except BaseException:
        stop_server(scope, cleanup_units)
        raise


def stop_server(scope: ServerScope | None, cleanup_units: list[str]) -> None:
    if scope is None:
        return
    if scope.launcher.poll() is None:
        subprocess.run(
            ["systemctl", "--user", "stop", scope.unit],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=20,
            check=False,
        )
    try:
        scope.launcher.wait(timeout=SERVER_STOP_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        subprocess.run(
            ["systemctl", "--user", "kill", "--kill-who=all", "--signal=KILL", scope.unit],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=15,
            check=False,
        )
        try:
            scope.launcher.wait(timeout=10)
        except subprocess.TimeoutExpired as error:
            raise FixtureError("run-owned server launcher did not stop after scope kill") from error
    if scope.unit in cleanup_units:
        cleanup_units.remove(scope.unit)


def wait_ready(scope: ServerScope, admin_port: int) -> None:
    deadline = time.monotonic() + ADMIN_READY_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        if scope.launcher.poll() is not None:
            raise FixtureError(f"server scope exited before readiness ({scope.launcher.returncode})")
        connection = http.client.HTTPConnection("127.0.0.1", admin_port, timeout=2)
        try:
            connection.request("GET", "/readyz", headers={"Connection": "close"})
            response = connection.getresponse()
            response.read()
            if response.status == 200:
                return
        except OSError:
            pass
        finally:
            connection.close()
        time.sleep(0.1)
    raise FixtureError("local TerraBase admin readiness did not become 200")


def https_request(
    method: str,
    path: str,
    ca_path: Path,
    client_port: int,
    token: str,
    body: bytes | None = None,
) -> tuple[int, bytes]:
    context = ssl.create_default_context(cafile=str(ca_path))
    connection = http.client.HTTPSConnection(
        "127.0.0.1", client_port, context=context, timeout=15
    )
    headers = {
        "Authorization": f"Bearer {token}",
        "Connection": "close",
    }
    if body is not None:
        headers["Content-Type"] = "application/json"
    try:
        connection.request(method, path, body=body, headers=headers)
        response = connection.getresponse()
        return response.status, response.read()
    except (OSError, ssl.SSLError, http.client.HTTPException) as error:
        raise FixtureError(f"verified local HTTPS {method} request failed") from error
    finally:
        connection.close()


def write_seed_body(local_config: Path, run_dir: Path) -> tuple[Path, str]:
    local = json.loads(local_config.read_text())
    binding = next(
        item for item in local["collections"] if item["collection"] == COLLECTION
    )
    if binding["generation"] != "1":
        raise FixtureError("initializer did not bind collection generation 1")
    request = {
        "version": 1,
        "transaction_id": {
            "expires_at_unix_ms": str(int(time.time() * 1000) + 60 * 60 * 1000),
            "nonce": "a1" * 16,
        },
        "writes": [
            {
                "collection": COLLECTION,
                "document": DOCUMENT,
                "schema_generation": binding["generation"],
                "schema_digest": binding["digest"],
                "observed": [],
                "value": {
                    "kind": "live",
                    "fields": [
                        {"id": 1, "value": {"type": "null"}},
                        {"id": 3, "value": {"type": "i64", "value": "73"}},
                    ],
                },
            }
        ],
    }
    path = run_dir / "seed-transaction.json"
    content = write_private_json(path, request)
    return path, sha256_bytes(content)


def document_path(client_port: int) -> str:
    return (
        f"/v1/tenants/{TENANT}/databases/{DATABASE}/shards/{SHARD}"
        f"/collections/{COLLECTION}/documents/{DOCUMENT}"
    )


def verify_expected_response(body: bytes) -> dict[str, Any]:
    try:
        response = json.loads(body)
    except json.JSONDecodeError as error:
        raise FixtureError("verified point read returned invalid JSON") from error
    if (
        response.get("version") != 1
        or response.get("state") != "value"
        or len(response.get("siblings", [])) != 1
    ):
        raise FixtureError("verified point read did not return one live v1 sibling")
    fields = response["siblings"][0].get("fields")
    if not isinstance(fields, list):
        raise FixtureError("verified point read omitted its typed sibling fields")
    by_id = {field.get("id"): field.get("value") for field in fields}
    if by_id.get(1) != {"type": "null"} or by_id.get(3) != {"type": "i64", "value": "73"}:
        raise FixtureError("verified point read did not return the seeded null and i64=73 fields")
    return response


def capture_result_files(
    output_dir: Path,
    run_dir: Path,
    *,
    source_manifest: Path,
    fixture_manifest: dict[str, Any],
    server_config: Path,
    local_config: Path,
    expected_response: Path,
    seed_body: Path,
    tls: dict[str, Path],
    token_files: list[Path],
    server_logs: list[Path],
) -> bool:
    if not output_dir.is_dir():
        return False
    shutil.copyfile(source_manifest, output_dir / "server-source-manifest.json")
    shutil.copyfile(server_config, output_dir / "server-client-config.json")
    shutil.copyfile(local_config, output_dir / "local-v1-config.json")
    shutil.copyfile(expected_response, output_dir / "expected-response.json")
    shutil.copyfile(seed_body, output_dir / "seed-transaction.json")
    shutil.copyfile(tls["ca"], output_dir / "client-ca.pem")
    server_logs_clean = True
    for log_path in server_logs:
        if log_path.exists():
            log_bytes = log_path.read_bytes()
            if any(token_path.exists() and token_path.read_bytes().strip() in log_bytes for token_path in token_files):
                fixture_manifest["server_log_redacted"] = True
                server_logs_clean = False
                continue
            shutil.copyfile(log_path, output_dir / log_path.name)
    manifest_bytes = canonical_json(fixture_manifest)
    (output_dir / "fixture-manifest.json").write_bytes(manifest_bytes)
    return server_logs_clean


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--integration-repo", type=Path, required=True)
    parser.add_argument("--server-binary", type=Path, required=True)
    parser.add_argument("--init-binary", type=Path, required=True)
    parser.add_argument("--keygen-binary", type=Path, required=True)
    parser.add_argument("--driver-binary", type=Path, required=True)
    parser.add_argument("--run-id", required=True)
    return parser.parse_args()


def run() -> int:
    args = parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_-]+", args.run_id):
        raise FixtureError("run ID must contain only ASCII letters, digits, hyphens, or underscores")
    signal.signal(signal.SIGTERM, lambda signum, frame: sys.exit(128 + signum))
    benchmark_root = Path(__file__).resolve().parents[3]
    integration_repo = args.integration_repo.resolve(strict=True)
    if Path(git_output(integration_repo, "rev-parse", "--show-toplevel").decode().strip()) != integration_repo:
        raise FixtureError("integration repository path must name a Git worktree root")

    server = require_executable(args.server_binary, "tb-server", static_musl=True)
    initializer = require_executable(args.init_binary, "terrabase-init", static_musl=True)
    keygen = require_executable(args.keygen_binary, "terrabase-keygen", static_musl=True)
    driver = require_executable(args.driver_binary, "HTTPS driver", static_musl=False)
    systemd_run = shutil.which("systemd-run")
    if systemd_run is None or shutil.which("systemctl") is None:
        raise FixtureError("finite user systemd scopes require systemd-run and systemctl")

    fixture_root = Path(__file__).resolve().parent
    fixture_recipe = fixture_root / "initializer-input.json"
    if not fixture_recipe.is_file():
        raise FixtureError("fixture initializer recipe is missing")
    runtime_root = fixture_root / "runtime"
    runtime_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    if runtime_root.is_symlink():
        raise FixtureError("fixture runtime directory must not be a symlink")
    os.chmod(runtime_root, 0o700)
    run_dir = runtime_root / args.run_id
    output_dir = benchmark_root / "bench" / "https-document-point-read" / args.run_id
    if output_dir.exists():
        raise FixtureError(f"benchmark result already exists: {output_dir}")
    run_dir.mkdir(mode=0o700, exist_ok=False)
    os.chmod(run_dir, 0o700)
    cleanup_units: list[str] = []
    token_files = [run_dir / "seed-write-token.txt", run_dir / "benchmark-read-token.txt"]
    atexit.register(cleanup_process_resources, cleanup_units, token_files)
    env = clean_environment(run_dir)

    data_dir = run_dir / "shard-data"
    local_config = run_dir / "local-v1-config.json"
    input_identity = initialize_shard(
        Path(initializer["path"]), fixture_recipe, run_dir, data_dir, local_config, env
    )
    ports = allocate_loopback_ports()
    admin_port, client_port, reservations = ports
    tls = create_tls_material(run_dir, env)
    registry_path = run_dir / "api-key-registry.json"
    server_config_path = run_dir / "client-listener-config.json"
    server_config = write_server_config(
        server_config_path, run_dir, client_port, tls, registry_path
    )

    seed_token_path, read_token_path = token_files
    seed_token, seed_entry = generate_key(
        Path(keygen["path"]), run_dir, seed_token_path.name, ["write"],
        rate_per_second=100, burst=200,
    )
    read_token, read_entry = generate_key(
        Path(keygen["path"]), run_dir, read_token_path.name, ["read"],
        rate_per_second=100, burst=200,
    )
    seed_registry_sha = write_registry(registry_path, [seed_entry, read_entry])
    seed_body_path, seed_body_sha = write_seed_body(local_config, run_dir)
    route = document_path(client_port)

    source_identity = repository_identity(integration_repo)
    source_tree_sha = sha256_bytes(canonical_json(source_identity))
    source_revision = f"{source_identity['head']}+worktree-sha256:{source_tree_sha}"
    source_manifest_path = run_dir / "server-source-manifest.json"
    source_manifest = {
        "schema_version": 1,
        "source_identity": source_identity,
        "source_tree_sha256": source_tree_sha,
        "artifacts": {
            "tb_server": server,
            "terrabase_init": initializer,
            "terrabase_keygen": keygen,
            "https_document_driver": driver,
        },
        "qualification": "live source and binary hashes captured by fixture harness; not a compiler attestation",
    }
    write_private_json(source_manifest_path, source_manifest)

    driver_config_path = run_dir / "workload.json"
    driver_config = {
        "url": f"https://127.0.0.1:{client_port}{route}",
        "ca_pem_path": str(tls["ca"]),
        "expected_response_path": str(run_dir / "expected-response.json"),
        "server_binary_path": server["path"],
        "server_source_revision": source_revision,
        "server_source_manifest_path": str(source_manifest_path),
        "timeout_ms": 10000,
        "inter_request_delay_ms": INTER_REQUEST_DELAY_MS,
    }
    write_private_json(driver_config_path, driver_config)

    start_args = {
        "tb_server": server,
        "data_dir": str(data_dir),
        "memory_limit_bytes": PROCESS_MEMORY_LIMIT_BYTES,
        "memory_headroom_bytes": PROCESS_MEMORY_HEADROOM_BYTES,
        "admin_port": admin_port,
        "client_config": str(server_config_path),
        "local_v1_config": str(local_config),
    }
    server_logs: list[Path] = []
    active_scope: ServerScope | None = None
    cgroup_readings: list[dict[str, str]] = []
    commit_response_path = run_dir / "seed-commit-response.json"
    expected_response_path = run_dir / "expected-response.json"
    result_code = 1
    fixture_manifest: dict[str, Any] = {
        "schema_version": 1,
        "run_id": args.run_id,
        "status": "in_progress",
        "integration_source": source_identity,
        "integration_source_tree_sha256": source_tree_sha,
        "server_source_revision_label": source_revision,
        "source_manifest_sha256": sha256_file(source_manifest_path),
        "artifact_sha256": {name: item["sha256"] for name, item in source_manifest["artifacts"].items()},
        "initializer": input_identity,
        "initializer_input_sha256": sha256_file(fixture_recipe),
        "data_directory": str(data_dir),
        "local_v1_config_sha256": sha256_file(local_config),
        "server_configuration": {
            "admin_listener": f"127.0.0.1:{admin_port}",
            "client_listener": server_config["listen_addr"],
            "client_config_path": str(server_config_path),
            "client_config_sha256": sha256_file(server_config_path),
            "certificate_chain_sha256": sha256_file(tls["chain"]),
            "ca_sha256": sha256_file(tls["ca"]),
            "private_key_sha256": sha256_file(tls["server_key"]),
            "api_key_registry_path": str(registry_path),
            "seed_registry_sha256": seed_registry_sha,
            "benchmark_registry_sha256": None,
            "server_argv": start_args,
        },
        "seed": {
            "collection": COLLECTION,
            "document": DOCUMENT,
            "transaction_path": str(seed_body_path),
            "transaction_sha256": seed_body_sha,
            "commit_response_path": str(commit_response_path),
            "expected_response_path": str(expected_response_path),
        },
        "keys": {
            "seed_key": {"scope_collection": COLLECTION, "permissions": ["write"], "removed_before_measurement": True},
            "benchmark_key": {
                "scope_collection": COLLECTION,
                "permissions": ["read"],
                "rate_per_second": 100,
                "burst": 200,
                "max_in_flight": 1,
            },
        },
        "workload": {
            "samples": 1000,
            "inter_request_delay_ms": INTER_REQUEST_DELAY_MS,
            "configured_offer_cap_rps": 1000 / INTER_REQUEST_DELAY_MS,
            "delay_timing": "after each completed timed sample except the final sample",
        },
        "cgroups": cgroup_readings,
        "rootless_namespace": "not used; both listeners are fixed to IPv4 loopback and require no privileged socket",
    }

    try:
        for reservation in reservations:
            reservation.close()

        active_scope = start_server(
            server, data_dir, local_config, server_config_path, admin_port, run_dir, "seed", cleanup_units
        )
        server_logs.append(active_scope.log_path)
        cgroup_readings.append({"phase": "seed", **(active_scope.cgroup or {})})
        wait_ready(active_scope, admin_port)
        status, commit_body = https_request(
            "POST", f"/v1/tenants/{TENANT}/databases/{DATABASE}/shards/{SHARD}/transactions",
            tls["ca"], client_port, seed_token, seed_body_path.read_bytes(),
        )
        if status != 200:
            raise FixtureError(f"durable seed commit returned HTTP {status}")
        try:
            commit_response = json.loads(commit_body)
        except json.JSONDecodeError as error:
            raise FixtureError("durable seed commit returned invalid JSON") from error
        if commit_response.get("durability") != "local_durable":
            raise FixtureError("seed transaction did not report local_durable")
        write_private(commit_response_path, commit_body + b"\n")
        stop_server(active_scope, cleanup_units)
        active_scope = None

        # Remove the write grant before reopening durable data for measurement.
        benchmark_registry_sha = write_registry(registry_path, [read_entry])
        seed_token_path.unlink()
        fixture_manifest["server_configuration"]["benchmark_registry_sha256"] = benchmark_registry_sha

        active_scope = start_server(
            server, data_dir, local_config, server_config_path, admin_port, run_dir, "measure", cleanup_units
        )
        server_logs.append(active_scope.log_path)
        cgroup_readings.append({"phase": "measure", **(active_scope.cgroup or {})})
        wait_ready(active_scope, admin_port)
        status, expected_body = https_request(
            "GET", route, tls["ca"], client_port, read_token
        )
        if status != 200:
            raise FixtureError(f"post-restart scoped point read returned HTTP {status}")
        expected_json = verify_expected_response(expected_body)
        write_private(expected_response_path, canonical_json(expected_json))
        fixture_manifest["seed"]["post_restart_read_status"] = status
        fixture_manifest["seed"]["post_restart_response_sha256"] = sha256_file(expected_response_path)
        fixture_manifest["status"] = "fixture_ready"

        driver_result_dir = benchmark_root / "bench" / "https-document-point-read" / args.run_id
        driver_result_dir.parent.mkdir(parents=True, exist_ok=True)
        driver_argv = [
            driver["path"],
            "--config", str(driver_config_path),
            "--token-file", str(read_token_path),
            "--bench-dir", str(benchmark_root / "bench"),
            "--run-id", args.run_id,
        ]
        try:
            driver_result = subprocess.run(
                driver_argv,
                cwd=benchmark_root / "https-document",
                env=clean_environment(run_dir),
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=600,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise FixtureError("HTTPS point-read driver did not complete") from error
        if any(token.encode() in driver_result.stdout + driver_result.stderr for token in (seed_token, read_token)):
            raise FixtureError("driver output contained a generated API token; output was withheld")
        write_private(run_dir / "driver.stdout.txt", driver_result.stdout)
        write_private(run_dir / "driver.stderr.txt", driver_result.stderr)
        fixture_manifest["driver"] = {
            "argv": driver_argv,
            "exit_code": driver_result.returncode,
            "binary_sha256": driver["sha256"],
            "output_dir": str(driver_result_dir),
        }
        fixture_manifest["status"] = "passed" if driver_result.returncode == 0 else "failed"

        stop_server(active_scope, cleanup_units)
        active_scope = None
        current_identity = repository_identity(integration_repo)
        fixture_manifest["integration_source_unchanged_during_run"] = current_identity == source_identity
        if current_identity != source_identity:
            fixture_manifest["status"] = "invalid_source_changed_during_run"
            result_code = 1
        logs_clean = capture_result_files(
            driver_result_dir,
            run_dir,
            source_manifest=source_manifest_path,
            fixture_manifest=fixture_manifest,
            server_config=server_config_path,
            local_config=local_config,
            expected_response=expected_response_path,
            seed_body=seed_body_path,
            tls=tls,
            token_files=token_files,
            server_logs=server_logs,
        )
        if not logs_clean:
            fixture_manifest["status"] = "failed_server_log_token_redaction"
            (driver_result_dir / "fixture-manifest.json").write_bytes(canonical_json(fixture_manifest))
            raise FixtureError("server log contained an API token; the log was withheld")
        if driver_result.returncode != 0:
            raise FixtureError(f"HTTPS point-read driver failed ({driver_result.returncode})")
        if current_identity != source_identity:
            raise FixtureError("integration source tree changed during the benchmark run")

        # Preserve the driver's stdout and stderr as reviewable run evidence.
        shutil.copyfile(run_dir / "driver.stdout.txt", driver_result_dir / "driver.stdout.txt")
        shutil.copyfile(run_dir / "driver.stderr.txt", driver_result_dir / "driver.stderr.txt")
        print(driver_result_dir)
        result_code = 0
    finally:
        for reservation in reservations:
            reservation.close()
        if active_scope is not None:
            stop_server(active_scope, cleanup_units)
            if active_scope.log_path not in server_logs:
                server_logs.append(active_scope.log_path)
        for token_path in token_files:
            if token_path.exists():
                token_path.unlink()

        # Successful runs discard the entire run-owned database and private
        # key material after copying non-secret evidence. Failures retain the
        # owner-only directory for investigation, with bearer tokens removed.
        if result_code == 0 and run_dir.exists():
            shutil.rmtree(run_dir)

    return result_code


if __name__ == "__main__":
    try:
        sys.exit(run())
    except FixtureError as error:
        print(f"HTTPS document fixture: {error}", file=sys.stderr)
        sys.exit(1)
