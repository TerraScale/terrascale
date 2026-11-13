import ctypes as c
import fcntl
import json
import os
import pathlib
import subprocess
import sys

build, root = map(pathlib.Path, sys.argv[1:])
library = next(build.glob("lib*_sdk_kit.so"))
lib = c.CDLL(str(library.resolve()))
prefix = library.name[3:-len("_sdk_kit.so")] + "_"
P = c.c_void_p


class Config(c.Structure):
    _fields_ = [
        ("async_io", c.c_uint64), ("path", c.c_char_p), ("features", c.c_char_p),
        ("vfs", c.c_char_p), ("cipher", c.c_char_p), ("key", c.c_char_p),
        ("codec", c.c_char_p), ("flags", c.c_uint32),
    ]


def bind(name, args, result=c.c_uint):
    function = getattr(lib, prefix + name)
    function.argtypes = args
    function.restype = result
    return function


new = bind("database_new", [c.POINTER(Config), c.POINTER(P), c.POINTER(P)])
open_db = bind("database_open", [P, c.POINTER(P)])
connect = bind("database_connect", [P, c.POINTER(P), c.POINTER(P)])
prepare = bind("connection_prepare_single", [P, c.c_char_p, c.POINTER(P), c.POINTER(P)])
step = bind("statement_step", [P, c.POINTER(P)])
run_io = bind("statement_run_io", [P, c.POINTER(P)])
integer = bind("statement_row_value_int", [P, c.c_size_t], c.c_int64)
columns = bind("statement_column_count", [P], c.c_int64)
finalize = bind("statement_finalize", [P, c.POINTER(P)])
free_statement = bind("statement_deinit", [P], None)
close = bind("connection_close", [P, c.POINTER(P)])
free_connection = bind("connection_deinit", [P], None)
free_database = bind("database_deinit", [P], None)


def check(code, error):
    if code not in (0, 1, 2, 3):
        raise RuntimeError(c.string_at(error).decode() if error else "database operation failed")


def stamp(path):
    lines = pathlib.Path(str(path) + ".key").read_text().splitlines()
    if len(lines) != 2 or lines[0] not in ("TERRABASE-DB-KEY-1", "TERRABASE-REPLICA-KEY-1"):
        raise ValueError("unknown key record: " + str(path))
    unwrap = (
        "const c = require('node:crypto'), fs = require('node:fs');"
        "const {master, wrapped} = JSON.parse(fs.readFileSync(0, 'utf8'));"
        "const w = Buffer.from(wrapped, 'base64');"
        "const d = c.createDecipheriv('aes-256-gcm', Buffer.from(master, 'base64'), w.subarray(0, 12));"
        "d.setAuthTag(w.subarray(-16));"
        "process.stdout.write(Buffer.concat([d.update(w.subarray(12, -16)), d.final()]).toString('hex'));"
    )
    key = subprocess.check_output(
        ["node", "-e", unwrap],
        input=json.dumps({"master": os.environ["TERRABASE_MASTER_KEY"], "wrapped": lines[1]}).encode(),
    )
    config = Config(0, str(path.resolve()).encode(), b"index_method,encryption", None, b"aegis256", key, None, 0)
    db, connection, error = P(), P(), P()
    check(new(c.byref(config), c.byref(db), c.byref(error)), error)
    try:
        check(open_db(db, c.byref(error)), error)
        check(connect(db, c.byref(connection), c.byref(error)), error)
        for sql in ["PRAGMA user_version;", "PRAGMA synchronous=FULL;", "PRAGMA user_version=1;", "PRAGMA wal_checkpoint(TRUNCATE);"]:
            statement = P()
            check(prepare(connection, sql.encode(), c.byref(statement), c.byref(error)), error)
            rows = []
            try:
                while True:
                    code = step(statement, c.byref(error))
                    check(code, error)
                    if code == 1:
                        break
                    if code == 3:
                        check(run_io(statement, c.byref(error)), error)
                    elif code == 2:
                        rows.append([integer(statement, n) for n in range(columns(statement))])
            finally:
                check(finalize(statement, c.byref(error)), error)
                free_statement(statement)
            if sql == "PRAGMA user_version;" and rows not in ([[1]], [[24]]):
                raise ValueError("only file versions 1 and 24 can be undone")
            if sql == "PRAGMA wal_checkpoint(TRUNCATE);" and (not rows or rows[0][0] != 0):
                raise OSError("database checkpoint did not complete")
    finally:
        if connection:
            check(close(connection, c.byref(error)), error)
            free_connection(connection)
        free_database(db)


with (root / ".terrabase.lock").open("r+b") as ownership:
    fcntl.flock(ownership, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if ownership.read() not in (b"24", b"23-41a88901325a", b"migrating:23->24"):
        raise ValueError("only the version-only 23 to 24 step can be undone")
    ownership.seek(0)
    ownership.write(b"23-41a88901325a")
    ownership.truncate()
    ownership.flush()
    os.fsync(ownership.fileno())
    print("directory format: 23-41a88901325a", flush=True)
    for path in sorted(root.rglob("*.db")):
        if path.is_symlink():
            raise OSError("database paths must not be symbolic links")
        stamp(path)
        print(str(path.relative_to(root)) + ": user_version=1", flush=True)
    (root / ".terrabase.migration").unlink(missing_ok=True)
    descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
print("undo complete; start the previous image")
