import base64
import concurrent.futures
import json
import os
import pathlib
import secrets
import signal
import socket
import subprocess
import tempfile
import threading
import time

os.umask(0o077)
root = pathlib.Path.cwd()
state = pathlib.Path(tempfile.mkdtemp(prefix="engine-crash-"))
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
    "DOTNET_ROOT": str(pathlib.Path.home() / ".local/share/mise/dotnet-root"),
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


def start(point=None, failure=None):
    global node, generation, reader
    generation += 1
    current = dict(env)
    if point:
        current.update(LD_PRELOAD=str(state / "crash.so"), ENGINE_CRASH_POINT=point)
    node = subprocess.Popen(
        ["nice", "-n", "19", env["DOTNET_ROOT"] + "/dotnet", str(root / "src/TerraBase/bin/Release/net11.0/terrabase.dll")],
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
                if b"engine crash hook ready" in line:
                    hook_ready.set()
                if b"Application started" in line:
                    ready.set()
            ready.set()
            hook_ready.set()

    reader = threading.Thread(target=drain)
    reader.start()
    if ready.wait(40):
        if failure is not None:
            node.wait(timeout=15)
            reader.join(timeout=5)
            assert failure in log.read_text(), log.read_text()[-5000:]
            assert node.returncode != 0
            return
        if hook_ready.wait(10) and node.poll() is None:
            return
    raise RuntimeError("node did not start: " + log.read_text()[-5000:])


def end(hard=False):
    global node
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
            executor.submit(hook, "rewrite", database) if point == "rewrite" else executor.submit(request, method, path, body)
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
            assert pathlib.Path(str(keys[0])[:-4]).exists() == (point == "rewrite")
            print(
                point + ": main " + ("present" if point == "rewrite" else "absent") + ", key present at filesystem boundary",
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


def hook(operation, database_id):
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(35)
        client.connect(env["ENGINE_CRASH_SOCKET"])
        client.sendall(f"{operation} {organization} {project} {database_id}\n".encode())
        result = client.recv(65536).decode().strip()
        if not result:
            raise RuntimeError("hook interrupted by process exit")
        assert result == "ok", result


references = {}
for pack in ("Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref"):
    for file in (pathlib.Path(env["DOTNET_ROOT"]) / "packs" / pack / "11.0.0/ref/net11.0").glob("*.dll"):
        references[file.name] = file
for file in (root / "src/TerraBase/bin/Release/net11.0").glob("*.dll"):
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

try:
    start()
    organization = request("POST", "/v1/management/organizations", {"name": "Crash proof"})["id"]
    project = request("POST", f"/v1/management/organizations/{organization}/projects", {"name": "Crash proof"})["id"]
    database = request("POST", f"/v1/projects/{project}/databases", {"name": "durable"})["id"]
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

    crash_request("rewrite", "", "")
    start()
    assert sql(project, database, ["SELECT count(*) FROM durable;"])["results"][0]["rows"] == [[101]]
    print("rewrite rename restart: copy healthy; 101 acknowledged rows remain", flush=True)
    end()

    crash_request("delete", "DELETE", f"/v1/projects/{project}/databases/{database}")
    start()
    assert not list((state / "data" / organization / project).glob("*.db*"))
    print("delete restart: no database or sidecars remain", flush=True)
    end()
    crash_request("publish", "POST", f"/v1/projects/{project}/databases", {"name": "half"})
    start()
    records = request("GET", f"/v1/projects/{project}/databases")
    assert not list((state / "data").glob("*/*/.*.creating*"))
    for file in (state / "data" / organization / project).glob("*.db.key"):
        assert pathlib.Path(str(file)[:-4]).exists()
    for file in (state / "data" / organization / project).glob("*.db"):
        assert pathlib.Path(str(file) + ".key").exists()
    print("publish restart: no half pairs or creation scratch remain; catalog=" + json.dumps(records), flush=True)
    end()
    start()
    request_database = request("POST", f"/v1/projects/{project}/databases", {"name": "healthy"})["id"]
    sql(project, request_database, ["CREATE TABLE healthy (id INTEGER PRIMARY KEY);", "INSERT INTO healthy VALUES (1);"])
    end()

    databases = ["0" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(25)) for _ in range(50)]
    for sample in range(3):
        start()
        for identity in databases:
            hook("hold", identity)
        end(hard=True)
        sizes = [(state / "data" / organization / project / (identity + ".db")).stat().st_size for identity in databases]
        began = time.perf_counter()
        start()
        elapsed = time.perf_counter() - began
        recovery_log = (state / ("node-" + str(generation) + ".log")).read_text()
        proofs = [json.loads(line.split(">", 1)[1]) for line in recovery_log.splitlines() if "Restart proofs" in line]
        assert len(proofs) == 1 and proofs[0]["Count"] == 50, proofs
        for identity in databases:
            hook("check", identity)
        print(
            f"recovery sample {sample + 1}: 50 open intents, main files {min(sizes)}..{max(sizes)} bytes; "
            f"recovery phase {proofs[0]['TotalMs']:.3f}ms; process launch to ready {elapsed:.3f}s",
            flush=True,
        )
        print(
            f"recovery checks sample {sample + 1}: median {proofs[0]['MedianMs']:.3f}ms, "
            f"p99 {proofs[0]['P99Ms']:.3f}ms, max {proofs[0]['MaxMs']:.3f}ms",
            flush=True,
        )
        end()

    start()
    damaged = databases[0]
    inaccessible = databases[1]
    hook("intent", damaged)
    hook("intent", inaccessible)
    end(hard=True)
    key = state / "data" / organization / project / (damaged + ".db.key")
    record = key.read_text().splitlines()
    wrapped = bytearray(base64.b64decode(record[1]))
    wrapped[-1] ^= 1
    key.write_text(record[0] + "\n" + base64.b64encode(wrapped).decode() + "\n")
    blocked = state / "data" / organization / project / (inaccessible + ".db.key")
    blocked.chmod(0)
    try:
        start()
        assert sql(project, request_database, ["SELECT count(*) FROM healthy;"])["results"][0]["rows"] == [[1]]
        recovery_log = (state / ("node-" + str(generation) + ".log")).read_text()
        faults = [
            json.loads(line.split(">", 1)[1]) for line in recovery_log.splitlines() if "faulted by an unclean ending" in line
        ]
        assert len(faults) == 1 and faults[0]["Count"] == 2, faults
        print("damaged wrapped key and inaccessible key: node starts, two copies faulted, healthy database readable", flush=True)
        end()
    finally:
        blocked.chmod(0o600)

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
    print("proof state: " + str(state), flush=True)
finally:
    end()
