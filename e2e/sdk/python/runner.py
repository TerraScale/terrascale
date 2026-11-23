import asyncio
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
from terrascale import AsyncTerraScale, Statement, TerraScale, is_valid_id, new_id
from terrascale.auth_organizations import OrganizationInvitationRequest, OrganizationRequest
from terrascale.models import CreateProjectUser, ImportProjectUser, UpdateProjectUser

if os.environ.get("E2E_SESSION_FAULT"):
    from session_fault import session_fault
    session_fault()
    sys.exit(0)

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


def auth_manage_user(op):
    user = os.environ["E2E_AUTH_USER"]
    with TerraScale(api_key=os.environ["E2E_AUTH_READ_KEY"], project=project) as read:
        same(any(item.id == user for item in read.list_users().items), True, "read key lists user")
        same(status(lambda: read.disable_user(user)), 403, "read key cannot disable")
    with TerraScale(api_key=os.environ["E2E_AUTH_WRITE_KEY"], project=project) as write:
        same(write.disable_user(user).disabled, True, "write key disables")
        same(write.enable_user(user).disabled, False, "write key enables")
        key = write.create_user_api_key(user, "journey", ["invoices:read"])
        secret = Path(os.environ["E2E_SECRETS"], "user-api-key-python")
        secret.touch(mode=0o600)
        secret.write_text(key.key, encoding="utf-8")
        with TerraScale(api_key=os.environ["E2E_AUTH_READ_KEY"], project=project) as read:
            same(any(item.id == key.id for item in read.list_user_api_keys(user).items), True, "list user key")
            verified = read.verify_user_api_key(key.key)
            same(verified.valid and verified.user_id == user, True, "verify user key")
            same(verified.scopes, ["invoices:read"], "user key scopes")
            write.revoke_user_api_key(user, key.id)
            same(read.verify_user_api_key(key.key).valid, False, "revoked user key invalid immediately")


    async def asynchronous():
        async with AsyncTerraScale(api_key=os.environ["E2E_AUTH_WRITE_KEY"], project=project) as write:
            same((await write.disable_user(user)).disabled, True, "async write key disables")
            same((await write.enable_user(user)).disabled, False, "async write key enables")

    asyncio.run(asynchronous())


def auth_user_profiles(op):
    with TerraScale(api_key=os.environ["E2E_AUTH_WRITE_KEY"], project=project) as write:
        created = write.create_user(CreateProjectUser(email=f"profile-python-{run}@example.com", first_name="SDK",
                                                      public_metadata={"keep": 1, "remove": True}))
        same(created.first_name, "SDK", "created profile")
        changed = write.update_user(created.id, UpdateProjectUser(public_metadata={"remove": None, "added": "yes"},
                                                                 private_metadata={"backend": True}))
        same(changed.public_metadata, {"keep": 1, "added": "yes"}, "metadata merge")
        imported = write.import_users([ImportProjectUser(email=f"import-python-{run}@example.com", email_verified=True,
                                                        password_hash=op["password_hash"]),
                                       ImportProjectUser(email=f"invalid-python-{run}@example.com", password_hash="malformed")])
        same(len(imported.items), 1, "import valid row")
        same(imported.errors[0].index, 1, "refuse malformed row")
        write.delete_user(imported.items[0].user.id)
        write.delete_user(created.id)

def auth_organizations(op):
    user = os.environ["E2E_AUTH_USER"]
    with TerraScale(api_key=os.environ["E2E_AUTH_WRITE_KEY"], project=project) as write:
        created = write.create_organization(
            project, OrganizationRequest("SDK organization", table.replace("_", "-"), user, None, {}, {}, None, "off")
        )
        try:
            with TerraScale(api_key=os.environ["E2E_AUTH_READ_KEY"], project=project) as read:
                same(read.get_organization(project, created.id).id, created.id, "organization read")
                same(any(item.id == created.id for item in read.list_organizations(project).items), True, "organization listed")
                same(read.list_organization_members(project, created.id).items[0].role, "admin", "initial admin")
                write.create_organization_invitation(
                    project, created.id, OrganizationInvitationRequest("org-" + run + "@example.com", "member")
                )
                same(read.list_organization_invitations(project, created.id).items[0].status, "pending", "invitation listed")
        finally:
            write.delete_organization(project, created.id)


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


def rules_manage(op):
    from terrascale.database_rules import RulesSet, RulesTable, RulesTestAuth, RulesTestRequest, RulesValues
    from terrascale.errors import InvalidRulesError, NotFoundError, VersionMismatchError

    db = state["db"]
    before = None
    try:
        before = db.get_rules()
    except NotFoundError as error:
        same(error.code, "no_rules", "missing rules")
    rules = RulesSet({table: RulesTable(read="row.id = auth.uid AND auth.claims.plan = 'pro'", create="auth.claims.plan = 'pro'",
                                        columns=["id", "label"], writable=["label"], set=RulesValues(create={"id": "auth.uid"}))})
    deployed = db.deploy_rules(rules, before.version if before else None)
    got = db.get_rules()
    same([got.version, got.tables], [deployed.version, rules.tables], "rules read")
    user = os.environ["E2E_AUTH_USER"]
    with TerraScale(api_key=os.environ["E2E_AUTH_MINT_KEY"], project=project) as backend:
        token = backend.mint_custom_token(user, database=os.environ["E2E_DATABASE_ID"], claims={"plan": "pro"})
    secret = Path(os.environ["E2E_SECRETS"]) / "custom-token-python"
    secret.touch(mode=0o600)
    secret.write_text(token.access_token)
    same([token.token_type, token.expires_in], ["Bearer", 300], "custom token lifetime")
    async_user = new_id()
    async def mint_async():
        async with AsyncTerraScale(api_key=os.environ["E2E_AUTH_MINT_KEY"], project=project) as backend:
            return await backend.mint_custom_token(async_user, claims={"plan": "pro"})
    unbound = asyncio.run(mint_async())
    secret = Path(os.environ["E2E_SECRETS"]) / "custom-token-python-unbound"
    secret.touch(mode=0o600)
    secret.write_text(unbound.access_token)
    same([unbound.token_type, unbound.expires_in], ["Bearer", 300], "async custom token lifetime")
    db.execute(f"INSERT INTO {table}(rowid,id,label) VALUES (700002,'other','hidden')")
    path = f"https://api.terrascale.tech/v1/projects/{project}/databases/{os.environ['E2E_DATABASE_ID']}/tables/{table}/rows/"
    for issued, uid, label in [(token, user, "custom-token"), (unbound, async_user, "async-custom-token")]:
        with httpx.Client(headers={"Authorization": "Bearer " + issued.access_token}) as browser:
            created = browser.post(path.rstrip("/"), json={"row": {"label": label}})
            same(created.status_code, 201, "custom token Rules write")
            same(created.json()["row"], {"id": uid, "label": label}, "custom token written row")
            row = browser.get(path + created.json()["key"])
            same(row.status_code, 200, "custom token Rules read")
            same(row.json()["row"], {"id": uid, "label": label}, "custom token readable row")
            hidden = browser.get(path + "700002")
            same([hidden.status_code, hidden.json()["error"]["code"]], [404, "row_not_found"], "custom token hidden row")
            if issued is token:
                update = browser.patch(path + created.json()["key"], json={"row": {"label": "changed"}})
                same([update.status_code, update.json()["error"]["code"]], [403, "rule_denied"], "custom token denied update")
    db.execute(f"DELETE FROM {table} WHERE rowid = 700002 OR label IN ('custom-token','async-custom-token')")
    allowed = db.test_rules(RulesTestRequest("read", table, RulesTestAuth("owner", claims={"plan": "pro"}), row={"id": "owner"}))
    same([allowed.allowed, allowed.columns, allowed.problems], [True, ["id", "label"], []], "rules owner")
    denied = db.test_rules(RulesTestRequest("read", table, RulesTestAuth("other", claims={"plan": "pro"}), row={"id": "owner"}))
    same([denied.allowed, denied.decided_by], [False, "read"], "rules other user")
    invalid = refused(lambda: db.deploy_rules(RulesSet({table: RulesTable(columns=["missing_column"])}), deployed.version))
    same([isinstance(invalid, InvalidRulesError), invalid.status, bool(invalid.problems)], [True, 400, True], "invalid rules")
    stale = refused(lambda: db.deploy_rules(rules, None))
    same([isinstance(stale, VersionMismatchError), stale.status], [True, 412], "stale rules")
    updated = db.deploy_rules(RulesSet({}), deployed.version)
    page = db.list_rules_versions(limit=1)
    same([len(page.items), page.items[0].version, bool(page.next)], [1, updated.version, True], "rules first page")
    next_page = db.list_rules_versions(after=page.next, limit=1)
    same([len(next_page.items), next_page.items[0].version], [1, deployed.version], "rules second page")
    version = db.get_rules_version(deployed.version)
    same([version.version, version.tables], [deployed.version, rules.tables], "rules version")
    restored = db.restore_rules(deployed.version, updated.version)
    same([restored.version != deployed.version, restored.tables], [True, rules.tables], "rules restore")
    if before:
        db.restore_rules(before.version, restored.version)
    else:
        db.deploy_rules(RulesSet({}), restored.version)


def functions_stats(op):
    from terrascale.functions import DatabaseFunctions, FunctionDeployment

    functions = DatabaseFunctions(state["db"])
    name = f"stats-{run}"
    functions.deploy_function(name, FunctionDeployment(source="async function handler() { return { ok: true }; }"))
    functions.invoke_function(name, {})
    stats = functions.get_function_stats(name, window="1h")
    same([stats.window, stats.invocations >= 1, len(stats.buckets)], ["1h", True, 60], "stats")
    logs = functions.list_function_logs(name, limit=1)
    if len(logs) != 1 or logs[0].http_status != 200 or logs[0].response_bytes < 0:
        raise AssertionError(f"log metrics differ: {logs[:1]}")
    functions.delete_function(name)


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
            scoped = TerraScale(
                api_key=KEY, project=project, issuer=raw.removesuffix("/") + "/terrascale", http_client=http, max_attempts=1
            )
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
    placed = TerraScale(base_url="https://fra.api.terrascale.tech", issuer="https://auth.terrascale.tech/terrascale")
    created = client.create_database(f"e2e-python-{run}-move", regions=["gru"])
    try:
        home = client.database(created.id)
        peer = placed.database(created.id)
        home.execute("CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)")
        home.execute("INSERT INTO placement_rows VALUES (1)")
        added = client.set_database_placement(created.id, regions=["gru", "fra"])
        same(added.regions, ["fra", "gru"], "added region")
        same(added.placement_version > created.placement_version, True, "membership raises placement version")
        progress = home.regions()
        same([(row.region, row.healthy) for row in progress.regions], [("fra", True), ("gru", True)],
             "every active-active region healthy")
        same(all(row.applied_bookmark for row in progress.regions), True, "every region reports an applied bookmark")
        same(cells(peer.query("SELECT value FROM placement_rows ORDER BY value", bookmark=home.bookmark)), [[1]],
             "fra reads the gru write with its bookmark")
        peer.execute("INSERT INTO placement_rows VALUES (2)")
        same(cells(home.query("SELECT value FROM placement_rows ORDER BY value", bookmark=peer.bookmark)), [[1], [2]],
             "gru reads the fra write with its bookmark")
        updated = placed.set_database_placement(created.id, regions=["fra"])
        same(updated.regions, ["fra"], "removed region")
        same(updated.placement_version > added.placement_version, True, "membership change raises placement version")
        after = peer.regions()
        same([(row.region, row.healthy) for row in after.regions], [("fra", True)], "remaining region status")
        same(cells(peer.query("SELECT value FROM placement_rows ORDER BY value")), [[1], [2]], "data survived placement")
        print("python placement: fra and gru healthy active-active peers; bookmark reads crossed both ways; rows [[1],[2]]")
    finally:
        placed.delete_database(created.id)


operations = {
    "auth.manage_user": auth_manage_user,
    "auth.user_profiles": auth_user_profiles,

    "auth.organizations": auth_organizations,
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
    "functions.stats": functions_stats,
    "rules.manage": rules_manage,
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
    op
    for op in spec["operations"]
    if op["group"] == os.environ["E2E_GROUP"]
    and ("sdks" not in op or "python" in op["sdks"])
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
