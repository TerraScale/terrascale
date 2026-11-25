import argparse
import base64
import contextlib
import hashlib
import json
import os
import pathlib
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "env"))
from ports import Hold

PORTS = ("port", "internal_port", "postgres_port", "metrics_port", "quic_port")
ACTOR = "01ARZ3NDEKTSV4RRFFQ69G5FAV"


def command(path):
    if path.is_dir():
        dll = path / "terrabase.dll"
        path = dll if dll.is_file() else path / "terrabase"
    if not path.is_file():
        raise FileNotFoundError(path)
    return ["dotnet", str(path)] if path.suffix == ".dll" else [str(path)]


def process_environment():
    return {
        key: value for key, value in os.environ.items()
        if not key.startswith(("TERRABASE_", "TS_", "AWS_")) and key not in ("BUCKET_NAME", "DOTNET_STARTUP_HOOKS")
    } | {
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_PROCESSOR_COUNT": "4",
        "TERRABASE_AUTH_ISSUER": "https://auth.invalid/",
        "TERRABASE_AUTH_AUDIENCE": "https://api.invalid/",
        "TERRABASE_MASTER_KEY": base64.b64encode(secrets.token_bytes(32)).decode(),
        "TERRABASE_PEER_SECRET": secrets.token_hex(32),
    }


def log_tail(log):
    return f"{log}\n{log.read_text()[-4000:]}"


@contextlib.contextmanager
def node(binary, directory, label, work, environment, ports, identity=None):
    env = environment | {"TERRABASE_DATA_DIRECTORY": str(directory)} | {
        f"TERRABASE_{name.upper()}": str(port) for name, port in ports.items()
    }
    if identity is not None:
        name, region, peers = identity
        env.update(DOTNET_ENVIRONMENT="Production", TS_NODE=name, TS_REGION=region, TS_PEERS_FILE=str(peers))
    log = work / f"{label}.log"
    with log.open("w") as output, subprocess.Popen(binary, env=env, stdout=output, stderr=output) as child:
        succeeded = False
        try:
            yield child, log
            succeeded = True
        finally:
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=35)
                finally:
                    if child.poll() is None:
                        child.kill()
                        child.wait()
                if succeeded:
                    assert child.returncode == 0, log_tail(log)


def wait_for(processes, condition, description):
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        for child, log in processes:
            if child.poll() is not None:
                raise RuntimeError(log_tail(log))
        if condition():
            return
        time.sleep(0.05)
    raise TimeoutError(description + "\n" + "\n".join(log_tail(log) for _, log in processes))


def ready(port):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/readyz", timeout=2) as response:
            return json.load(response)["status"] == "ok"
    except (OSError, urllib.error.HTTPError):
        return False


def internal(port, path, headers, method="GET", body=None, actor=False):
    args = [
        "curl", "--http2-prior-knowledge", "--noproxy", "*", "--silent", "--show-error", "--fail-with-body",
        "--max-time", "15", "--request", method, "--header", "@" + str(headers),
        f"http://127.0.0.1:{port}{path}",
    ]
    if actor:
        args += ["--header", f"X-TerraBase-User-Id: {ACTOR}"]
    if body is not None:
        args += ["--header", "Content-Type: application/json", "--data-binary", "@-"]
    result = subprocess.run(args, input=json.dumps(body), text=True, capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(f"{method} {path}: {result.stderr.strip()} {result.stdout}")
    return json.loads(result.stdout) if result.stdout else None


def seed(port, headers):
    organization = internal(port, "/v1/management/organizations", headers, "POST", {"name": "Format proof"}, actor=True)["id"]
    project_route = f"/v1/management/organizations/{organization}/projects"
    project = internal(port, project_route, headers, "POST", {"name": "Format proof"}, actor=True)["id"]
    databases_route = f"/v1/projects/{project}/databases"
    database = internal(port, databases_route, headers, "POST", {"name": "format-proof"}, actor=True)["id"]
    sql = f"{databases_route}/{database}/sql/execute"
    internal(port, sql, headers, "POST", {
        "statements": [{"sql": "CREATE TABLE evidence (id INTEGER PRIMARY KEY)"}, {"sql": "INSERT INTO evidence VALUES (42)"}],
    })
    return sql


def prove_data(port, headers, sql):
    result = internal(port, sql, headers, "POST", {"statements": [{"sql": "SELECT id FROM evidence"}]})
    assert result["results"][0]["rows"] == [[42]], result


def file_digests(root):
    digests = {}
    for path in root.rglob("*"):
        if path.is_file():
            with path.open("rb") as source:
                digests[path.relative_to(root)] = hashlib.file_digest(source, "sha256").hexdigest()
    return digests


def prove_local(candidate, incompatible, legacy, work, environment, ports, headers, local_format, incompatible_format):
    directory = work / "data"
    marker = directory / ".storage-format"
    with node(legacy, directory, "seed", work, environment, ports) as started:
        wait_for([started], lambda: ready(ports["port"]), "seed node did not become ready")
        sql = seed(ports["internal_port"], headers)
    assert not marker.exists(), "the legacy node unexpectedly wrote a format marker"
    with node(candidate, directory, "adopt", work, environment, ports) as adopted:
        wait_for([adopted], lambda: ready(ports["port"]), "candidate did not adopt the legacy data directory")
    assert marker.read_text() == local_format
    with node(candidate, directory, "restart", work, environment, ports) as restarted:
        wait_for([restarted], lambda: ready(ports["port"]), "candidate did not become ready")
        health = internal(ports["internal_port"], "/readyz", headers)
        assert health["storageFormat"] == local_format, health
        prove_data(ports["internal_port"], headers, sql)
    assert marker.read_text() == local_format
    print(f"RESULT clean v2 restart preserved SQL data at {local_format}", flush=True)

    before = file_digests(directory)
    with node(incompatible, directory, "local-refusal", work, environment, ports) as (child, log):
        assert child.wait(timeout=20) == 2, log_tail(log)
        refusal = log.read_text()
        assert all(text in refusal for text in ("storage_format_mismatch", local_format, incompatible_format)), log_tail(log)
    assert file_digests(directory) == before, "refused startup changed data-directory files"
    print("RESULT mismatched startup refused before changing data-directory files", flush=True)


def peer_events(log, node_id):
    events = []
    for line in log.read_text().splitlines(keepends=True):
        start = line.find("{")
        if start >= 0 and line.endswith("\n"):
            event = json.loads(line[start:])
            if event.get("NodeId") == node_id:
                events.append(event)
    return events


def refusal_events(events, formats):
    return [
        event["event_id"] for event in events
        if event.get("event_id") in (8, 10)
        and event["level"] == "WARN"
        and all(text in event["Reason"] for text in ("storage_format_mismatch", *formats))
    ]


def prove_peers(left, right, work, environment, ports, label, formats=None):
    local, remote = ports
    peers = work / f"{label}.peers"
    peers.write_text(
        f"format-left gru 127.0.0.1 {local['quic_port']} {local['internal_port']}\n"
        f"format-right fra 127.0.0.1 {remote['quic_port']} {remote['internal_port']}\n"
    )
    with (
        node(left, work / f"{label}-left", f"{label}-left", work, environment, local, ("format-left", "gru", peers)) as first,
        node(
            right, work / f"{label}-right", f"{label}-right", work, environment, remote, ("format-right", "fra", peers),
        ) as second,
    ):
        peers_to_check = ((first[1], "format-right"), (second[1], "format-left"))

        def observed():
            if not (ready(local["port"]) and ready(remote["port"])):
                return False
            events = [peer_events(log, peer) for log, peer in peers_to_check]
            if formats is None:
                return all(
                    any(event.get("event_id") == 2109 and event["Current"] == "Healthy" for event in peer)
                    for peer in events
                )
            return all(
                any(event.get("event_id") == 2109 and event["Current"] == "Unreachable" for event in peer)
                and set(refusal_events(peer, formats)) == {8, 10}
                for peer in events
            )

        wait_for([first, second], observed, f"{label} peer health did not reach the expected state")
        if formats is not None:
            for log, peer in peers_to_check:
                events = peer_events(log, peer)
                assert all(
                    event.get("event_id") not in (3, 7) and event.get("Current") != "Healthy" for event in events
                ), log_tail(log)
                assert sorted(refusal_events(events, formats)) == [8, 10], log_tail(log)

    print(f"RESULT {label} peer handshake verified in both directions", flush=True)


def main():
    parser = argparse.ArgumentParser(description="Rehearse the TerraBase v2 storage format fence with real builds.")
    parser.add_argument("candidate", type=pathlib.Path, help="Fenced TerraBase executable, DLL, or build directory")
    parser.add_argument("incompatible", type=pathlib.Path, help="Fenced build with a different StorageFormat.Id")
    parser.add_argument("work", type=pathlib.Path, help="New directory for the disposable data and logs")
    parser.add_argument("--legacy", type=pathlib.Path, required=True, help="Unfenced v2 build with the candidate's format ID")
    args = parser.parse_args()
    candidate = command(args.candidate.resolve())
    incompatible = command(args.incompatible.resolve())
    legacy = command(args.legacy.resolve())
    work = args.work.resolve()
    work.mkdir(mode=0o700)
    environment = process_environment()

    def format_id(binary):
        return subprocess.check_output([*binary, "--storage-format"], env=environment, text=True, timeout=15).strip()

    local_format = format_id(candidate)
    incompatible_format = format_id(incompatible)
    assert local_format and incompatible_format and local_format != incompatible_format, "builds must report different format IDs"
    assert format_id(legacy) == local_format, "legacy build must report the candidate's format ID"
    headers = work / "peer.headers"
    headers.touch(mode=0o600)
    headers.write_text("X-TerraBase-Peer-Credential: " + environment["TERRABASE_PEER_SECRET"] + "\n")

    held = Hold({f"{side}_{name}": 0 for side in ("local", "remote") for name in PORTS})
    ports = tuple({name: held.ports[f"{side}_{name}"] for name in PORTS} for side in ("local", "remote"))
    prove_local(candidate, incompatible, legacy, work, environment, ports[0], headers, local_format, incompatible_format)
    prove_peers(candidate, candidate, work, environment, ports, "matching")
    prove_peers(candidate, legacy, work, environment, ports, "rolling-upgrade")
    prove_peers(candidate, incompatible, work, environment, ports, "mismatched", (local_format, incompatible_format))


if __name__ == "__main__":
    main()
