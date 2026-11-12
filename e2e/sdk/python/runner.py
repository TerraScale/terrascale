import base64
import ipaddress
import json
import os
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlsplit

import httpx
from terrascale import Statement, TerraScale, is_valid_id, new_id
from terrascale.errors import ServiceUnavailableError

spec = json.loads(Path(os.environ["E2E_OPERATIONS"]).read_text(encoding="utf-8"))
run = os.environ["E2E_RUN_ID"]
project = os.environ["TERRASCALE_PROJECT_ID"]
table = spec["table"].replace("{sdk}", "python").replace("{run}", run)
client = TerraScale()
state = {}


def fill(text):
    return text.replace("{table}", table).replace("{sdk}", "python").replace("{run}", run)


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
    same(cells(state["db"].query(fill(op["check"]))), op["checkRows"], "rolled back rows")


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
    same(cells(state["db"].query(fill(op["check"]))), op["checkRows"], "rolled back rows")


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
    same([is_valid_id(new_id()), is_valid_id(op["invalid"])], [True, False], "validation")


TOKEN = "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln"
KEY = "01JA0000000000000000000000_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk"


def address(origin):
    url = urlsplit(origin)
    try:
        return url.scheme, ipaddress.ip_address(url.hostname), url.port
    except ValueError:
        return url.scheme, url.hostname, url.port


def api_origin(raw, http):
    try:
        return TerraScale(token=TOKEN, project=project, base_url=raw, http_client=http).base_url
    except ValueError:
        return None


def auth_origin(raw):
    sent = []

    def record(request):
        sent.append(request.url)
        raise httpx.ConnectError("recorded, not sent", request=request)

    with httpx.Client(transport=httpx.MockTransport(record)) as http:
        try:
            scoped = TerraScale(api_key=KEY, project=project, issuer=raw.removesuffix("/") + "/terrascale", http_client=http,
                                max_attempts=1)
        except ValueError:
            return None
        refused(lambda: scoped.create_scoped_key(new_id(), "read"))
    return f"{sent[0].scheme}://{sent[0].netloc.decode()}" if sent else None


def origin_rule(op):
    cases = json.loads(Path(os.environ["E2E_OPERATIONS"]).with_name(op["cases"]).read_text(encoding="utf-8"))["cases"]
    wrong = []
    with httpx.Client() as http:
        for index, (raw, api, auth) in enumerate(cases):
            for column, expected, got in (("api", api, api_origin(raw, http)), ("auth", auth, auth_origin(raw))):
                if (got is None) != (expected is None) or got is not None and address(got) != address(expected):
                    wrong.append(f"#{index} {raw[:60]!r} {column}: expected {expected or 'refused'}, got {got or 'refused'}")
    if wrong:
        raise AssertionError(f"origin rule: {len(wrong)} of {2 * len(cases)} checks differ: {'; '.join(wrong)}")


def create_ttl(op):
    before = datetime.now(timezone.utc)
    created = client.create_database(fill(op["name"]), ttl_seconds=op["ttlSeconds"])
    after = datetime.now(timezone.utc)
    same(created.name, fill(op["name"]), "name")
    if created.expires_at is None:
        raise AssertionError("the database has no expiry")
    expiry = datetime.fromisoformat(created.expires_at.replace("Z", "+00:00"))
    earliest = before + timedelta(seconds=op["ttlSeconds"])
    latest = after + timedelta(seconds=op["ttlSeconds"] + 5)
    if not earliest <= expiry <= latest:
        raise AssertionError(
            f"expiry the server reports, {created.expires_at}, is outside {earliest.isoformat()}..{latest.isoformat()}"
        )
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
        status(lambda: narrow.create_scoped_key(agent, "read")),
        op["mintStatus"],
        "minting with a scoped key",
    )


def fork(op):
    db = state["db"]
    db.execute(fill(op["setup"]))
    db.execute(fill(op["sql"]))
    forked = client.fork_database(os.environ["TERRASCALE_DATABASE_NAME"], fill(op["name"]))
    same([forked.name, forked.branched_from], [fill(op["name"]), os.environ["E2E_DATABASE_ID"]], "fork")
    same(cells(client.database(forked.id).query(fill(op["check"]))), op["checkRows"], "rows of the fork")
    client.delete_database(forked.id)


def scoped_key_outside(op):
    source = os.environ["E2E_DATABASE_ID"]
    scoped = client.create_scoped_key(source, op["access"], ttl_seconds=op["ttlSeconds"])
    secret = Path(os.environ["E2E_SECRETS"], "scoped-key-python-outside")
    secret.touch(mode=0o600)
    secret.write_text(scoped.key, encoding="utf-8")
    narrow = TerraScale(api_key=scoped.key, project=project)
    narrow.database(source).query(op["sql"])
    error = refused(lambda: narrow.database(os.environ["E2E_OTHER_DATABASE"]).query(op["sql"]))
    same([getattr(error, "status", None), getattr(error, "code", None)], [op["status"], op["code"]], "the other database")


def delete(op):
    client.delete_database(state["agent"].id)
    same(
        status(lambda: client.database(state["agent"].id).execute(op["sql"])),
        op["deletedStatus"],
        "the deleted database",
    )


def placement_create(op):
    created = client.create_database(f"e2e-python-{run}-placed", regions=["gru", "fra"])
    try:
        same(created.regions, ["fra", "gru"], "created regions")
        same(isinstance(created.placement_version, int), True, "integer placement version")
        repeated = client.set_database_placement(created.id, regions=["fra", "gru"])
        same(repeated.placement_version, created.placement_version, "unchanged placement version")
    finally:
        client.delete_database(created.id)


def placement_set(op):
    created = client.create_database(f"e2e-python-{run}-move", regions=["gru"])
    placed = client
    try:
        client.database(created.id).execute("CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)")
        client.database(created.id).execute("INSERT INTO placement_rows VALUES (1)")
        added = client.set_database_placement(created.id, regions=["gru", "fra"])
        same(added.regions, ["fra", "gru"], "added region")
        same(added.placement_version > created.placement_version, True, "membership raises placement version")
        progress = client.database(created.id).regions()
        same([row.region for row in progress.regions], ["fra", "gru"], "status regions")
        same(next(row.role for row in progress.regions if row.region == "gru"), "writer", "writer role")
        deadline = time.monotonic() + 90
        while True:
            try:
                updated = client.set_database_placement(created.id, regions=["fra"])
                break
            except ServiceUnavailableError as error:
                if error.code != "replica_not_ready" or time.monotonic() >= deadline:
                    raise
                time.sleep(1)
        placed = TerraScale(base_url="https://fra.api.terrascale.tech", issuer="https://auth.terrascale.tech/terrascale")
        same(updated.regions, ["fra"], "dropped writer")
        same(updated.placement_version > added.placement_version, True, "writer change raises placement version")
        after = placed.database(created.id).regions()
        same([(row.region, row.role) for row in after.regions], [("fra", "writer")], "moved status")
        error = refused(lambda: client.database(created.id).regions())
        same([error.status, error.code], [421, "wrong_region"], "nonmember endpoint")
        same(cells(placed.database(created.id).query("SELECT value FROM placement_rows")), [[1]], "data survived placement")
        print("python placement: create [gru,fra], add fra, repeat drop gru -> 200, status fra writer, 421 wrong_region")
    finally:
        placed.delete_database(created.id)


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
    "origin.rule": origin_rule,
    "agent.create_ttl": create_ttl,
    "agent.set_ttl": set_ttl,
    "agent.clear_ttl": clear_ttl,
    "agent.scoped_key": scoped_key,
    "agent.delete": delete,
    "placement.create": placement_create,
    "placement.set": placement_set,
    "agent.fork": fork,
    "agent.scoped_key_outside": scoped_key_outside,
}

group = [
    op for op in spec["operations"]
    if op["group"] == os.environ["E2E_GROUP"]
    and ("needs" not in op or os.environ.get("E2E_REGIONS") == "2" and op["id"].startswith("placement."))
]
if os.environ["E2E_GROUP"] in ("a", "c"):
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
