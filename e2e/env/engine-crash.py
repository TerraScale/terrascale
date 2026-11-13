import base64
import concurrent.futures
import fcntl
import hashlib
import json
import os
import pathlib
import secrets
import shutil
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time

cases = {}


def case(name):
    def register(function):
        assert name not in cases, "duplicate crash case: " + name
        cases[name] = function
        return function
    return register


os.umask(0o077)
harness_started = time.perf_counter()
root = pathlib.Path.cwd()
build = (
    pathlib.Path(os.environ["E2E_BUILD"]) / "terrabase"
    if "E2E_BUILD" in os.environ else root / "src/TerraBase/bin/Release/net11.0"
)
baseline = pathlib.Path(sys.argv[1]).resolve()
state = pathlib.Path(tempfile.mkdtemp(prefix="engine-crash-"))
capacity = shutil.disk_usage(state)
assert capacity.used + 24_000_000_000 < capacity.total * 0.8
ports = []
for _ in range(4):
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        ports.append(sock.getsockname()[1])
peer = secrets.token_hex(32)
headers = state / "headers"
headers.write_text("X-TerraBase-Peer-Credential: " + peer + "\n")
headers.chmod(0o600)
user = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
env = {
    **{
        key: value
        for key, value in os.environ.items()
        if not key.startswith(("TERRABASE_", "TS_", "AWS_")) and key != "BUCKET_NAME"
    },
    "DOTNET_ROOT": os.environ["DOTNET_ROOT"],
    "DOTNET_PROCESSOR_COUNT": "4",
    "DOTNET_ENVIRONMENT": "Development",
    "TERRABASE_AUTH_ISSUER": "https://auth.invalid/",
    "TERRABASE_AUTH_AUDIENCE": "https://api.invalid/",
    "TERRABASE_DATA_DIRECTORY": str(state / "data") + "/",
    "TERRABASE_PEER_SECRET": peer,
    "DOTNET_STARTUP_HOOKS": str(state / "hook.dll"),
    "ENGINE_CRASH_SOCKET": str(state / "hook.sock"),
    "TERRABASE_MASTER_KEY": base64.b64encode(secrets.token_bytes(32)).decode(),
    "TERRABASE_PORT": str(ports[0]),
    "TERRABASE_INTERNAL_PORT": str(ports[1]),
    "TERRABASE_METRICS_PORT": str(ports[2]),
    "TERRABASE_POSTGRES_PORT": str(ports[3]),
}
node = None
generation = 0
reader = None
idle_observed = []
idle_drained = threading.Event()
proof_finished = threading.Condition()
proof_events = 0
rollback_count = 0
proof_held = threading.Event()
proof_reads = 0
publication_late = threading.Event()
fault_waiting = threading.Event()
proof_failed = []


def request(method, path, body=None, actor=True):
    command = [
        "curl",
        "--http2-prior-knowledge",
        "--silent",
        "--show-error",
        "--max-time",
        "35",
        "-X",
        method,
        "-H",
        "@" + str(headers),
        "-w",
        "\n%{http_code}",
        "http://127.0.0.1:" + str(ports[1]) + path,
    ]
    if actor:
        command += ["-H", "X-TerraBase-User-Id: " + user]
    if body is not None:
        command += ["-H", "Content-Type: application/json", "--data-binary", "@-"]
    result = subprocess.run(command, input=json.dumps(body) if body is not None else None, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError("curl " + str(result.returncode) + ": " + result.stderr.strip())
    text, status = result.stdout.rsplit("\n", 1)
    if int(status) >= 400:
        raise RuntimeError(status + " " + text)
    return json.loads(text) if text else None


def start(point=None, failure=None, binary=None, directory=None, proof_target=None, integrity=False, checkpoint_target=None):
    global node, generation, reader, proof_events, proof_reads
    generation += 1
    proof_reads = 0
    current = dict(env)
    if directory is not None:
        current["TERRABASE_DATA_DIRECTORY"] = str(directory) + "/"
    if proof_target:
        current.update(
            LD_PRELOAD=str(state / "crash.so"),
            ENGINE_PROOF_TARGET=proof_target,
            ENGINE_PROOF_CONTROL=str(state / "proof-control"),
        )
    if checkpoint_target:
        current.update(
            LD_PRELOAD=str(state / "crash.so"),
            ENGINE_CHECKPOINT_TARGET=checkpoint_target,
            ENGINE_CHECKPOINT_CONTROL=str(state / "checkpoint-control"),
        )
    if integrity:
        current.update(
            ENGINE_INTEGRITY_PROXY=str(state / "integrity.so"),
            ENGINE_INTEGRITY_CONTROL=str(state / "integrity-control"),
        )
    if point:
        current.update(LD_PRELOAD=str(state / "crash.so"), ENGINE_CRASH_POINT=point)
    node = subprocess.Popen(
        [
            "nice", "-n", "19", env["DOTNET_ROOT"] + "/dotnet",
            str(binary or build / "terrabase.dll"),
        ],
        env=current,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    log = state / ("node-" + str(generation) + ".log")
    ready = threading.Event()
    hook_ready = threading.Event()
    process = node

    def drain():
        global proof_events, proof_reads
        with log.open("wb") as output:
            while line := process.stdout.readline():
                output.write(line)
                output.flush()
                if (
                    b"Database restart proof:" in line
                    or b"recovery proof is incomplete" in line
                    or b"pending database recovery proof completed" in line
                ):
                    with proof_finished:
                        proof_events += 1
                        proof_finished.notify_all()
                if b"Idle change history retained" in line:
                    retained_rows = json.loads(line.decode().split(">", 1)[1])["RetainedRows"]
                    idle_observed.append(retained_rows)
                    if retained_rows < 32:
                        idle_drained.set()
                if b"open latency:" in line:
                    print(line.decode().strip(), flush=True)
                if b"proof tenant file read" in line:
                    proof_reads += 1
                if b"still waiting for a modification in flight" in line:
                    publication_late.set()
                if b"A copy fault waits for storage capacity" in line:
                    fault_waiting.set()
                if b"recovery proof failed and will retry" in line or b"Recovery proof retries stopped;" in line:
                    with proof_finished:
                        proof_failed.append(line.decode().strip())
                        proof_finished.notify_all()
                if b"proof file claim held" in line:
                    proof_held.set()
                if b"engine crash hook ready" in line:
                    hook_ready.set()
                if b"Application started" in line:
                    ready.set()
            ready.set()
            hook_ready.set()

    reader = threading.Thread(target=drain)
    reader.start()
    if ready.wait(600):
        if failure is not None:
            node.wait(timeout=15)
            reader.join(timeout=5)
            assert failure in log.read_text(), log.read_text()[-5000:]
            assert node.returncode != 0
            return
        if hook_ready.wait(10) and node.poll() is None:
            return
    raise RuntimeError("node did not start: " + log.read_text()[-5000:])


def end(hard=False, rollback=True):
    global node, rollback_count
    if node is not None:
        node.send_signal(signal.SIGKILL if hard else signal.SIGTERM)
        try:
            exit_code = node.wait(timeout=15)
            if hard:
                assert exit_code == -signal.SIGKILL
        finally:
            if node.poll() is None:
                node.kill()
                node.wait()
            reader.join(timeout=5)
            node.stdout.close()
            node = None
        if hard and rollback:
            snapshot = state / "rollback"
            shutil.copytree(state / "data", snapshot)
            try:
                start(binary=baseline, directory=snapshot)
                rollback_count += 1
                print("origin/main startup on crash directory: ready", flush=True)
                end()
            finally:
                shutil.rmtree(snapshot)


def sql(project, database, statements):
    return request(
        "POST",
        f"/v1/projects/{project}/databases/{database}/sql/execute",
        {"statements": [{"sql": statement} for statement in statements]},
        actor=False,
    )


def crash_request(point, method, path, body=None):
    start(point)
    with concurrent.futures.ThreadPoolExecutor() as executor:
        pending = (
            executor.submit(hook, "rewrite", database)
            if point.startswith("rewrite")
            else executor.submit(request, method, path, body)
        )

        def expired(signum, frame):
            raise TimeoutError("the node did not reach the crash boundary")

        previous = signal.signal(signal.SIGALRM, expired)
        signal.alarm(40)
        try:
            _, status = os.waitpid(node.pid, os.WUNTRACED)
            assert os.WIFSTOPPED(status), "node exited before reaching the boundary"
            keys = list((state / "data" / organization / project).glob("*.db.key"))
            assert len(keys) == 1
            assert pathlib.Path(str(keys[0])[:-4]).exists() == point.startswith("rewrite")
            print(
                point + ": main " + ("present" if point.startswith("rewrite") else "absent")
                + ", key present at filesystem boundary",
                flush=True,
            )
        finally:
            signal.alarm(0)
            signal.signal(signal.SIGALRM, previous)
            end(hard=True)
        try:
            pending.result(timeout=5)
            raise AssertionError("interrupted request unexpectedly returned success")
        except RuntimeError as error:
            assert str(error).startswith(("curl ", "hook interrupted")), str(error)
    print("kill -KILL node at " + point + ": exit -9", flush=True)


def hook(operation, database_id, count=None):
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(600)
        client.connect(env["ENGINE_CRASH_SOCKET"])
        message = f"{operation} {organization} {project} {database_id}" + (f" {count}" if count is not None else "")
        client.sendall((message + "\n").encode())
        result = client.recv(65536).decode().strip()
        if not result:
            raise RuntimeError("hook interrupted by process exit")
        if operation in ("ledger", "pending", "refused"):
            return [int(value) for value in result.split()]
        if operation == "idle-pass":
            return result
        assert result == "ok", result


def files(database_id):
    path = state / "data" / organization / project
    return {
        file.name: (file.stat().st_size, hashlib.sha256(file.read_bytes()).hexdigest())
        for file in path.glob(database_id + ".db*")
    }


def gauge(name):
    metrics = subprocess.check_output(["curl", "-fsS", f"http://127.0.0.1:{ports[2]}/metrics"], text=True)
    return sum(float(line.rsplit(" ", 1)[1]) for line in metrics.splitlines() if line.startswith((name + " ", name + "{")))


def unavailable(database_id, code):
    try:
        sql(project, database_id, ["SELECT 1;"])
        raise AssertionError("HTTP read served an unavailable database")
    except RuntimeError as error:
        assert "503" in str(error) and code in str(error), str(error)
    with Postgres(database_id) as client:
        response = client.query("SELECT 1;")
        assert response["C"] == "57P03" and code in response["M"], response


class Postgres:
    def __init__(self, database_id):
        self.startup_error = None
        self.socket = socket.create_connection(("127.0.0.1", ports[3]), timeout=35)
        parameters = f"user\0probe\0database\0{project}/{database_id}\0\0".encode()
        self.socket.sendall(struct.pack("!II", len(parameters) + 8, 196608) + parameters)
        kind, body = self.receive()
        assert kind == b"R" and struct.unpack("!I", body)[0] == 3
        self.send(b"p", api_key.encode() + b"\0")
        while True:
            kind, body = self.receive()
            if kind == b"E":
                self.startup_error = {field[:1].decode(): field[1:].decode() for field in body.split(b"\0") if field}
                return
            if kind == b"Z":
                break

    def receive(self):
        def exact(size):
            result = b""
            while len(result) < size:
                data = self.socket.recv(size - len(result))
                assert data, "Postgres connection closed"
                result += data
            return result
        header = exact(5)
        return header[:1], exact(struct.unpack("!I", header[1:])[0] - 4)

    def send(self, kind, body):
        self.socket.sendall(kind + struct.pack("!I", len(body) + 4) + body)

    def query(self, statement):
        if self.startup_error is not None:
            return self.startup_error
        self.send(b"Q", statement.encode() + b"\0")
        return self.result()

    def result(self):
        error = {}
        while True:
            kind, body = self.receive()
            if kind == b"E":
                error = {field[:1].decode(): field[1:].decode() for field in body.split(b"\0") if field}
            if kind == b"Z":
                return error

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.socket.close()


def wait_for_proofs():
    deadline = time.monotonic() + 90
    with proof_finished:
        while hook("pending", "*") != [0]:
            remaining = deadline - time.monotonic()
            assert remaining > 0, "proofs did not finish"
            proof_finished.wait(remaining)


def recovered(identities):
    wait_for_proofs()
    for identity in identities:
        assert hook("ledger", identity) == [0, 0]
        assert sql(project, identity, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
        with Postgres(identity) as client:
            assert client.query("SELECT 1;") == {}


def crash_recording(identity, operation="fault"):
    hook("arm-recording", identity)
    crash_hook(operation, identity)


def crash_hook(operation, identity):
    with concurrent.futures.ThreadPoolExecutor() as executor:
        pending = executor.submit(hook, operation, identity)

        def expired(signum, frame):
            raise TimeoutError(operation + " did not reach its stop boundary")

        previous = signal.signal(signal.SIGALRM, expired)
        signal.alarm(40)
        try:
            _, status = os.waitpid(node.pid, os.WUNTRACED)
            assert os.WIFSTOPPED(status), "node exited before the " + operation + " stop boundary"
        finally:
            signal.alarm(0)
            signal.signal(signal.SIGALRM, previous)
            end(hard=True, rollback=False)
        try:
            pending.result(timeout=5)
            raise AssertionError(operation + " returned before its killed boundary")
        except RuntimeError as error:
            assert str(error).startswith("hook interrupted"), str(error)


@case("latency")
def latency_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "open-latency"})["id"]
    for _ in range(3):
        hook("latency", identity)
    end()


@case("native")
def native_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "native-barrier"})["id"]
    end()
    release = state / "checkpoint-control.release"
    os.mkfifo(release)
    start(checkpoint_target=identity + ".db")
    sql(project, identity, [
        "CREATE TABLE native_evidence(id INTEGER PRIMARY KEY);", "INSERT INTO native_evidence VALUES (1);",
    ])
    hook("native-race", identity)
    assert hook("ledger", identity) == [1, 0]
    unavailable(identity, "copy_faulted")
    end()
    release.unlink()
    print("native checkpoint barrier: publication waited for backfill; no later mutation", flush=True)


@case("native-timeout")
def native_timeout_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "native-deadline"})["id"]
    healthy = request("POST", f"/v1/projects/{project}/databases", {"name": "native-healthy"})["id"]
    end()
    release = state / "checkpoint-control.release"
    os.mkfifo(release)
    publication_late.clear()
    start(checkpoint_target=identity + ".db")
    sql(project, identity, [
        "CREATE TABLE native_deadline(id INTEGER PRIMARY KEY);", "INSERT INTO native_deadline VALUES (1);",
    ])
    began = time.perf_counter()
    hook("native-race", identity, "timeout")
    assert hook("ledger", identity) == [0, 1]
    unavailable(identity, "copy_faulted")
    assert publication_late.wait(60)
    elapsed = time.perf_counter() - began
    assert 34 <= elapsed < 45, elapsed
    assert node.poll() is None
    assert gauge("terrabase_storage_fault_publication_late_total") == 1
    request("GET", "/readyz", actor=False)
    assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
    with Postgres(healthy) as client:
        assert client.query("SELECT 1;") == {}
    assert hook("ledger", identity) == [0, 1]
    unavailable(identity, "copy_faulted")
    end(hard=True, rollback=False)
    release.unlink()
    start()
    wait_for_proofs()
    assert hook("ledger", identity) == [1, 0]
    unavailable(identity, "copy_faulted")
    assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
    end()
    print(
        f"stalled native checkpoint: node kept serving past the bound ({elapsed:.3f}s); intent durable during the wait; "
        "killed during the wait, the restart refused the copy with a permanent fault", flush=True,
    )


@case("barrier-no-intent")
def barrier_no_intent_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "barrier-no-intent"})["id"]
    hook("check", identity)
    hook("hold-fault", identity)
    assert hook("ledger", identity) == [0, 1]
    unavailable(identity, "copy_faulted")
    end(hard=True, rollback=False)
    start()
    wait_for_proofs()
    assert hook("ledger", identity) == [1, 0]
    unavailable(identity, "copy_faulted")
    end()
    print("barrier held without a write intent at detection, then kill: restart refused the copy", flush=True)


@case("session-open-kill")
def session_open_kill_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "session-open"})["id"]
    end()
    control = state / "proof-control"
    release = state / "proof-control.release"
    os.mkfifo(release)
    start(proof_target=identity + ".db")
    hook("check", identity)
    hook("close-idle", identity)
    control.write_text("keyhold")
    proof_held.clear()
    with concurrent.futures.ThreadPoolExecutor() as executor:
        reading = executor.submit(sql, project, identity, ["SELECT 1;"])
        assert proof_held.wait(30)
        hook("fault-recorded", identity)
        assert hook("ledger", identity) == [0, 1], hook("ledger", identity)
        assert not reading.done()
        end(hard=True, rollback=False)
    control.unlink()
    release.unlink()
    start()
    wait_for_proofs()
    assert hook("ledger", identity) == [1, 0]
    unavailable(identity, "copy_faulted")
    end()
    print("real session open held the barrier inside the native open, then kill: restart refused the copy", flush=True)


@case("delete-queued")
def delete_queued_case():
    for variant in ("queued", "busy"):
        start()
        identity = request("POST", f"/v1/projects/{project}/databases", {"name": "delete-" + variant})["id"]
        faulted = gauge("terrabase_storage_faulted_databases")
        hook("delete-" + variant, identity)
        assert hook("ledger", identity) == [0, 0], hook("ledger", identity)
        assert gauge("terrabase_storage_faulted_databases") == faulted
        hook("finish-delete", identity)
        assert hook("ledger", identity) == [0, 0]
        assert sql(project, identity, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
        with Postgres(identity) as client:
            assert client.query("SELECT 1;") == {}
        end()
        print(
            f"fault record after a deletion forgot ({variant}): no fault row, gauge unchanged; same-id creation healthy",
            flush=True,
        )


@case("busy-retry")
def busy_retry_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "busy-retry"})["id"]
    healthy = request("POST", f"/v1/projects/{project}/databases", {"name": "busy-healthy"})["id"]
    hook("check", identity)
    hook("check", healthy)
    fault_waiting.clear()
    hook("hold-writer", "journal")
    hook("fault-async", identity)
    assert fault_waiting.wait(90)
    unavailable(identity, "copy_faulted")
    request("GET", "/readyz", actor=False)
    assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
    with Postgres(healthy) as client:
        assert client.query("SELECT 1;") == {}
    assert hook("ledger", identity) == [0, 0]
    hook("release-writer", "journal")
    hook("published", identity)
    assert hook("ledger", identity) == [1, 0]
    assert gauge("terrabase_storage_faulted_databases") == hook("ledger", "*")[0]
    end()
    print("busy intent store: copy refused, node and other copies served; released writer made the fault durable", flush=True)


@case("shutdown-busy")
def shutdown_busy_case():
    for kind in ("write", "read"):
        start()
        identity = request("POST", f"/v1/projects/{project}/databases", {"name": "shutdown-busy-" + kind})["id"]
        hook("check", identity)
        if kind == "write":
            hook("intent", identity)
        fault_waiting.clear()
        hook("hold-writer", "journal")
        hook("fault-async", identity)
        assert fault_waiting.wait(90)
        hook("stop-retries", "*")
        end()
        start()
        wait_for_proofs()
        assert hook("ledger", identity) == [1, 0], hook("ledger", identity)
        unavailable(identity, "copy_faulted")
        end()
        print(f"clean shutdown during a busy fault retry (damage found by a {kind}): restart refused the copy", flush=True)


@case("proof-isolation")
def proof_isolation_case():
    start()
    waiting = request("POST", f"/v1/projects/{project}/databases", {"name": "proof-isolated"})["id"]
    hook("intent", waiting)
    end(hard=True, rollback=False)
    key = state / "data" / organization / project / (waiting + ".db.key")
    key.chmod(0)
    try:
        start()
        assert gauge("terrabase_storage_proof_pending") == 1
        proof_failed.clear()
        hook("poison-proof", "*")
        with proof_finished:
            assert proof_finished.wait_for(lambda: proof_failed, 40)
        hook("unpoison-proof", "*")
    finally:
        key.chmod(0o600)
    wait_for_proofs()
    hook("check", waiting)
    end()
    print(
        "one proof retry failed unclassified (" + proof_failed[0][-80:] + "); the other pending proof still completed",
        flush=True,
    )


@case("orphan-key")
def orphan_key_case():
    start()
    existing = request("POST", f"/v1/projects/{project}/databases", {"name": "orphan-neighbour"})["id"]
    sql(project, existing, ["CREATE TABLE kept(id INTEGER PRIMARY KEY);", "INSERT INTO kept VALUES (1);"])
    end()
    directory = state / "data" / organization / project
    orphan = "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(26))
    orphan = "0" + orphan[1:]
    kept = (directory / (existing + ".db.key")).read_bytes()
    (directory / (orphan + ".db.key")).write_bytes(kept)
    start()
    assert not (directory / (orphan + ".db.key")).exists()
    retained = list(directory.glob("." + orphan + ".*.retained-key"))
    assert len(retained) == 1 and retained[0].read_bytes() == kept
    assert gauge("terrabase_storage_retained_keys") == len(list((state / "data").rglob(".*.retained-key")))
    assert (directory / (existing + ".db.key")).read_bytes() == kept
    hook("create", orphan)
    assert (directory / (orphan + ".db")).exists()
    assert (directory / (orphan + ".db.key")).read_bytes() != kept
    assert sql(project, existing, ["SELECT id FROM kept;"])["results"][0]["rows"] == [[1]]
    end()
    start()
    assert list(directory.glob("." + orphan + ".*.retained-key")) == retained and retained[0].read_bytes() == kept
    assert (directory / (orphan + ".db")).exists()
    end()
    print(
        "orphan key without main, log or creation retained at start as " + retained[0].name
        + " with identical bytes and kept across restart; same-id creation succeeded; existing key kept",
        flush=True,
    )


@case("delete-race")
def delete_race_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "delete-race"})["id"]
    hook("delete-race", identity)
    assert hook("ledger", identity) == [0, 0]
    assert sql(project, identity, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
    with Postgres(identity) as client:
        assert client.query("SELECT 1;") == {}
    end()
    print("deletion while fault publication waited: no fault row; same-key creation healthy", flush=True)


@case("delete-forget")
def delete_forget_case():
    for point in ("delete", "deleting"):
        start()
        identity = request("POST", f"/v1/projects/{project}/databases", {"name": "forget-" + point})["id"]
        hook("fault", identity)
        assert hook("ledger", identity) == [1, 0]
        end()
        start(point)
        crash_hook("delete", identity)
        start()
        wait_for_proofs()
        assert not list((state / "data" / organization / project).glob(identity + ".db*"))
        assert hook("ledger", identity) == [0, 0]
        assert gauge("terrabase_storage_faulted_databases") == hook("ledger", "*")[0]
        end()
        print(f"kill at the {point} boundary of a deletion: next start ended deleted and forgotten", flush=True)


@case("memory")
def memory_case():
    start()
    waiting = request("POST", f"/v1/projects/{project}/databases", {"name": "memory-pending"})["id"]
    faulted = request("POST", f"/v1/projects/{project}/databases", {"name": "memory-fault"})["id"]
    healthy = request("POST", f"/v1/projects/{project}/databases", {"name": "memory-healthy"})["id"]
    hook("intent", waiting)
    end(hard=True, rollback=False)
    key = state / "data" / organization / project / (waiting + ".db.key")
    key.chmod(0)
    try:
        start()
        assert gauge("terrabase_storage_proof_pending") == 1
        hook("check", healthy)
        hook("check", faulted)
        hook("saturate", "*")
        with proof_finished:
            before = proof_events
        hook("fault", faulted)
        with proof_finished:
            assert proof_finished.wait_for(lambda: proof_events > before, 40)
        request("GET", "/readyz", actor=False)
        assert hook("refused", healthy) == [0]
        assert hook("ledger", faulted) == [1, 0]
        assert hook("ledger", waiting) == [0, 1]
        assert gauge("terrabase_storage_proof_pending") == 1
        hook("unsaturate", "*")
        assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
        with Postgres(healthy) as client:
            assert client.query("SELECT 1;") == {}
        unavailable(faulted, "copy_faulted")
        unavailable(waiting, "recovery_pending")
    finally:
        key.chmod(0o600)
    wait_for_proofs()
    hook("check", waiting)
    end()
    print(
        "memory budget saturated by tenants: fault recorded and pending proof retried; node ready, "
        "healthy copy admitted, affected copies refused", flush=True,
    )


@case("idle-cost")
def idle_cost_case():
    start()
    hook("many", "*", 1000)
    end()
    start()

    def memory():
        status = dict(line.split(":", 1) for line in pathlib.Path(f"/proc/{node.pid}/status").read_text().splitlines())
        return int(status["VmRSS"].split()[0]) // 1024, int(status["VmHWM"].split()[0]) // 1024

    resident, _ = memory()
    for label in ("first pass after restart", "steady pass"):
        result = hook("idle-pass", "*")
        current, peak = memory()
        print(
            f"idle pass with 1000 idle databases, {label}: {result}; resident {current} MiB "
            f"(+{current - resident} MiB since start), peak {peak} MiB", flush=True,
        )
    end()


@case("recreate")
def recreate_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "ordinary-recreate"})["id"]
    hook("fault", identity)
    hook("recreate", identity)
    assert hook("ledger", identity) == [0, 0]
    assert sql(project, identity, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
    with Postgres(identity) as client:
        assert client.query("SELECT 1;") == {}
    end()
    print(
        "ordinary explicit deletion: new creation at the same path healthy over HTTP and PostgreSQL immediately", flush=True,
    )


@case("opened-key")
def opened_key_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "opened-key"})["id"]
    healthy = request("POST", f"/v1/projects/{project}/databases", {"name": "healthy-key"})["id"]
    hook("check", identity)
    key = state / "data" / organization / project / (identity + ".db.key")
    key.chmod(0)
    try:
        hook("fault", identity)
        assert hook("ledger", identity) == [1, 0]
        assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
        with Postgres(healthy) as client:
            assert client.query("SELECT 1;") == {}
        assert request("GET", "/readyz", actor=False) is not None
    finally:
        key.chmod(0o600)
    hook("recreate", identity)
    assert hook("ledger", identity) == [0, 0]
    assert sql(project, identity, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
    with Postgres(identity) as client:
        assert client.query("SELECT 1;") == {}
    end()
    print(
        "opened unreadable key: fault persisted, healthy HTTP/PostgreSQL and readiness served; "
        "explicit deletion and same-location recreation served immediately", flush=True,
    )


@case("key-retry")
def key_retry_case():
    global proof_reads
    start(integrity=True)
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "key-retry"})["id"]
    healthy = request("POST", f"/v1/projects/{project}/databases", {"name": "key-retry-healthy"})["id"]
    lease = request("POST", f"/v1/projects/{project}/databases", {"name": "key-retry-lease"})["id"]
    hook("first-open", identity, 11)
    end(hard=True, rollback=False)
    control = state / "proof-control"
    control.write_text("io")
    start(proof_target=identity + ".db.key")
    assert hook("ledger", identity) == [0, 1]
    release = pathlib.Path(str(control) + ".release")
    os.mkfifo(release)
    proof_held.clear()
    proof_reads = 0
    control.write_text("keyhold")
    assert proof_held.wait(15), "the fault binding retry did not enter its key read"
    try:
        began = time.perf_counter()
        hook("migration-lease", lease)
        hook("fault", healthy)
        elapsed = time.perf_counter() - began
        assert elapsed < 5, elapsed
        assert hook("ledger", healthy) == [1, 0]
    finally:
        control.write_text("pass")
        descriptor = os.open(release, os.O_WRONLY | os.O_NONBLOCK)
        os.write(descriptor, b"x")
        os.close(descriptor)
    wait_for_proofs()
    assert proof_reads == 1, ("creation evidence was read again during commit", proof_reads)
    assert hook("ledger", identity) == [1, 0]
    control.unlink()
    release.unlink()
    end()
    print(
        f"held fault-binding key: independent fault and migration lease {elapsed:.3f}s; "
        "exactly one evidence read outside commit", flush=True,
    )


@case("first-open")
def first_open_case():
    for code in (10, 11, 26):
        start(integrity=True)
        identity = request("POST", f"/v1/projects/{project}/databases", {"name": "first-open-" + str(code)})["id"]
        hook("first-open", identity, code)
        assert hook("ledger", identity) == [0, 1]
        before = files(identity)
        unavailable(identity, "copy_faulted")
        hook("migration-refused", identity)
        assert hook("ledger", identity) == [0, 1]
        assert files(identity) == before
        end(hard=True, rollback=False)
        start()
        unavailable(identity, "copy_faulted")
        hook("migration-refused", identity)
        assert hook("ledger", identity) == [1, 0]
        assert files(identity) == before
        healthy = request("POST", f"/v1/projects/{project}/databases", {"name": "healthy-after-" + str(code)})["id"]
        assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
        assert request("GET", "/readyz", actor=False) is not None
        end()
        print(
            f"first session {code}: durable identity-free marker refused reopen, migration and overwrite; "
            "permanent after restart",
            flush=True,
        )


@case("creation")
def creation_case():
    start(integrity=True)
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "creation-bound"})["id"]
    replacement = request("POST", f"/v1/projects/{project}/databases", {"name": "different-creation"})["id"]
    hook("first-open", identity, 11)
    end()
    directory = state / "data" / organization / project
    for file in directory.glob(identity + ".db*"):
        file.unlink()
    for suffix in ("", ".key", "-wal"):
        source = directory / (replacement + ".db" + suffix)
        if source.exists():
            shutil.copyfile(source, directory / (identity + ".db" + suffix))
    before = files(identity)
    start()
    unavailable(identity, "recovery_pending")
    assert hook("ledger", identity) == [0, 1], "the old intent faulted a different creation"
    hook("migration-refused", identity)
    assert files(identity) == before
    end()
    start()
    assert hook("ledger", identity) == [0, 1]
    assert files(identity) == before
    request("DELETE", f"/v1/projects/{project}/databases/{identity}")
    assert hook("ledger", identity) == [0, 0]
    end()
    print(
        "different creation at same location: no inherited permanent fault; intent retained until explicit deletion",
        flush=True,
    )


@case("checkpoints")
def checkpoints_case():
    for kind in ("eviction", "close"):
        start()
        identity = request("POST", f"/v1/projects/{project}/databases", {"name": kind + "-race"})["id"]
        sql(project, identity, ["CREATE TABLE evidence(id INTEGER PRIMARY KEY);", "INSERT INTO evidence VALUES (1);"])
        hook(kind + "-race", identity)
        before = files(identity)
        unavailable(identity, "copy_faulted")
        end()
        assert files(identity) == before
        print(kind + " queued checkpoint refused after concurrent fault; WAL byte-identical including close", flush=True)


@case("independent")
def independent_case():
    start()
    copies = [
        request("POST", f"/v1/projects/{project}/databases", {"name": "independent-" + str(index)})["id"]
        for index in range(3)
    ]
    for identity in copies[:2]:
        hook("intent", identity)
    end(hard=True, rollback=False)
    control = state / "proof-control"
    control.write_text("io")
    second_key = state / "data" / organization / project / (copies[1] + ".db.key")
    second_key.chmod(0)
    start(proof_target=copies[0] + ".db")
    assert gauge("terrabase_storage_proof_pending") == 2
    assert request("GET", "/readyz", actor=False) is not None
    release = pathlib.Path(str(control) + ".release")
    os.mkfifo(release)
    proof_held.clear()
    control.write_text("hold")
    assert proof_held.wait(15), "the background proof did not acquire its copy claim"
    try:
        second_key.chmod(0o600)
        deadline = time.monotonic() + 15
        with proof_finished:
            while hook("ledger", copies[1]) != [0, 0]:
                remaining = deadline - time.monotonic()
                assert remaining > 0, "another proof waited behind the held copy"
                proof_finished.wait(remaining)
        recorded = time.perf_counter()
        hook("fault", copies[2])
        elapsed = time.perf_counter() - recorded
        assert elapsed < 5, elapsed
        assert hook("ledger", copies[2]) == [1, 0]
        assert hook("ledger", copies[0]) == [0, 1]
        unavailable(copies[0], "recovery_pending")
        assert request("GET", "/readyz", actor=False) is not None
        print(
            f"held throwing copy: other proof completed, readiness stayed ready, third fault recorded in {elapsed:.3f}s",
            flush=True,
        )
    finally:
        control.write_text("io")
        descriptor = os.open(release, os.O_WRONLY | os.O_NONBLOCK)
        try:
            os.write(descriptor, b"x")
        finally:
            os.close(descriptor)
        second_key.chmod(0o600)
    control.unlink()
    recovered(copies[:2])
    end()


@case("migration")
def migration_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "migration-refused"})["id"]
    hook("fault", identity)
    end()
    before = files(identity)
    start()
    hook("migration-refused", identity)
    unavailable(identity, "copy_faulted")
    assert files(identity) == before
    end()
    print("faulted unopened copy: direct migration refused without reading or changing its files", flush=True)


@case("unknown-key")
def unknown_key_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "unknown-key"})["id"]
    healthy = request("POST", f"/v1/projects/{project}/databases", {"name": "unknown-key-healthy"})["id"]
    key = state / "data" / organization / project / (identity + ".db.key")
    key.chmod(0)
    try:
        crash_recording(identity, "unopened-fault")
    finally:
        key.chmod(0o600)
    before = files(identity)
    snapshot = state / "unavailable-rollback"
    shutil.copytree(state / "data", snapshot)
    try:
        start(binary=baseline, directory=snapshot)
        assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
        assert request("GET", "/readyz", actor=False) is not None
        end()
    finally:
        shutil.rmtree(snapshot)
    for _ in range(2):
        start()
        assert hook("ledger", identity) == [0, 1]
        unavailable(identity, "recovery_pending")
        assert sql(project, healthy, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
        assert request("GET", "/readyz", actor=False) is not None
        assert files(identity) == before
        end()
    start()
    hook("recreate", identity)
    assert hook("ledger", identity) == [0, 0]
    assert sql(project, identity, ["SELECT 1;"])["results"][0]["rows"] == [[1]]
    end()
    print(
        "unopened unreadable key: unavailable binding survived two restarts; "
        "rollback startup healthy; only that copy pending until explicit deletion", flush=True,
    )


@case("marker")
def marker_case():
    start()
    identity = request("POST", f"/v1/projects/{project}/databases", {"name": "recording-marker"})["id"]
    sql(project, identity, ["CREATE TABLE intact(id INTEGER PRIMARY KEY);", "INSERT INTO intact VALUES (1);"])
    crash_recording(identity)
    before = files(identity)
    start()
    assert hook("ledger", identity) == [1, 0], "fault marker was discarded"
    unavailable(identity, "copy_faulted")
    assert files(identity) == before
    end()
    start()
    unavailable(identity, "copy_faulted")
    assert hook("ledger", identity) == [1, 0]
    assert files(identity) == before
    end()
    print(
        "fault marker flushed, killed before fault row: permanent fault before proof; refused across two restarts", flush=True
    )


@case("integrity")
def integrity_case():
    for code in (11, 26):
        start()
        identity = request("POST", f"/v1/projects/{project}/databases", {"name": "integrity-" + str(code)})["id"]
        hook("intent", identity)
        end(hard=True, rollback=False)
        before = files(identity)
        control = state / "integrity-control"
        control.write_text(str(code))
        start(integrity=True)
        counts = hook("ledger", identity)
        proof_log = (state / ("node-" + str(generation) + ".log")).read_text()
        assert f"injected integrity exception {code}" in proof_log, proof_log
        assert counts == [0, 1], (counts, [line for line in proof_log.splitlines() if "proof" in line or "injected" in line])
        unavailable(identity, "recovery_pending")
        hook("migration-refused", identity)
        assert gauge("terrabase_storage_proof_pending") == 1
        metrics = subprocess.check_output(["curl", "-fsS", f"http://127.0.0.1:{ports[2]}/metrics"], text=True)
        assert 'reason="integrity_engine_corruption"' in metrics, metrics
        assert files(identity) == before
        end(hard=True, rollback=False)
        start(integrity=True)
        assert hook("ledger", identity) == [0, 1]
        unavailable(identity, "recovery_pending")
        assert files(identity) == before
        control.unlink()
        recovered([identity])
        end()
        print(
            f"integrity throws {code}: no fault, retained intent and files, pending refusal across restart; "
            "reason integrity_engine_corruption; healthy after removal", flush=True
        )


@case("admission")
def admission_case():
    start()
    request("PUT", f"/v1/management/organizations/{organization}/plan", {"plan": "enterprise"})
    marked = request("POST", f"/v1/projects/{project}/databases", {"name": "marked"})["id"]
    with Postgres(marked) as existing:
        hook("tap-open", marked)
        sql(project, marked, ["CREATE TABLE captured(id INTEGER PRIMARY KEY);", "INSERT INTO captured VALUES (1);"])
        assert existing.query("BEGIN;") == {}
        assert existing.query("SELECT 1;") == {}
        hook("fault", marked)
        hook("tap-refused", marked)
        before = files(marked)
        response = existing.query("SELECT 1;")
        assert response["C"] == "57P03" and "copy_faulted" in response["M"], response
        unavailable(marked, "copy_faulted")
        assert hook("ledger", marked) == [1, 0]
        assert files(marked) == before, (before, files(marked))
    end()
    assert files(marked) == before, ("shutdown", before, files(marked))
    start()
    unavailable(marked, "copy_faulted")
    assert hook("ledger", marked) == [1, 0]
    assert gauge("terrabase_storage_faulted_databases") == hook("ledger", "*")[0] >= 1
    assert files(marked) == before, (before, files(marked))
    print(
        "FAULTED: existing Postgres transaction and new SELECT -> 57P03 copy_faulted; HTTP read -> 503 "
        "copy_faulted; files/key unchanged; fault gauge survives restart",
        flush=True,
    )
    end()

    for kind in ("locked", "io"):
        start()
        identity = request("POST", f"/v1/projects/{project}/databases", {"name": "proof-" + kind})["id"]
        hook("intent", identity)
        end(hard=True, rollback=False)
        before = files(identity)
        control = state / "proof-control"
        control.write_text(kind)
        lock = (state / "data" / organization / project / (identity + ".db")).open("rb")
        if kind == "locked":
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        start(proof_target=identity + ".db" if kind == "io" else None)
        unavailable(identity, "recovery_pending")
        assert hook("ledger", identity) == [0, 1]
        assert gauge("terrabase_storage_proof_pending") == 1
        assert files(identity) == before
        control.unlink()
        lock.close()
        recovered([identity])
        print(
            f"{kind}: INCOMPLETE, no fault, intent retained, files/key unchanged; PostgreSQL 57P03 "
            "recovery_pending and HTTP 503 recovery_pending; PASSED after removal, both protocols read",
            flush=True,
        )
        end()

    start()
    unreadable = [
        request("POST", f"/v1/projects/{project}/databases", {"name": f"key-{index}"})["id"]
        for index in range(24)
    ]
    for identity in unreadable:
        hook("intent", identity)
    end(hard=True, rollback=False)
    before = {identity: files(identity) for identity in unreadable}
    for identity in unreadable:
        (state / "data" / organization / project / (identity + ".db.key")).chmod(0)
    try:
        start()
        assert gauge("terrabase_storage_proof_pending") == 24
        for identity in unreadable:
            unavailable(identity, "recovery_pending")
            assert hook("ledger", identity) == [0, 1]
            path = state / "data" / organization / project
            assert {file.name: file.stat().st_size for file in path.glob(identity + ".db*")} == {
                name: value[0] for name, value in before[identity].items()
            }
        end()
        start()
        assert gauge("terrabase_storage_proof_pending") == 24
        for identity in unreadable:
            assert hook("ledger", identity) == [0, 1]
    finally:
        for identity in unreadable:
            (state / "data" / organization / project / (identity + ".db.key")).chmod(0o600)
    for identity in unreadable:
        assert files(identity) == before[identity]
    recovered(unreadable)
    print(
        "24 unreadable keys: INCOMPLETE, zero new faults, 24 intents and unchanged files; each protocol "
        "unavailable; all 24 PASSED and readable after key access restored",
        flush=True,
    )
    end()

    start()
    empty = request("POST", f"/v1/projects/{project}/databases", {"name": "empty-after-kill"})["id"]
    hook("hold", empty)
    end(hard=True, rollback=False)
    path = state / "data" / organization / project / (empty + ".db")
    assert pathlib.Path(str(path) + "-wal").exists()
    path.write_bytes(b"")
    before = files(empty)
    start()
    unavailable(empty, "copy_faulted")
    assert hook("ledger", empty) == [1, 0]
    assert files(empty) == before
    print(
        "zero-length main with WAL after kill: DAMAGED; main/WAL/key unchanged; PostgreSQL 57P03 "
        "copy_faulted, HTTP 503 copy_faulted",
        flush=True,
    )
    end()


@case("recovery")
def recovery_case():
    start()
    with Postgres(database) as client:
        assert client.query("SELECT 1;") == {}
    print("PostgreSQL authentication and healthy SELECT: ready", flush=True)
    print("create organization, project, database under trailing-separator data directory: 201", flush=True)
    print(
        json.dumps(
            sql(
                project,
                database,
                [
                    "CREATE TABLE durable (id INTEGER PRIMARY KEY, value TEXT NOT NULL);",
                    "INSERT INTO durable VALUES (1, 'acknowledged');",
                ],
            )
        ),
        flush=True,
    )
    end(hard=True)
    print("kill -KILL node: exit -9 after acknowledged write", flush=True)
    start()
    result = sql(project, database, ["SELECT id, value FROM durable;"])
    print("restart SELECT: " + json.dumps(result), flush=True)
    assert result["results"][0]["rows"] == [[1, "acknowledged"]]
    for index in range(2, 102):
        sql(project, database, [f"INSERT INTO durable VALUES ({index}, 'bounded');"])
    metrics = subprocess.check_output(["curl", "-fsS", f"http://127.0.0.1:{ports[2]}/metrics"], text=True)
    retained = [line for line in metrics.splitlines() if line.startswith("terrabase_cdc_retained_rows{")]
    assert len(retained) == 1 and float(retained[0].rsplit(" ", 1)[1]) <= 32
    assert sql(project, database, ["SELECT count(*) FROM durable;"])["results"][0]["rows"] == [[101]]
    print("101 acknowledged rows, no backup tap: " + retained[0], flush=True)
    end()

    crash_request("rewrite-intent", "", "")
    start()
    assert sql(project, database, ["SELECT count(*) FROM durable;"])["results"][0]["rows"] == [[101]]
    print("rewrite before rename restart: copy healthy; 101 acknowledged rows remain", flush=True)
    end()

    crash_request("rewrite", "", "")
    start()
    try:
        sql(project, database, ["SELECT count(*) FROM durable;"])
        raise AssertionError("a single-value intent cannot prove the replacement lineage")
    except RuntimeError as error:
        assert "fault" in str(error), str(error)
    print(
        "rewrite after rename: single-value writer retained; replacement lineage unavailable; "
        "copy refuses pending later writer release", flush=True,
    )
    end()

    crash_request("delete", "DELETE", f"/v1/projects/{project}/databases/{database}")
    start()
    assert not list((state / "data" / organization / project).glob("*.db*"))
    assert hook("ledger", database) == [0, 0]
    print("delete restart: no database or sidecars remain", flush=True)
    end()
    retained_before = len(list((state / "data" / organization / project).glob(".*.retained-key")))
    crash_request("publish", "POST", f"/v1/projects/{project}/databases", {"name": "half"})
    start()
    records = request("GET", f"/v1/projects/{project}/databases")
    assert not list((state / "data").glob("*/*/.*.creating*"))
    assert len(list((state / "data" / organization / project).glob(".*.retained-key"))) == retained_before + 1
    for file in (state / "data" / organization / project).glob("*.db.key"):
        assert pathlib.Path(str(file)[:-4]).exists()
    for file in (state / "data" / organization / project).glob("*.db"):
        assert pathlib.Path(str(file) + ".key").exists()
    print(
        "publish restart: no half pairs or creation scratch remain; the unpublished key is retained; catalog="
        + json.dumps(records),
        flush=True,
    )
    end()
    start()
    request_database = request("POST", f"/v1/projects/{project}/databases", {"name": "healthy"})["id"]
    sql(project, request_database, ["CREATE TABLE healthy (id INTEGER PRIMARY KEY);", "INSERT INTO healthy VALUES (1);"])
    end()

    start()
    idle = "0" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(25))
    hook("backlog", idle)
    end()
    start()
    idle_observed.clear()
    idle_drained.clear()
    assert idle_drained.wait(150), idle_observed
    metrics = subprocess.check_output(["curl", "-fsS", f"http://127.0.0.1:{ports[2]}/metrics"], text=True)
    retained = [float(line.rsplit(" ", 1)[1]) for line in metrics.splitlines() if line.startswith("terrabase_cdc_retained_rows{")]
    assert len(idle_observed) >= 2 and retained and retained[0] < 32, (idle_observed, retained)
    print(
        "idle history after restart, no tenant writes or opens: retained-row gauge " + str(idle_observed)
        + " final " + str(retained), flush=True,
    )
    end()

    databases = ["0" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(25)) for _ in range(24)]
    start()
    for index, identity in enumerate(databases):
        hook("populate", identity, 200 if index < 20 else 1000)
    end()
    for sample in range(3):
        start()
        for identity in databases:
            hook("replay", identity)
            hook("intent", identity)
        end(hard=True)
        sizes = [(state / "data" / organization / project / (identity + ".db")).stat().st_size for identity in databases]
        wal_sizes = [(state / "data" / organization / project / (identity + ".db-wal")).stat().st_size for identity in databases]
        assert min(wal_sizes) >= 16 * 1024 * 1024, wal_sizes
        for file in (state / "data").rglob("*"):
            if file.is_file() and (file.suffix == ".db" or file.name.endswith("-wal")):
                with file.open("rb") as handle:
                    os.posix_fadvise(handle.fileno(), 0, 0, os.POSIX_FADV_DONTNEED)
        began = time.perf_counter()
        start()
        elapsed = time.perf_counter() - began
        recovery_log = (state / ("node-" + str(generation) + ".log")).read_text()
        proofs = [json.loads(line.split(">", 1)[1]) for line in recovery_log.splitlines() if "Restart proofs" in line]
        checks = [json.loads(line.split(">", 1)[1]) for line in recovery_log.splitlines() if "Database restart proof:" in line]
        assert len(proofs) == 1 and proofs[0]["Count"] == 24, proofs
        assert len(checks) == 24, checks
        for identity in databases:
            hook("check", identity)
        gb = sum(item["Bytes"] for item in checks) / 1e9
        wal_gb = sum(wal_sizes) / 1e9
        replay = sum(item["ReplayMs"] for item in checks) / 1000
        integrity = sum(item["IntegrityMs"] for item in checks) / 1000
        print(
            f"recovery sample {sample + 1}: 24 open intents, main files {min(sizes)}..{max(sizes)} bytes, {gb:.3f} GB; "
            f"recovery phase {proofs[0]['TotalMs'] / 1000:.3f}s; process launch to ready {elapsed:.3f}s; "
            f"replay {replay:.3f}s ({replay / wal_gb:.3f}s/WAL-GB over {wal_gb:.3f} GB WAL); "
            f"integrity {integrity:.3f}s ({integrity / gb:.3f}s/GB); "
            f"max replay {max(item['ReplayMs'] for item in checks) / 1000:.3f}s; "
            f"ledger/key overhead {proofs[0]['TotalMs'] / 1000 - replay - integrity:.3f}s",
            flush=True,
        )
        print(
            f"recovery checks sample {sample + 1}: median {proofs[0]['MedianMs']:.3f}ms, "
            f"p99 {proofs[0]['P99Ms']:.3f}ms, max {proofs[0]['MaxMs']:.3f}ms",
            flush=True,
        )
        end()

    start()
    damaged, inaccessible = databases[:2]
    hook("intent", damaged)
    hook("intent", inaccessible)
    end(hard=True)
    key = state / "data" / organization / project / (damaged + ".db.key")
    original_key = key.read_bytes()
    record = original_key.decode().splitlines()
    wrapped = bytearray(base64.b64decode(record[1]))
    wrapped[-1] ^= 1
    key.write_text(record[0] + "\n" + base64.b64encode(wrapped).decode() + "\n")
    blocked = state / "data" / organization / project / (inaccessible + ".db.key")
    blocked.chmod(0)
    try:
        start()
        assert gauge("terrabase_storage_proof_pending") == 2
        assert hook("ledger", damaged) == [0, 1]
        assert hook("ledger", inaccessible) == [0, 1]
        assert sql(project, request_database, ["SELECT count(*) FROM healthy;"])["results"][0]["rows"] == [[1]]
    finally:
        key.write_bytes(original_key)
        blocked.chmod(0o600)
    wait_for_proofs()
    for identity in (damaged, inaccessible):
        assert hook("ledger", identity) == [0, 0]
        hook("check", identity)
    print(
        "damaged wrapped key and inaccessible key: INCOMPLETE, no faults, intents retained; healthy "
        "database readable; both recover after key repair",
        flush=True,
    )
    end()
    assert rollback_count == 9, rollback_count
    for name in ("_system.db", "_intents.db"):
        path = state / "data" / name
        saved = state / (name + ".saved")
        path.rename(saved)
        start(failure="a required catalog or intent store is missing its main file")
        end()
        assert pathlib.Path(str(path) + ".key").exists()
        assert not path.exists()
        saved_key = state / (name + ".key.saved")
        key = pathlib.Path(str(path) + ".key")
        key.rename(saved_key)
        start(failure="a required catalog or intent store is missing its main file")
        end()
        assert not path.exists()
        saved_key.rename(key)
        saved.rename(path)
        print(name + " missing on populated node: startup refuses with or without key; key retained", flush=True)


references = {}
for pack in ("Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref"):
    for file in (pathlib.Path(env["DOTNET_ROOT"]) / "packs" / pack / "11.0.0/ref/net11.0").glob("*.dll"):
        references[file.name] = file
for file in build.glob("*.dll"):
    references.setdefault(file.name, file)
response = state / "compile.rsp"
response.write_text(
    "\n".join(
        ["-nologo", "-target:library", "-nullable:enable", "-langversion:preview", "-out:" + str(state / "hook.dll")]
        + ["-r:" + str(file) for file in references.values()]
        + [str(root / "e2e/env/engine-crash-hook.cs")]
    )
)
subprocess.run(
    [
        "nice",
        "-n",
        "19",
        env["DOTNET_ROOT"] + "/dotnet",
        env["DOTNET_ROOT"] + "/sdk/11.0.100/Roslyn/bincore/csc.dll",
        "@" + str(response),
    ],
    env={key: value for key, value in env.items() if key != "DOTNET_STARTUP_HOOKS"},
    check=True,
)


subprocess.run(
    ["cc", "-shared", "-fPIC", str(root / "e2e/env/engine-crash.c"), "-o", str(state / "crash.so"), "-ldl"], check=True
)

subprocess.run(
    ["cc", "-shared", "-fPIC", str(root / "e2e/env/engine-crash-integrity.c"),
     "-o", str(state / "integrity.so"), "-Wl,--no-as-needed", str(build / "libturso_sdk_kit.so"),
     "-Wl,-rpath," + str(build), "-ldl", "-pthread"], check=True
)

selected = sys.argv[2].split(",") if len(sys.argv) > 2 else [
    "recovery", "admission", *[name for name in cases if name not in ("recovery", "admission", "idle-cost")], "idle-cost",
]
assert len(selected) == len(set(selected)), "duplicate crash cases"
assert "recovery" not in selected[1:], "the dependent recovery sequence must run first"
assert set(selected) <= cases.keys(), "unknown crash cases: " + str(set(selected) - cases.keys())

try:
    start()
    organization = request("POST", "/v1/management/organizations", {"name": "Crash proof"})["id"]
    project = request("POST", f"/v1/management/organizations/{organization}/projects", {"name": "Crash proof"})["id"]
    database = request("POST", f"/v1/projects/{project}/databases", {"name": "durable"})["id"]
    api_key = request("POST", "/admin/api-keys", {
        "name": "crash protocols", "scopes": ["sql:read", "sql:write"],
        "organizationId": organization, "projectId": project,
    }, actor=False)["secret"]
    if selected[0] != "recovery":
        request("PUT", f"/v1/management/organizations/{organization}/plan", {"plan": "enterprise"})
    end()
    for name in selected:
        began = time.perf_counter()
        cases[name]()
        print(f"crash case {name}: {time.perf_counter() - began:.3f}s", flush=True)
    if "recovery" in selected:
        assert rollback_count == 9, rollback_count
    print(f"crash harness: {time.perf_counter() - harness_started:.3f}s", flush=True)
finally:
    end()
    shutil.rmtree(state)
