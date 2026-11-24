import base64
import concurrent.futures
import fcntl
import hashlib
import json
import os
import pathlib
import platform
import secrets
import shutil
import signal
import socket
import statistics
import struct
import subprocess
import sys
import tempfile
import threading
import time

import ports as e2e_ports

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
native = build / "runtimes" / ("linux-arm64" if platform.machine() == "aarch64" else "linux-x64") / "native"
baseline = pathlib.Path(sys.argv[1]).resolve()
state = pathlib.Path(tempfile.mkdtemp(prefix="engine-crash-"))
capacity = shutil.disk_usage(state)
assert capacity.used + 24_000_000_000 < capacity.total * 0.8
_ports = e2e_ports.Hold({"client": 0, "internal": 0, "metrics": 0, "postgres": 0, "quic": 0})
ports = [_ports.ports[name] for name in ("client", "internal", "metrics", "postgres", "quic")]
peer = secrets.token_hex(32)
headers = state / "headers"
headers.write_text("X-TerraBase-Peer-Credential: " + peer + "\n")
headers.chmod(0o600)
user = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
master = base64.b64encode(secrets.token_bytes(32)).decode()
system_id = "0" * 26
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
    "TERRABASE_MASTER_KEY": master,
    "TERRABASE_PORT": str(ports[0]),
    "TERRABASE_INTERNAL_PORT": str(ports[1]),
    "TERRABASE_METRICS_PORT": str(ports[2]),
    "TERRABASE_POSTGRES_PORT": str(ports[3]),
    "TERRABASE_QUIC_PORT": str(ports[4]),
}
node = None
generation = 0
reader = None
rollback_count = 0
proof_held = threading.Event()
checkpoint_held = threading.Event()
crash_reached = []


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


def start(point=None, target=None, failure=None, binary=None, directory=None, proof_target=None, integrity=False,
          checkpoint_target=None, keys=None):
    global node, generation, reader
    generation += 1
    current = dict(env)
    if directory is not None:
        current["TERRABASE_DATA_DIRECTORY"] = str(directory) + "/"
    if binary is not None:
        del current["DOTNET_STARTUP_HOOKS"]
    if keys is not None:
        del current["TERRABASE_MASTER_KEY"]
        current["TERRABASE_MASTER_KEYS"] = json.dumps(keys)
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
        current.update(LD_PRELOAD=str(state / "crash.so"), ENGINE_CRASH_POINT=point, ENGINE_CRASH_TARGET=target or "")
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
        with log.open("wb") as output:
            while line := process.stdout.readline():
                output.write(line)
                output.flush()
                if b"open latency:" in line:
                    print(line.decode().strip(), flush=True)
                if b"engine crash point" in line:
                    crash_reached.append(line.decode().strip())
                if b"proof file open held" in line:
                    proof_held.set()
                if b"native checkpoint backfill held" in line:
                    checkpoint_held.set()
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
            try:
                node.wait(timeout=15)
            except subprocess.TimeoutExpired:
                raise AssertionError("startup did not refuse: " + log.read_text()[-2000:]) from None
            reader.join(timeout=5)
            assert failure in log.read_text(), log.read_text()[-5000:]
            assert node.returncode != 0
            return
        if (binary is not None or hook_ready.wait(10)) and node.poll() is None:
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


def sql(database_id, statements):
    return request(
        "POST",
        f"/v1/projects/{project}/databases/{database_id}/sql/execute",
        {"statements": [{"sql": statement} for statement in statements]},
        actor=False,
    )


def rows(database_id, statement):
    return sql(database_id, [statement])["results"][0]["rows"]


def create(name):
    return request("POST", f"/v1/projects/{project}/databases", {"name": name})["id"]


def directory(database_id):
    return state / "data" / "databases" / database_id


def crash_request(point, action, target=None):
    crash_reached.clear()
    start(point, target=target)
    with concurrent.futures.ThreadPoolExecutor() as executor:
        pending = executor.submit(action)

        def expired(signum, frame):
            raise TimeoutError("the node did not reach the " + point + " boundary")

        previous = signal.signal(signal.SIGALRM, expired)
        signal.alarm(60)
        try:
            _, status = os.waitpid(node.pid, os.WUNTRACED)
            assert os.WIFSTOPPED(status), "node exited before reaching the boundary"
        finally:
            signal.alarm(0)
            signal.signal(signal.SIGALRM, previous)
            end(hard=True)
        try:
            pending.result(timeout=5)
            raise AssertionError("interrupted request unexpectedly returned success")
        except RuntimeError as error:
            assert str(error).startswith("curl "), str(error)
    assert crash_reached, "the crash point did not report its boundary"
    print("kill -KILL node at " + point + ": exit -9 (" + crash_reached[-1] + ")", flush=True)


def hook(operation, database_id, count=0):
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(1800)
        client.connect(env["ENGINE_CRASH_SOCKET"])
        client.sendall(f"{operation} {database_id} {count}\n".encode())
        result = b""
        while chunk := client.recv(65536):
            result += chunk
    answer = result.decode().strip()
    if not answer:
        raise RuntimeError("hook interrupted by process exit")
    assert not answer.startswith("error"), answer
    if operation in ("log", "check"):
        return [float(value) for value in answer.split()]
    if operation == "catalog":
        return answer.split()
    return answer


def files(database_id):
    return {
        file.name: (file.stat().st_size, hashlib.sha256(file.read_bytes()).hexdigest())
        for file in directory(database_id).iterdir() if file.is_file() and not file.name.endswith("-shm")
    }


def consistent(database_id):
    watermark, last, entries = hook("log", database_id)
    assert watermark == last, ("commit log and database disagree", watermark, last)
    return int(watermark), int(entries)


def unavailable(database_id, code):
    try:
        sql(database_id, ["SELECT 1;"])
        raise AssertionError("HTTP read served an unavailable database")
    except RuntimeError as error:
        assert str(error).startswith("503 ") and code in str(error), str(error)
    with Postgres(database_id) as client:
        response = client.query("SELECT 1;")
        assert response["C"] == "57P03", response


def healthy(database_id):
    assert rows(database_id, "SELECT 1;") == [[1]]
    with Postgres(database_id) as client:
        assert client.query("SELECT 1;") == {}
    assert request("GET", "/readyz", actor=False)["status"] == "ok"


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


def release(fifo):
    descriptor = os.open(fifo, os.O_WRONLY | os.O_NONBLOCK)
    try:
        os.write(descriptor, b"x")
    finally:
        os.close(descriptor)


def memory():
    status = dict(line.split(":", 1) for line in pathlib.Path(f"/proc/{node.pid}/status").read_text().splitlines())
    return int(status["VmRSS"].split()[0]) // 1024, int(status["VmHWM"].split()[0]) // 1024


@case("latency")
def latency_case():
    start()
    identity = create("open-latency")
    end()
    for _ in range(3):
        start()
        hook("latency", identity)
        end()


@case("native")
def native_case():
    start()
    identity = create("native-checkpoint")
    sql(identity, ["CREATE TABLE payload (id INTEGER PRIMARY KEY, body BLOB);"])
    end()
    fifo = state / "checkpoint-control.release"
    os.mkfifo(fifo)
    checkpoint_held.clear()
    start(checkpoint_target="/" + identity + ".db")
    (state / "checkpoint-control").write_text("armed")
    acknowledged = []

    def fill():
        for _ in range(64):
            sql(identity, ["INSERT INTO payload (body) VALUES (randomblob(1048576));"])
            acknowledged.append(1)

    with concurrent.futures.ThreadPoolExecutor() as executor:
        writing = executor.submit(fill)
        assert checkpoint_held.wait(120), "no WAL checkpoint reached the main file"
        began = time.perf_counter()
        healthy(database)
        seen = rows(identity, "SELECT count(*) FROM payload;")[0][0]
        assert seen in (len(acknowledged), len(acknowledged) + 1), (seen, len(acknowledged))
        elapsed = time.perf_counter() - began
        assert not writing.done()
        end(hard=True)
        try:
            writing.result(timeout=5)
            raise AssertionError("the write held in its checkpoint returned success")
        except RuntimeError as error:
            assert str(error).startswith("curl "), str(error)
    fifo.unlink()
    start()
    assert rows(identity, "SELECT count(*) FROM payload;") == [[len(acknowledged) + 1]]
    consistent(identity)
    end()
    print(
        f"native checkpoint held inside the main-file backfill after {len(acknowledged)} acknowledged MiB rows: other copy, "
        f"PostgreSQL and readiness served and the same copy read in {elapsed:.3f}s; kill during the backfill, rollback "
        "startup ready, restart served every acknowledged row plus the committed in-flight row; commit log = watermark",
        flush=True,
    )


@case("independent")
def independent_case():
    start()
    held = create("held-open")
    other = create("independent-open")
    for identity in (held, other):
        sql(identity, ["CREATE TABLE evidence (id INTEGER PRIMARY KEY);", "INSERT INTO evidence VALUES (1);"])
    end()
    control = state / "proof-control"
    fifo = state / "proof-control.release"
    os.mkfifo(fifo)
    proof_held.clear()
    start(proof_target="/" + held + ".db")
    control.write_text("hold")
    def served():
        assert rows(other, "SELECT id FROM evidence;") == [[1]]
        with Postgres(other) as client:
            assert client.query("SELECT 1;") == {}
        assert request("GET", "/readyz", actor=False)["status"] == "ok"

    with concurrent.futures.ThreadPoolExecutor() as executor:
        reading = executor.submit(rows, held, "SELECT id FROM evidence;")
        try:
            assert proof_held.wait(30), "the held copy did not enter its native open"
            began = time.perf_counter()
            serving = executor.submit(served)
            try:
                serving.result(timeout=5)
            except concurrent.futures.TimeoutError:
                raise AssertionError("another copy's open waited behind the held native open") from None
            elapsed = time.perf_counter() - began
            assert not reading.done()
        finally:
            control.write_text("pass")
            release(fifo)
        assert reading.result(timeout=35) == [[1]]
    control.unlink()
    fifo.unlink()
    end()
    print(
        f"native open of one copy held: another copy opened and served over HTTP and PostgreSQL with readiness in "
        f"{elapsed:.3f}s; the held copy served after release",
        flush=True,
    )


@case("locked")
def locked_case():
    start()
    identity = create("locked-copy")
    sql(identity, ["CREATE TABLE evidence (id INTEGER PRIMARY KEY);", "INSERT INTO evidence VALUES (1);"])
    end()
    before = files(identity)
    with (directory(identity) / (identity + ".db")).open("rb+") as handle:
        fcntl.lockf(handle, fcntl.LOCK_EX)
        start()
        unavailable(identity, "storage_busy")
        healthy(database)
        fcntl.lockf(handle, fcntl.LOCK_UN)
    assert files(identity) == before
    assert rows(identity, "SELECT id FROM evidence;") == [[1]]
    end()
    print(
        "another process held the main file's lock: HTTP 503 storage_busy and PostgreSQL 57P03 after the busy timeout, "
        "files unchanged, other copy and readiness served; served after release without a restart",
        flush=True,
    )


@case("first-open")
def first_open_case():
    start()
    identity = create("first-open")
    sql(identity, ["CREATE TABLE evidence (id INTEGER PRIMARY KEY);", "INSERT INTO evidence VALUES (1);"])
    end()
    control = state / "integrity-control"
    for code in (10, 11, 26):
        before = files(identity)
        control.write_text(f"open {code} /{identity}.db")
        start(integrity=True)
        unavailable(identity, "storage_corrupted")
        assert files(identity) == before
        healthy(database)
        control.unlink()
        assert rows(identity, "SELECT id FROM evidence;") == [[1]]
        consistent(identity)
        end(hard=True, rollback=False)
        start()
        assert rows(identity, "SELECT id FROM evidence;") == [[1]]
        end()
        print(
            f"open error {code} injected into the native open: HTTP 503 storage_corrupted and PostgreSQL 57P03, files "
            "byte-identical, other copy and readiness served; served on the next open and after a kill and restart",
            flush=True,
        )


@case("commit-failure")
def commit_failure_case():
    start(integrity=True)
    identity = create("commit-failure")
    sql(identity, ["CREATE TABLE evidence (id INTEGER PRIMARY KEY);", "INSERT INTO evidence VALUES (1);"])
    control = state / "integrity-control"
    for code in (10, 11, 26):
        watermark, _ = consistent(identity)
        control.write_text(f"commit {code} /{identity}.db")
        try:
            sql(identity, [f"INSERT INTO evidence VALUES ({code});"])
            raise AssertionError("a failed native commit was acknowledged")
        except RuntimeError as error:
            assert str(error)[:1] == "5", str(error)
        assert not control.exists(), "the native commit was not reached"
        assert consistent(identity)[0] == watermark, "the failed commit stayed in the commit log"
        assert rows(identity, "SELECT id FROM evidence ORDER BY id;") == [[1]]
    sql(identity, ["INSERT INTO evidence VALUES (2);"])
    watermark, _ = consistent(identity)
    end(hard=True, rollback=False)
    start()
    assert rows(identity, "SELECT id FROM evidence ORDER BY id;") == [[1], [2]]
    assert consistent(identity)[0] == watermark
    end()
    print(
        "native COMMIT failed with 10, 11 and 26 after the commit log append: refused, log truncated back to the "
        "watermark, row absent; next write continued the sequence; kill and restart kept log and database equal",
        flush=True,
    )


@case("wrong-key")
def wrong_key_case():
    second = base64.b64encode(secrets.token_bytes(32)).decode()
    wrong = base64.b64encode(secrets.token_bytes(32)).decode()
    start(keys={"active": "k1", "keys": {"k0": master, "k1": second}})
    identity = create("second-key")
    sql(identity, ["CREATE TABLE kept (id INTEGER PRIMARY KEY);", "INSERT INTO kept VALUES (1);"])
    end()
    before = files(identity)
    for label, keys, code in (
        ("different material", {"active": "k0", "keys": {"k0": master, "k1": wrong}}, "storage_corrupted"),
        ("missing id", {"active": "k0", "keys": {"k0": master}}, "master_key_missing"),
    ):
        start(keys=keys)
        unavailable(identity, code)
        healthy(database)
        end(hard=True, rollback=False)
        assert files(identity) == before
        print(f"master key {label}: HTTP 503 {code} and PostgreSQL 57P03; files byte-identical after a kill", flush=True)
    start(keys={"active": "k0", "keys": {"k0": master, "k1": second}})
    assert rows(identity, "SELECT id FROM kept;") == [[1]]
    consistent(identity)
    end()
    print("correct master key restored: the copy served its rows; other copies served throughout", flush=True)


@case("unreadable")
def unreadable_case():
    start()
    unreadable = [create(f"unreadable-{index}") for index in range(24)]
    for identity in unreadable:
        sql(identity, ["CREATE TABLE evidence (id INTEGER PRIMARY KEY);", "INSERT INTO evidence VALUES (1);"])
    end()
    before = {identity: files(identity) for identity in unreadable}
    for identity in unreadable:
        (directory(identity) / (identity + ".db")).chmod(0)
    try:
        for _ in range(2):
            start()
            for identity in unreadable:
                unavailable(identity, "storage_corrupted")
            healthy(database)
            end(hard=True, rollback=False)
    finally:
        for identity in unreadable:
            (directory(identity) / (identity + ".db")).chmod(0o600)
    for identity in unreadable:
        assert files(identity) == before[identity]
    start()
    for identity in unreadable:
        assert rows(identity, "SELECT id FROM evidence;") == [[1]]
        consistent(identity)
    end()
    print(
        "24 unreadable main files across two kills: each refused over HTTP and PostgreSQL, files byte-identical, other "
        "copy and readiness served; all 24 served after access was restored",
        flush=True,
    )


@case("empty-main")
def empty_main_case():
    start()
    identity = create("empty-after-kill")
    sql(identity, ["CREATE TABLE evidence (id INTEGER PRIMARY KEY, body BLOB);"])
    for index in range(1, 201):
        sql(identity, [f"INSERT INTO evidence VALUES ({index}, randomblob(1024));"])
    end()
    start()
    sql(identity, ["INSERT INTO evidence VALUES (201, randomblob(1024));"])
    acknowledged = rows(identity, "SELECT id, hex(body) FROM evidence ORDER BY id;")
    watermark, _ = consistent(identity)
    end(hard=True, rollback=False)
    main = directory(identity) / (identity + ".db")
    assert pathlib.Path(str(main) + "-wal").exists()
    checkpointed = main.stat().st_size
    main.write_bytes(b"")
    start()
    assert rows(identity, "SELECT id, hex(body) FROM evidence ORDER BY id;") == acknowledged
    assert rows(identity, "PRAGMA integrity_check;") == [["ok"]]
    assert consistent(identity)[0] == watermark
    healthy(database)
    end()
    print(
        f"main file ({checkpointed} bytes, 200 rows only there) truncated to zero after a kill with one more row in the "
        "WAL: restart rebuilt the copy from its commit log; all 201 acknowledged rows byte-identical, integrity_check ok, "
        "commit log = watermark",
        flush=True,
    )


@case("create-kill")
def create_kill_case():
    crash_request("create", lambda: create("half"))
    start()
    listed = [item["name"] for item in request("GET", f"/v1/projects/{project}/databases")["items"]]
    active = set(hook("catalog", "*"))
    local = {path.name for path in (state / "data" / "databases").iterdir()}
    assert local <= active, ("local copies without a catalog record", sorted(local - active))
    end()
    print("creation killed after its main file: no local copy without a catalog record; catalog=" + json.dumps(listed),
          flush=True)


@case("delete-kill")
def delete_kill_case():
    start()
    identity = create("delete-kill")
    sql(identity, ["CREATE TABLE evidence (id INTEGER PRIMARY KEY);", "INSERT INTO evidence VALUES (1);"])
    end()
    crash_request("delete", lambda: request("DELETE", f"/v1/projects/{project}/databases/{identity}"), target=identity)
    start()
    try:
        request("GET", f"/v1/projects/{project}/databases/{identity}")
        raise AssertionError("the deleted database is still listed")
    except RuntimeError as error:
        assert str(error).startswith("404 "), str(error)
    assert identity not in hook("catalog", "*")
    remaining = sorted(path.name for path in directory(identity).rglob("*")) if directory(identity).exists() else []
    assert not remaining, ("files of the deleted database survived the restart", remaining)
    end()
    print("deletion killed after its first shredded file: next start ended deleted with no files left", flush=True)


@case("missing-catalog")
def missing_catalog_case():
    start()
    sql(database, ["CREATE TABLE kept (id INTEGER PRIMARY KEY);", "INSERT INTO kept VALUES (1);"])
    end()
    catalog = directory(system_id)
    saved = state / "system-catalog.saved"
    catalog.rename(saved)
    start(failure="system catalog")
    end()
    assert not catalog.exists(), ("startup replaced the missing system catalog", sorted(path.name for path in catalog.iterdir()))
    saved.rename(catalog)
    start()
    assert rows(database, "SELECT id FROM kept;") == [[1]]
    end()
    print("system catalog missing on a populated node: startup refuses without creating one; restored catalog served",
          flush=True)


@case("idle-cost")
def idle_cost_case():
    start()
    idle_org = request("POST", "/v1/management/organizations", {"name": "Crash idle"})["id"]
    request("PUT", f"/v1/management/organizations/{idle_org}/plan", {"plan": "enterprise"}, actor=False)
    idle_project = request("POST", f"/v1/management/organizations/{idle_org}/projects", {"name": "Crash idle"})["id"]

    def create_idle(name):
        return request("POST", f"/v1/projects/{idle_project}/databases", {"name": name})["id"]

    def read_idle(identity):
        return request(
            "POST",
            f"/v1/projects/{idle_project}/databases/{identity}/sql/execute",
            {"statements": [{"sql": "SELECT 1;"}]},
            actor=False,
        )["results"][0]["rows"]

    with concurrent.futures.ThreadPoolExecutor(8) as executor:
        idle = list(executor.map(lambda index: create_idle(f"idle-{index}"), range(1000)))
    end()
    start()
    resident, _ = memory()
    for label in ("first pass after restart", "steady pass"):
        began = time.perf_counter()
        with concurrent.futures.ThreadPoolExecutor(8) as executor:
            assert all(result == [[1]] for result in executor.map(read_idle, idle))
        elapsed = time.perf_counter() - began
        current, peak = memory()
        print(
            f"read pass over 1000 idle databases, {label}: {elapsed:.3f}s; {hook('sessions', '*')}; resident {current} MiB "
            f"(+{current - resident} MiB since start), peak {peak} MiB",
            flush=True,
        )
    end()


@case("recovery")
def recovery_case():
    rollbacks = rollback_count
    start()
    with Postgres(database) as client:
        assert client.query("SELECT 1;") == {}
    print("PostgreSQL authentication and healthy SELECT: ready", flush=True)
    print(
        json.dumps(sql(database, [
            "CREATE TABLE durable (id INTEGER PRIMARY KEY, value TEXT NOT NULL);",
            "INSERT INTO durable VALUES (1, 'acknowledged');",
        ])),
        flush=True,
    )
    end(hard=True)
    print("kill -KILL node: exit -9 after acknowledged write", flush=True)
    start()
    result = rows(database, "SELECT id, value FROM durable;")
    print("restart SELECT: " + json.dumps(result), flush=True)
    assert result == [[1, "acknowledged"]]
    for index in range(2, 102):
        sql(database, [f"INSERT INTO durable VALUES ({index}, 'bounded');"])
    assert rows(database, "SELECT count(*) FROM durable;") == [[101]]
    watermark, entries = consistent(database)
    print(f"101 acknowledged rows; commit log contiguous through the watermark {watermark} ({entries} entries)", flush=True)
    sql(database, ["CREATE TABLE segments (id INTEGER PRIMARY KEY, body BLOB);"])
    watermark, _ = consistent(database)
    end()

    crash_request("log-ahead", lambda: sql(database, ["INSERT INTO durable VALUES (102, 'logged');"]), target=database)
    start()
    assert rows(database, "SELECT value FROM durable WHERE id = 102;") == [["logged"]]
    assert consistent(database)[0] == watermark + 1
    print(
        "kill after the commit log fsync and before the database commit: restart replayed the logged write; "
        "commit log = watermark", flush=True,
    )
    end()

    acknowledged = []

    def fill():
        for _ in range(64):
            sql(database, ["INSERT INTO segments (body) VALUES (randomblob(1048576));"])
            acknowledged.append(1)

    crash_request("seal", fill, target=database)
    start()
    assert rows(database, "SELECT count(*) FROM segments;") == [[len(acknowledged) + 1]]
    watermark, entries = consistent(database)
    sql(database, ["INSERT INTO segments (body) VALUES (randomblob(16));"])
    assert consistent(database)[0] == watermark + 1
    sealed = len(list(directory(database).glob("*.tbseg")))
    print(
        f"kill after a commit log segment was sealed and before the next active log existed: restart served "
        f"{len(acknowledged)} acknowledged rows and the logged in-flight row; {sealed} sealed segments, {entries} entries "
        "contiguous; next write continued the sequence", flush=True,
    )
    end()

    databases = []
    start()
    for index in range(24):
        identity = create(f"sample-{index}")
        sql(identity, ["CREATE TABLE payload (id INTEGER PRIMARY KEY, body BLOB);"])
        hook("populate", identity, 100 if index < 20 else 500)
        databases.append(identity)
    end()
    for sample in range(3):
        start()
        for identity in databases:
            for row in range(16):
                updated = sql(identity, [
                    "UPDATE payload SET body = randomblob(1048576) "
                    f"WHERE id = (SELECT id FROM payload ORDER BY id LIMIT 1 OFFSET {row});",
                ])
                assert updated["results"][0]["rows_affected"] == 1, updated
        end(hard=True)
        sizes = [(directory(identity) / (identity + ".db")).stat().st_size for identity in databases]
        logs = sum(file.stat().st_size for identity in databases for file in directory(identity).glob("*.tb*"))
        wal = sum(pathlib.Path(str(directory(identity) / (identity + ".db")) + "-wal").stat().st_size for identity in databases)
        for file in (state / "data").rglob("*"):
            if file.is_file():
                with file.open("rb") as handle:
                    os.posix_fadvise(handle.fileno(), 0, 0, os.POSIX_FADV_DONTNEED)
        began = time.perf_counter()
        start()
        elapsed = time.perf_counter() - began
        checks = [hook("check", identity) for identity in databases]
        for _, watermark, last, _ in checks:
            assert watermark == last, ("commit log and database disagree after recovery", watermark, last)
        opens = sorted(opened for opened, _, _, _ in checks)
        print(
            f"recovery sample {sample + 1}: 24 databases killed after 16 MiB bursts, main files {min(sizes)}..{max(sizes)} "
            f"bytes, {sum(sizes) / 1e9:.3f} GB; commit logs {logs / 1e9:.3f} GB; WAL {wal / 1e6:.1f} MB; process launch "
            f"to ready {elapsed:.3f}s; first open and read total {sum(opens) / 1000:.3f}s, median "
            f"{statistics.median(opens):.3f}ms, p99 {opens[int(len(opens) * 0.99)]:.3f}ms, max {opens[-1]:.3f}ms",
            flush=True,
        )
        end()

    assert rollback_count - rollbacks == 6, rollback_count - rollbacks


try:
    references = {}
    for pack in ("Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref"):
        for file in (pathlib.Path(env["DOTNET_ROOT"]) / "packs" / pack / "11.0.0/ref/net11.0").glob("*.dll"):
            references[file.name] = file
    for file in build.glob("*.dll"):
        references.setdefault(file.name, file)
    usings = state / "usings.cs"
    usings.write_text("".join("global using " + name + ";\n" for name in (
        "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing",
        "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging", "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
        "System.Net.Http.Json", "System.Threading", "System.Threading.Tasks", "TerraScale.Contracts.Consistency",
        "TerraScale.Contracts.Storage",
    )))
    response = state / "compile.rsp"
    response.write_text(
        "\n".join(
            ["-nologo", "-target:library", "-nullable:enable", "-langversion:preview", "-warnaserror", "-nowarn:1701,1702",
             "-out:" + str(state / "hook.dll")]
            + ["-r:" + str(file) for file in references.values()]
            + [str(usings), str(root / "e2e/env/engine-crash-hook.cs")]
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
         "-o", str(state / "integrity.so"), "-Wl,--no-as-needed", str(native / "libsqlite3mc.so"),
         "-Wl,-rpath," + str(native), "-ldl", "-pthread"], check=True
    )

    selected = sys.argv[2].split(",") if len(sys.argv) > 2 else [
        "recovery", *[name for name in cases if name not in ("recovery", "idle-cost")], "idle-cost",
    ]
    assert len(selected) == len(set(selected)), "duplicate crash cases"
    assert "recovery" not in selected[1:], "the dependent recovery sequence must run first"
    assert set(selected) <= cases.keys(), "unknown crash cases: " + str(set(selected) - cases.keys())

    start()
    organization = request("POST", "/v1/management/organizations", {"name": "Crash proof"})["id"]
    request("PUT", f"/v1/management/organizations/{organization}/plan", {"plan": "enterprise"}, actor=False)
    project = request("POST", f"/v1/management/organizations/{organization}/projects", {"name": "Crash proof"})["id"]
    database = create("durable")
    api_key = request("POST", "/admin/api-keys", {
        "name": "crash protocols", "scopes": ["sql:read", "sql:write"],
        "organizationId": organization, "projectId": project,
    }, actor=False)["secret"]
    end()
    for name in selected:
        began = time.perf_counter()
        cases[name]()
        print(f"crash case {name}: {time.perf_counter() - began:.3f}s", flush=True)
    print(f"crash harness: {time.perf_counter() - harness_started:.3f}s", flush=True)
finally:
    end()
    shutil.rmtree(state)
