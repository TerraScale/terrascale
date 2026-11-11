import base64
import json
import os
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

from terrascale import Statement, TerraScale, is_valid_id, new_id

spec = json.load(open(os.environ["E2E_OPERATIONS"], encoding="utf-8"))
run = os.environ["E2E_RUN_ID"]
project = os.environ["TERRASCALE_PROJECT_ID"]
table = spec["table"].replace("{sdk}", "python").replace("{run}", run)
client = TerraScale()
state = {}


def fill(text):
    return (
        text.replace("{table}", table).replace("{sdk}", "python").replace("{run}", run)
    )


def same(actual, expected, what):
    if actual != expected:
        raise AssertionError(f"{what}: expected {expected!r}, got {actual!r}")


def cells(rows):
    return [list(row) for row in rows]


def refused(work):
    try:
        work()
    except Exception as error:
        return error
    raise AssertionError("the call was accepted")


def status(work):
    return getattr(refused(work), "status", None)


@dataclass
class Item:
    id: str
    n: int


class Thrown(Exception):
    pass


def execute_positional(op):
    db = state["db"]
    db.execute(fill(op["setup"]))
    same(
        db.execute(fill(op["sql"]), op["args"]).rows_affected,
        op["rowsAffected"],
        "rows affected",
    )


def execute_named(op):
    same(
        state["db"].execute(fill(op["sql"]), op["named"]).rows_affected,
        op["rowsAffected"],
        "rows affected",
    )


def execute_statement(op):
    db = state["db"]
    same(
        db.execute(Statement(fill(op["sql"]), op["args"])).rows_affected,
        op["rowsAffected"],
        "rows affected",
    )
    same(cells(db.query(fill(op["check"]))), op["checkRows"], "stored row")


def query(op):
    same(cells(state["db"].query(fill(op["sql"]))), op["rows"], "rows")


def query_typed(op):
    rows = state["db"].query(fill(op["sql"]), op["args"], Item)
    same([{"id": row.id, "n": row.n} for row in rows], op["objects"], "typed rows")


def batch(op):
    db = state["db"]
    results = db.batch([fill(sql) for sql in op["statements"]])
    same(len(results), len(op["statements"]), "results")
    same(cells(results[-1].rows), op["lastRows"], "last rows")
    error = refused(lambda: db.batch([fill(sql) for sql in op["failing"]]))
    same([error.code, error.statement], [op["code"], op["statement"]], "batch refusal")
    same(cells(db.query(fill(op["check"]))), op["checkRows"], "rolled back")


def refused_statement(op):
    error = refused(lambda: state["db"].execute(fill(op["sql"])))
    same(
        [error.code, error.statement, error.sqlstate, error.status],
        [op["code"], op["statement"], op["sqlstate"], op["status"]],
        "SqlError",
    )


def values(op):
    db, v = state["db"], op["values"]
    db.execute(fill(op["setup"]))
    blob = base64.b64decode(v["b"])
    db.execute(
        fill(op["sql"]),
        [v["k"], v["n"], int(v["big"]), v["r"], v["flag"], v["t"], blob, v["absent"]],
    )
    row = db.query(fill(op["select"]))[0]
    same(
        [
            row[0],
            row[1],
            str(row[2]),
            row[3],
            row[4],
            row[5],
            base64.b64encode(bytes(row[6])).decode(),
            row[7],
        ],
        [
            v["k"],
            v["n"],
            v["big"],
            v["r"],
            op["storedFlag"],
            v["t"],
            v["b"],
            v["absent"],
        ],
        "values",
    )


def ws_connect(op):
    state["session"] = state["db"].connect()


def ws_statements(op):
    session = state["session"]
    same(session.execute(fill(op["sql"])).rows_affected, 1, "rows affected")
    same(cells(session.query(fill(op["query"]))), op["rows"], "rows")
    same(
        cells(session.batch([fill(sql) for sql in op["statements"]])[-1].rows),
        op["lastRows"],
        "last rows",
    )


def ws_begin_commit(op):
    session = state["session"]
    session.begin()
    session.execute(fill(op["sql"]))
    session.commit()
    same(cells(state["db"].query(fill(op["check"]))), op["checkRows"], "committed rows")


def ws_rollback(op):
    session = state["session"]
    session.begin()
    session.execute(fill(op["sql"]))
    session.rollback()
    same(
        cells(state["db"].query(fill(op["check"]))), op["checkRows"], "rolled back rows"
    )


def ws_transaction_commit(op):
    state["session"].transaction(lambda session: session.execute(fill(op["sql"])))
    same(cells(state["db"].query(fill(op["check"]))), op["checkRows"], "committed rows")


def ws_transaction_rethrow(op):
    thrown = Thrown(op["message"])

    def work(session):
        session.execute(fill(op["sql"]))
        raise thrown

    error = refused(lambda: state["session"].transaction(work))
    if error is not thrown:
        raise AssertionError(f"the transaction rethrew {error!r}")
    same(
        cells(state["db"].query(fill(op["check"]))), op["checkRows"], "rolled back rows"
    )


def ws_in_transaction(op):
    session = state["session"]
    session.begin()
    same(session.in_transaction, True, "inside")
    session.commit()
    same(session.in_transaction, False, "after commit")


def ws_close(op):
    session = state["session"]
    session.close()
    refused(lambda: session.execute(op["sql"]))


def conflicts(op):
    now = datetime.now(timezone.utc)
    window = [
        table,
        (now - timedelta(hours=1)).isoformat(),
        (now + timedelta(hours=1)).isoformat(),
    ]
    same(cells(state["db"].query(op["sql"], window)), op["rows"], "conflict rows")


def ids(op):
    same(
        [is_valid_id(new_id()), is_valid_id(op["invalid"])], [True, False], "validation"
    )


def create_ttl(op):
    created = client.create_database(fill(op["name"]), ttl_seconds=op["ttlSeconds"])
    same(created.name, fill(op["name"]), "name")
    if created.expires_at is None:
        raise AssertionError("the database has no expiry")
    state["agent"] = created


def set_ttl(op):
    updated = client.set_database_ttl(state["agent"].id, op["ttlSeconds"])
    if not updated.expires_at > state["agent"].expires_at:
        raise AssertionError("the expiry did not move later")


def clear_ttl(op):
    same(client.clear_database_ttl(state["agent"].id).expires_at, None, "expiry")


def scoped_key(op):
    agent = state["agent"].id
    scoped = client.create_scoped_key(agent, op["access"], ttl_seconds=op["ttlSeconds"])
    secret = Path(os.environ["E2E_SECRETS"], "scoped-key-python")
    secret.touch(mode=0o600)
    secret.write_text(scoped.key, encoding="utf-8")
    narrow = TerraScale(api_key=scoped.key, project=project)
    narrow.database(agent).execute(op["sql"])
    same(
        status(lambda: narrow.database(fill(op["outside"])).execute(op["sql"])),
        op["outsideStatus"],
        "outside its database",
    )
    same(
        status(lambda: narrow.create_scoped_key(agent, "read")),
        op["mintStatus"],
        "minting with a scoped key",
    )


def delete(op):
    client.delete_database(state["agent"].id)
    same(
        status(lambda: client.database(state["agent"].id).execute(op["sql"])),
        op["deletedStatus"],
        "the deleted database",
    )


operations = {
    "sql.execute.positional": execute_positional,
    "sql.execute.named": execute_named,
    "sql.execute.statement": execute_statement,
    "sql.query": query,
    "sql.query.typed": query_typed,
    "sql.batch": batch,
    "sql.refused": refused_statement,
    "values.roundtrip": values,
    "ws.connect": ws_connect,
    "ws.statements": ws_statements,
    "ws.begin_commit": ws_begin_commit,
    "ws.rollback": ws_rollback,
    "ws.transaction.commit": ws_transaction_commit,
    "ws.transaction.rethrow": ws_transaction_rethrow,
    "ws.in_transaction": ws_in_transaction,
    "ws.close": ws_close,
    "conflicts.query": conflicts,
    "ids.new_validate": ids,
    "agent.create_ttl": create_ttl,
    "agent.set_ttl": set_ttl,
    "agent.clear_ttl": clear_ttl,
    "agent.scoped_key": scoped_key,
    "agent.delete": delete,
}

group = [
    op
    for op in spec["operations"]
    if op["group"] == os.environ["E2E_GROUP"] and "needs" not in op
]
if os.environ["E2E_GROUP"] == "a":
    state["db"] = client.database(os.environ["TERRASCALE_DATABASE_NAME"])
failed = False
for op in group:
    started = time.perf_counter()
    line = {"id": op["id"], "ok": True}
    try:
        operations[op["id"]](op)
    except Exception as error:
        failed = True
        line = {
            "id": op["id"],
            "ok": False,
            "error": f"{type(error).__name__}: {error}",
        }
    line["ms"] = round((time.perf_counter() - started) * 1000)
    print(json.dumps(line), flush=True)
sys.exit(1 if failed else 0)
