import base64
import contextlib
import ctypes as c
import json
import os
import pathlib
import re
import secrets
import shutil
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request

previous, candidate, work = (pathlib.Path(arg).resolve() for arg in sys.argv[1:4])
repo = pathlib.Path(__file__).resolve().parents[2]
dotnet = shutil.which("dotnet")
port = 18580
env = os.environ | {
    "DOTNET_ENVIRONMENT": "Development",
    "TERRABASE_MASTER_KEY": base64.b64encode(secrets.token_bytes(32)).decode(),
    "TERRABASE_PEER_SECRET": secrets.token_hex(32),
    "TERRABASE_MEMORY_BUDGET": "67108864",
    "TERRABASE_STORAGE_THREADS": "1",
    "TERRABASE_PORT": str(port),
    "TERRABASE_INTERNAL_PORT": str(port + 1),
    "TERRABASE_POSTGRES_PORT": str(port + 2),
    "TERRABASE_METRICS_PORT": str(port + 3),
}
FIRST = "23-41a88901325a"

library = next(candidate.glob("lib*_sdk_kit.so"))
lib = c.CDLL(str(library))
prefix = library.name[3 : -len("_sdk_kit.so")] + "_"
P = c.c_void_p


class Config(c.Structure):
    _fields_ = [
        ("async_io", c.c_uint64),
        ("path", c.c_char_p),
        ("features", c.c_char_p),
        ("vfs", c.c_char_p),
        ("cipher", c.c_char_p),
        ("key", c.c_char_p),
        ("codec", c.c_char_p),
        ("flags", c.c_uint32),
    ]


def bind(name, args, result=c.c_uint):
    function = getattr(lib, prefix + name)
    function.argtypes = args
    function.restype = result
    return function


native_new = bind("database_new", [c.POINTER(Config), c.POINTER(P), c.POINTER(P)])
native_open = bind("database_open", [P, c.POINTER(P)])
native_connect = bind("database_connect", [P, c.POINTER(P), c.POINTER(P)])
native_prepare = bind("connection_prepare_single", [P, c.c_char_p, c.POINTER(P), c.POINTER(P)])
native_step = bind("statement_step", [P, c.POINTER(P)])
native_io = bind("statement_run_io", [P, c.POINTER(P)])
native_kind = bind("statement_row_value_kind", [P, c.c_size_t])
native_int = bind("statement_row_value_int", [P, c.c_size_t], c.c_int64)
native_bytes = bind("statement_row_value_bytes_ptr", [P, c.c_size_t], P)
native_size = bind("statement_row_value_bytes_count", [P, c.c_size_t], c.c_int64)
native_columns = bind("statement_column_count", [P], c.c_int64)
native_finalize = bind("statement_finalize", [P, c.POINTER(P)])
native_free_statement = bind("statement_deinit", [P], None)
native_close = bind("connection_close", [P, c.POINTER(P)])
native_free_connection = bind("connection_deinit", [P], None)
native_free_database = bind("database_deinit", [P], None)
UNWRAP = (
    "const c = require('node:crypto'), fs = require('node:fs');"
    "const {master, wrapped} = JSON.parse(fs.readFileSync(0, 'utf8'));"
    "const w = Buffer.from(wrapped, 'base64');"
    "const d = c.createDecipheriv('aes-256-gcm', Buffer.from(master, 'base64'), w.subarray(0, 12));"
    "d.setAuthTag(w.subarray(-16));"
    "process.stdout.write(Buffer.concat([d.update(w.subarray(12, -16)), d.final()]).toString('hex'));"
)
PULL = (
    "const http2 = require('node:http2');"
    "const [port, path, secret, accept] = process.argv.slice(1);"
    "const client = http2.connect('http://127.0.0.1:' + port);"
    "const headers = {':method': 'POST', ':path': path, 'content-type': 'application/protobuf',"
    " 'x-terrabase-peer-credential': secret};"
    "if (accept !== '-') headers['terrabase-storage-formats'] = accept;"
    "const request = client.request(headers); let answer = {}; const body = [];"
    "request.on('response', h => answer = {status: h[':status'], format: h['terrabase-storage-format'] ?? null});"
    "request.on('data', b => body.push(b));"
    "request.on('end', () => { const data = Buffer.concat(body); answer.bytes = data.length;"
    " answer.code = (data.toString('latin1').match(/storage_format_[a-z]+/) ?? [null])[0];"
    " console.log(JSON.stringify(answer)); client.close(); });"
    "request.end(Buffer.from([8, 0]));"
)


def check(code, error):
    if code not in (0, 1, 2, 3):
        raise RuntimeError(c.string_at(error).decode() if error else "native call failed")


def sql(path, statement):
    lines = pathlib.Path(str(path) + ".key").read_text().splitlines()
    key = subprocess.check_output(
        ["node", "-e", UNWRAP], input=json.dumps({"master": env["TERRABASE_MASTER_KEY"], "wrapped": lines[1]}).encode()
    )
    config = Config(0, str(path).encode(), b"index_method,encryption", None, b"aegis256", key, None, 0)
    db, connection, error, statement_handle = P(), P(), P(), P()
    check(native_new(c.byref(config), c.byref(db), c.byref(error)), error)
    check(native_open(db, c.byref(error)), error)
    check(native_connect(db, c.byref(connection), c.byref(error)), error)
    try:
        check(native_prepare(connection, statement.encode(), c.byref(statement_handle), c.byref(error)), error)
        rows = []
        while True:
            code = native_step(statement_handle, c.byref(error))
            check(code, error)
            if code == 1:
                return rows
            if code == 3:
                check(native_io(statement_handle, c.byref(error)), error)
            elif code == 2:
                rows.append(
                    [
                        native_int(statement_handle, n)
                        if native_kind(statement_handle, n) == 1
                        else c.string_at(native_bytes(statement_handle, n), native_size(statement_handle, n)).decode()
                        for n in range(native_columns(statement_handle))
                    ]
                )
    finally:
        if statement_handle:
            check(native_finalize(statement_handle, c.byref(error)), error)
            native_free_statement(statement_handle)
        check(native_close(connection, c.byref(error)), error)
        native_free_connection(connection)
        native_free_database(db)


def databases(directory):
    return sorted(
        path
        for path in directory.rglob("*.db")
        if path.parent == directory
        or (
            len(path.relative_to(directory).parts) == 3
            and all(re.fullmatch(r"[0-9A-Z]{26}", part) for part in (path.parent.parent.name, path.parent.name, path.stem))
        )
    )


def stamps(directory):
    return {str(path.relative_to(directory)): sql(path, "PRAGMA user_version;")[0][0] for path in databases(directory)}


def lock(directory):
    return (directory / ".terrabase.lock").read_text()


def ready():
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/readyz", timeout=2) as response:
            return response.status == 200
    except (OSError, urllib.error.HTTPError):
        return False


@contextlib.contextmanager
def node(build, directory, name, extra=None):
    log = work / (name + ".log")
    with log.open("w") as output:
        child = subprocess.Popen(
            ["nice", "-n", "19", dotnet, str(build / "terrabase.dll")],
            env=env | {"TERRABASE_DATA_DIRECTORY": str(directory)} | (extra or {}),
            stdout=output,
            stderr=output,
        )
        try:
            yield child, log
        finally:
            if child.poll() is None:
                child.terminate()
            child.wait(timeout=60)


def wait_ready(child, log, deadline=120):
    started = time.monotonic()
    while time.monotonic() - started < deadline:
        if child.poll() is not None:
            raise RuntimeError(name_tail(log))
        if ready():
            return time.monotonic() - started
        time.sleep(0.05)
    raise RuntimeError("readiness deadline: " + name_tail(log))


def name_tail(log):
    return log.name + ": " + log.read_text()[-2500:]


def start(build, directory, name):
    with node(build, directory, name) as (child, log):
        seconds = wait_ready(child, log)
    return seconds, log.read_text()


def refused(build, directory, name):
    with node(build, directory, name) as (child, log):
        child.wait(timeout=120)
        assert not ready()
    return log.read_text()


def pull(tenant, accept):
    organization, project, database = tenant.parts[-3], tenant.parts[-2], tenant.stem
    path = f"/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/pull-updates"
    output = subprocess.check_output(["node", "-e", PULL, str(port + 1), path, env["TERRABASE_PEER_SECRET"], accept])
    return json.loads(output)


def tenants(directory):
    return [path for path in databases(directory) if path.parent != directory]


def copy(source, name):
    target = work / name
    shutil.rmtree(target, ignore_errors=True)
    shutil.copytree(source, target)
    return target


def result(line):
    print("RESULT " + line, flush=True)


def build_target(target):
    source = work / f"source-{target}"
    output = work / f"build-target-{target}"
    shutil.rmtree(source, ignore_errors=True)
    subprocess.run(
        [
            "rsync",
            "-a",
            "--exclude",
            ".git",
            "--exclude",
            "node_modules",
            "--exclude",
            "bin",
            "--exclude",
            "obj",
            str(repo) + "/",
            str(source) + "/",
        ],
        check=True,
    )
    format_file = next(source.glob("src/TerraBase/Engine/*/StorageFormat.cs"))
    text = format_file.read_text()
    changed = re.sub(r"public const int Target = \d+;", f"public const int Target = {target};", text)
    assert changed != text
    format_file.write_text(changed)
    with (work / f"build-target-{target}.log").open("w") as log:
        subprocess.run(
            [
                "nice",
                "-n",
                "19",
                dotnet,
                "build",
                str(source / "src/TerraBase/TerraBase.csproj"),
                "-c",
                "Release",
                "-o",
                str(output),
            ],
            stdout=log,
            stderr=log,
            check=True,
        )
    return output


GDB = """set pagination off
set confirm off
set print thread-events off
handle SIGSEGV nostop noprint pass
handle SIGXCPU nostop noprint pass
catch syscall fsync fdatasync rename renameat renameat2
commands
silent
python
import os
try:
    pid = gdb.selected_inferior().pid
    number = int(gdb.parse_and_eval("$orig_rax"))
    mode = os.environ['FORMAT_KILL']
    hit = None
    if number in (74, 75) and mode in ('staged', 'journal'):
        path = os.readlink('/proc/' + str(pid) + '/fd/' + str(int(gdb.parse_and_eval("$rdi"))))
        if path.endswith('/_system.db.migrating-wal' if mode == 'staged' else '/.terrabase.lock'):
            hit = path
    elif number in (82, 264, 316) and mode == 'rename' and int(gdb.parse_and_eval("(long)$rax")) == -38:
        path = gdb.parse_and_eval("(char*)" + ("$rdi" if number == 82 else "$rsi")).string()
        if path.endswith('/_system.db.migrating'):
            original = path[:-len('.migrating')]
            stale = [suffix for suffix in ('-wal', '-shm') if os.path.exists(original + suffix)]
            hit = path + ' stale sidecars ' + str(stale)
    if hit:
        print('FORMAT SIGKILL ' + mode + ' ' + hit, flush=True)
        os.kill(pid, 9)
except (OSError, gdb.error):
    pass
end
continue
end
run"""


def kill_at(build, directory, mode):
    script = work / "kill.gdb"
    script.write_text(GDB + "\n")
    log = work / f"kill-{mode}.log"
    with log.open("w") as output:
        subprocess.run(
            ["nice", "-n", "19", "gdb", "-q", "-nx", "-batch", "-x", str(script), "--args", dotnet, str(build / "terrabase.dll")],
            env=env | {"TERRABASE_DATA_DIRECTORY": str(directory), "FORMAT_KILL": mode},
            stdout=output,
            stderr=output,
            timeout=180,
        )
    lines = [line for line in log.read_text().splitlines() if line.startswith("FORMAT SIGKILL")]
    assert lines, name_tail(log)
    return lines[0]


work.mkdir(parents=True, exist_ok=True)
seed = work / "seed"
shutil.rmtree(seed, ignore_errors=True)
start(previous, seed, "seed-previous")
for path in databases(seed):
    sql(path, "CREATE TABLE format_proof (id INTEGER PRIMARY KEY, value TEXT NOT NULL);")
    sql(path, "INSERT INTO format_proof VALUES (1, 'written-by-previous');")
seed_stamps = stamps(seed)
assert lock(seed) == FIRST and set(seed_stamps.values()) == {1}, (lock(seed), seed_stamps)
result(f"seed: previous build created {len(seed_stamps)} files, lock {lock(seed)}, every user_version 1")

plain = copy(seed, "plain-restart")
upgrade = copy(seed, "upgrade")
restart_times, candidate_times = [], []
for attempt in range(3):
    restart_times.append(start(previous, plain, f"plain-restart-{attempt}")[0])
    seconds, log = start(candidate, upgrade, f"candidate-{attempt}")
    candidate_times.append(seconds)
    assert "Storage migration to format" not in log
assert lock(upgrade) == FIRST and stamps(upgrade) == seed_stamps, (lock(upgrade), stamps(upgrade))
result(
    f"1 upgrade: candidate opened previous data, lock still {lock(upgrade)}, "
    f"all {len(seed_stamps)} user_version 1, no migration pass"
)
result(
    f"1 readiness: previous restart median {statistics.median(restart_times):.2f}s {[round(t, 2) for t in restart_times]}; "
    f"candidate median {statistics.median(candidate_times):.2f}s {[round(t, 2) for t in candidate_times]}"
)

start(previous, upgrade, "back-to-previous")
assert all(sql(path, "SELECT value FROM format_proof;") == [["written-by-previous"]] for path in databases(upgrade))
result("2 rollback: previous build ready on the candidate's directory with no undo; every proof row read")

target24 = build_target(24)
stamped = copy(seed, "stamped-24")
seconds, log = start(target24, stamped, "target-24")
assert lock(stamped) == "24" and set(stamps(stamped).values()) == {24}, (lock(stamped), stamps(stamped))
summary = next(json.loads(line[line.index("{") :]) for line in log.splitlines() if "Storage migration to format" in line)
result(f"3 stamp: Target-24 scratch build migrated {len(seed_stamps)} files to 24 and was ready in {seconds:.2f}s")
result(
    f"3 tenant pass: {summary['Migrated']} tenant files migrated, {summary['Failed']} failed, in {summary['ElapsedMs']:.1f} ms"
)
tenant = tenants(stamped)[0]
with node(candidate, stamped, "candidate-on-24") as (child, node_log):
    wait_ready(child, node_log)
    current = pull(tenant, "23-24")
    old_peer = pull(tenant, "-")
assert current["status"] == 200 and current["format"] == "24", current
assert old_peer["status"] == 503 and old_peer["code"] == "storage_format_unsupported", old_peer
assert lock(stamped) == "24" and set(stamps(stamped).values()) == {24}
sql(tenant, "INSERT INTO format_proof VALUES (2, 'written-after-candidate');")
with node(candidate, stamped, "candidate-on-24-again") as (child, node_log):
    wait_ready(child, node_log)
    again = pull(tenant, "23-24")
assert again["status"] == 200 and again["format"] == "24" and again["bytes"] > 0, again
assert sql(tenant, "SELECT count(*) FROM format_proof;") == [[2]]
result(
    f"3 candidate on 24: ready, lock 24 kept, pages served as format 24 ({current['bytes']} bytes) to a 23-24 peer, "
    f"refused {old_peer['status']} {old_peer['code']} to a 23-only peer, files still 24"
)
with node(previous, upgrade, "previous-peer") as (child, node_log):
    wait_ready(child, node_log)
    from_old = pull(tenants(upgrade)[0], "23-24")
assert from_old["status"] == 200 and from_old["format"] is None, from_old
result(
    f"3 from a 23 peer: previous build served {from_old['bytes']} bytes with no format header, which the candidate reads as 23"
)
text = refused(previous, stamped, "previous-on-24")
result("3 previous on 24 refused: " + next(line for line in text.splitlines() if "storage format" in line).strip()[:200])

for mode in ("staged", "journal", "rename"):
    directory = copy(seed, f"kill-{mode}")
    line = kill_at(target24, directory, mode)
    versions = {name: version for name, version in stamps(directory).items() if name in ("_system.db", "_intents.db")}
    record = lock(directory)
    seconds, _ = start(target24, directory, f"kill-{mode}-resume")
    assert lock(directory) == "24" and set(stamps(directory).values()) == {24}
    assert all(sql(path, "SELECT value FROM format_proof;") == [["written-by-previous"]] for path in databases(directory))
    result(
        f"4 kill {mode}: {line.split(' ', 3)[3][-120:]}; after kill lock {record} system files {versions}; "
        f"resumed ready in {seconds:.2f}s, all 24, rows kept"
    )

undo = copy(stamped, "undo")
middle = tenants(undo)[len(tenants(undo)) // 2]
key = pathlib.Path(str(middle) + ".key")
original_key = key.read_text()
key.write_text(original_key.splitlines()[0] + "\nAAAA\n")
undo_env = env | {"TERRABASE_MASTER_KEY": env["TERRABASE_MASTER_KEY"]}
first = subprocess.run(
    ["python3", str(repo / "scripts/storage-format-undo.py"), str(candidate), str(undo)],
    env=undo_env,
    capture_output=True,
    text=True,
)
assert first.returncode == 1 and lock(undo) == "24", first.stdout + first.stderr
key.write_text(original_key)
left = [name for name, version in stamps(undo).items() if version == 24]
assert left == [str(middle.relative_to(undo))], left
second = subprocess.run(
    ["python3", str(repo / "scripts/storage-format-undo.py"), str(candidate), str(undo)],
    env=undo_env,
    capture_output=True,
    text=True,
)
assert second.returncode == 0 and lock(undo) == FIRST and set(stamps(undo).values()) == {1}, second.stdout + second.stderr
start(previous, undo, "previous-after-undo")
result(
    f"6 undo: first run exit {first.returncode}, "
    + next(line for line in first.stdout.splitlines() if "NOT STAMPED" in line)[:110]
)
result(f"6 undo: every other file stamped, lock kept 24; rerun exit 0 '{second.stdout.splitlines()[-1]}'; previous build ready")
