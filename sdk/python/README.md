# TerraScale Python SDK

The Python client for [TerraScale](https://terrascale.tech): SQL on TerraBase,
sync and asyncio, built on [httpx](https://www.python-httpx.org/). Python 3.10+.

```sh
pip install terrascale
# until the first PyPI release:
pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"
```

## Quickstart

Create a database and a **Full access** API key in the
[dashboard](https://dash.terrascale.tech/), then:

```sh
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
```

```python
from terrascale import TerraScale, new_id

db = TerraScale().database("main")
db.execute("CREATE TABLE IF NOT EXISTS notes (id TEXT PRIMARY KEY, body TEXT)")
db.execute("INSERT INTO notes VALUES (?, ?)", [new_id(), "hello"])
print(db.query("SELECT * FROM notes"))
```

The API key is exchanged with ts-auth (`https://auth.terrascale.tech`) for a
short-lived project access token, cached and refreshed automatically. Pass
either `api_key=` or `token=`: both raise `ValueError`, and `TERRASCALE_API_KEY`
is read only when neither is passed. A client that holds an API key (`api_key=`
or `TERRASCALE_API_KEY`) and a `base_url` other than `https://api.terrascale.tech` needs `issuer=`, the ts-auth
issuer that exchanges the key (for example `https://auth.example.com/terrascale`);
without it the client raises `ValueError` before any request instead of sending
the key to `https://auth.terrascale.tech`. Keep keys on your server and out of git. See [examples/quickstart.py](examples/quickstart.py).
`verify_webhook` verifies a Standard Webhooks `v1` delivery of ts-auth end-user
lifecycle events and returns the parsed event or raises `WebhookVerificationError`.

## SQL

Every call is one request and one transaction, all or nothing.

```python
from dataclasses import dataclass
from terrascale import SqlError, SqlErrorCodes, Statement

rows = db.query("SELECT id, body FROM notes WHERE body = :body", {"body": "hello"})
rows[0]["body"], rows[0][0], rows[0].get_string("body"), dict(rows[0])

@dataclass
class Note:
    id: str
    body: str

notes = db.query("SELECT id, body FROM notes", None, Note)   # Note(**row) per row

db.batch([                                                   # 1 to 256 statements, one transaction
    ("INSERT INTO notes VALUES (?, ?)", [new_id(), "a"]),
    Statement("UPDATE notes SET body = :b WHERE body = 'a'", {"b": "b"}),
])

try:
    db.execute("INSERT INTO notes VALUES (?, ?)", [rows[0]["id"], "dup"])
except SqlError as err:
    assert err.code == SqlErrorCodes.PRIMARY_KEY_VIOLATION   # err.statement, err.sqlstate, err.status
```

| Method | Returns |
| --- | --- |
| `execute(sql, args=None)` / `execute(Statement)` | `StatementResult(columns, rows, rows_affected, last_insert_rowid)` |
| `query(sql, args=None, mapper=None)` | `list[Row]`, or `mapper(**row)` per row |
| `batch([sql \| (sql, args) \| Statement, ...])` | one `StatementResult` per statement |

Arguments are a list (`?`) or a dict (`:name`, `@name`, `$name`, keys without the
prefix): `None`, `int` (64-bit), finite `float`, `bool` (stored 1/0), `str` and
`bytes` (sent as `{"base64": ...}`). A `Row` reads by ordinal or column name, with
`get_int64`, `get_double`, `get_boolean`, `get_string`, `get_bytes` and `is_null`;
blobs come back as `bytes` and integers stay exact. `TerraScale(region="gru")`
pins every request to one region. A missing, empty, or whitespace region sends no
`X-TerraScale-Region-Pin` header.

A refused statement raises `SqlError` with `code` (a `SqlErrorCodes` constant),
`message`, `statement` (zero-based), `sqlstate` and `status`; the request rolled
back. `is_transient` is true for `serialization_conflict` and `tenant_busy`. A
`409 serialization_conflict` is resent at most twice; nothing else is.

### Sessions

`db.connect()` opens a SQL WebSocket session (`terrascale.sql.v1`) for
interactive transactions of at most 5 seconds:

```python
with db.connect() as session:
    session.transaction(lambda s: s.execute("UPDATE ledger SET amount = amount - 5 WHERE id = ?", [1]))
    session.begin()
    session.execute("INSERT INTO ledger VALUES (?, ?)", [3, 5])
    session.commit()                       # or session.rollback()

async with ats.database("main").connect() as session:  # or: session = await db.connect()
    rows = await session.query("SELECT * FROM ledger")
```

`execute`, `query` and `batch` match the REST methods; outside a transaction each
call is its own transaction. `transaction(fn)` begins, runs `fn(session)` and
commits, rolling back and re-raising when `fn` raises. `in_transaction` is the
last answer's flag. Error frames raise `SqlError` with `status` 0; a refused
upgrade raises the same HTTP errors as REST. Nothing is resent: when the socket
drops after a committing request may have been sent, calls in flight and every later
call raise `CommitUncertainError`; the session stays closed. If no committing request
was sent, a lost open transaction raises `transaction_rolled_back` until `rollback()`
clears it, after which a call can reconnect. After `close()` the session stays
closed. Async calls may overlap.

### SQLAlchemy

Install the optional extra — SQLAlchemy stays out of the core dependency set —
and address the engine as `terrascale://<project_id>/<database>`. The API key
comes from `TERRASCALE_API_KEY`, as everywhere else, or from the URL password,
which SQLAlchemy renders as `***` in reprs and logs:

```python
from sqlalchemy import create_engine

engine = create_engine("terrascale://01J.../app")            # TERRASCALE_API_KEY, TERRASCALE_PROJECT_ID
engine = create_engine("terrascale://:<api_key>@01J.../app")  # or the key as the URL password
with engine.connect() as conn:
    conn.execute(text("INSERT INTO notes VALUES (?, ?)"), [new_id(), "hello"])
    print(conn.execute(text("SELECT * FROM notes")).fetchall())
```

The URL query takes `base_url`, `region` and `issuer`, the client options of the same names.

The dialect defaults to real transactions: the first statement after connect,
commit or rollback implicitly opens a transaction over one SQL WebSocket
session, `commit()` and `rollback()` end it, and `begin_nested()` maps to
SAVEPOINT/RELEASE/ROLLBACK TO as plain SQL. Interactive transactions last at
most 5 seconds on the server; past that the statement fails with
`OperationalError` and the transaction was rolled back. For the one-request-
per-statement HTTP path instead, set
`create_engine(..., isolation_level="AUTOCOMMIT")` or
`conn.execution_options(isolation_level="AUTOCOMMIT")`. `rowcount` maps to
`rows_affected` and `lastrowid` to `last_insert_rowid`.

### Async

```python
from terrascale import AsyncTerraScale

async with AsyncTerraScale() as ts:
    db = ts.database("main")
    rows = await db.query("SELECT * FROM notes")
```

`AsyncTerraScale` has the same methods as `TerraScale`, awaited.

## Databases for agents

The client uses its configured `project` for database management:

```python
with TerraScale() as ts:
    agent = ts.create_database("agent", ttl_seconds=3600)
    copy = ts.fork_database(agent.id, "agent-copy", ttl_seconds=600)
    restored = ts.fork_database(agent.id, "agent-restored", point_in_time="2026-11-04T00:00:00Z")
    ts.set_database_ttl(copy.id, 1200)
    ts.clear_database_ttl(restored.id)
    scoped = ts.create_scoped_key(agent.id, "read_write", ttl_seconds=600)
    with TerraScale(scoped.key, project=ts.project) as worker:
        worker.database(agent.id).execute("CREATE TABLE notes (id TEXT PRIMARY KEY)")
    ts.delete_database(copy.id)
    ts.delete_database(restored.id)
```

Database selectors accept names or ULIDs; scoped keys require the database ULID.
Database TTLs are 60–31,536,000 seconds; `expires_at` is an RFC 3339 string or
`None`. Expiry deletes the database, backups and data keys. Forks do not inherit
the source TTL; `point_in_time` must be inside its restore window. Database
management requires `databases:write`.

`create_scoped_key(database, access, project=None, ttl_seconds=None)` uses the
client's API key with HTTP Basic at ts-auth; a token alone cannot mint keys.
`project` defaults to the client's project. Access is `read` or `read_write`;
key TTLs are 60–2,592,000 seconds, default 3600, bounded by the parent key.
The returned `ScopedKey` exposes `key`, `id`, `project_id`, `database`, `access`
and `expires_at`; store `key` when returned because it is shown once. Scoped
keys reach only their database, cannot manage databases or mint keys, and are
revoked when their parent is revoked or rotated. Refusals retain `APIError`
types, status, code and `retry_after`. Both clients expose these methods;
await them on `AsyncTerraScale`.

## Placement

```python
db = ts.create_database("orders", regions=["gru", "sjc"])
ts.set_database_placement(db.id, regions=["fra", "gru", "sjc"])
```

Every region that holds a database is equal. `Database` carries `regions` (every region that holds it, sorted by
region id; the order has no meaning) and `all_regions`. `db.regions()` reads each region's pull progress; an entry's
`role` is `writer` for the region that commits writes. `create_database` and `set_database_placement` take `regions`
(at least one, no duplicates) or `all_regions`, and not both. TerraBase picks which region writes and moves that on
its own when the set changes. Tests cover the recorded fixtures in
`sdk/dotnet/contracts/terrabase/placement/`; live producer qualification has not run. All-regions placement
(`all_regions`) holds the database in every serving region, including regions added later
(`ts db placement ... -all-regions`).

## API

| Surface | Methods |
| --- | --- |
| Client | `TerraScale(api_key=, project=, token=, base_url=, region=, issuer=)`, `database(name)`, `list_databases()`, `create_database(name, regions=, ttl_seconds=)`, `rename_database(id, name)`, `fork_database`, `set_database_ttl`, `set_database_placement`, `clear_database_ttl`, `delete_database`, `delete_database_if_id`, `create_scoped_key`, `create_api_key(organization, name, scopes, project=, database=, expires_at=, allowed_cidrs=)`, `list_api_keys(organization, project=, limit=, cursor=)`, `revoke_api_key(organization, key, project=)`, `rotate_api_key(organization, key, step_up_proof=, project=, grace_seconds=)`, `create_project_client(name, client_type, redirect_uris, step_up_proof=, project=)`, `list_project_clients(project=)`, `update_project_client(client_id, name, redirect_uris, step_up_proof=, project=)`, `rotate_project_client_secret(client_id, step_up_proof=, grace_seconds=, project=)`, `delete_project_client(client_id, project=)` |
| Database | `info()`, `delete()`, `delete_if_id(expected_id)`, `branch(name)`, `branch_if_id(name, expected_id)`, `list_database_logs(status=None)`, `execute`, `query`, `batch` (see [SQL](#sql)) |

**IDs** are canonical ULIDs (`terrascale.new_id()`).

`database.list_database_logs(status=None)` reads the newest 100 recorded requests within 24 hours; `status` is `"2xx"`, `"3xx"`, `"4xx"` or `"5xx"`. Recording is best effort, without a cursor or export guarantee.

**Regions**: `create_database("eu", regions=["fra"])` selects the regions that hold the database;
without `regions` the called region holds it.

Every region that holds a database is equal.
A region can accept a local SQL REST write; TerraBase applies it once. The default
`TerraScale-Durability` is `local`. REST reads can use a ready local region. Copies are eventually
consistent. REST, psql and SQL WebSocket can read a ready local copy. Postgres and WebSocket sessions
preserve read-your-writes; use the writer for REST reads that must see a previous write. All-regions
placement (`-all-regions` on `ts db create` and `ts db placement`) holds the database in every
serving region; otherwise set an explicit region list.

The [published pricing catalog](https://terrascale.tech/pricing.json) meters database writes and storage; selecting additional regions does not multiply storage billing. Deleting a database destroys its backup data
keys, making retained encrypted backups unreadable; physical cleanup is separate. Forks remain independent.

## Errors

Every server error is a `terrascale.APIError` with `status`, `code`, `message`
and `retry_after`. Common cases have
subclasses: `InvalidRequestError` (400), `AuthenticationError` (401),
`PermissionDeniedError` (403), `NotFoundError` (404), `ConflictError` /
`UniqueViolationError` (409), `RateLimitError` (429), `ServiceUnavailableError` (503). A refused API key raises `TokenError`.

`APIError` (including its subclasses) and `TokenError` expose
`request_id: str | None` for support correlation in both sync and async clients.
It is the responding request's `X-Request-ID`, accepted only as one canonical
26-character uppercase ULID (first character `0`–`7`). Missing, malformed or
repeated IDs give `None`; IDs in response bodies are ignored. The ID is
diagnostic metadata, never proof of identity, ownership or authorization.
Existing error strings are unchanged; report `exc.request_id` separately and
keep credentials, URLs with query values and response bodies out of logs.

An invalid acknowledgement raises an `APIError` subclass with code
`invalid_response`, its HTTP status and request ID, rather than a raw parser
exception. Its fixed message omits response contents; the SDK does not retry it.
Malformed token success responses retain `TokenError` / `invalid_token_response`
and now carry the request ID too. Transport failures with no response have no ID.

Reads retry transport failures, 429, 502, 504 and
transient 503s (`storage_busy`, `unavailable`, `capacity`, ...) with jittered
exponential backoff, honouring `Retry-After`, up to `max_attempts` (default 4).
The token exchange retries transport failures, 429 and 5xx the same way; one
exchange runs at a time and concurrent callers share its result. A connect or
connection-pool failure never reached the server, so writes retry it too; when
attempts run out the `TerraScaleError` names the cause and says the request was
not sent.

A failed write is uncertain if no response arrives after sending, if a 5xx response has no server error code, or if the server code is `commit_uncertain`, `write_outcome_unknown` or `internal_error`. Other coded 5xx responses and SQL errors (the request rolled back) are definite refusals. Reads are never uncertain. Reconcile state before repeating an uncertain write; a malformed success acknowledgement is also uncertain.

Malformed read responses raise `InvalidResponseError`.
The server forwards a timed-out local publish with `TerraBase-Publish-Id` set to the same transaction id, and the writer applies that write once. A push stream that returns NATS error 10074 is not retried, because it requests more regional copies than the cluster provides.

## Development

```sh
uv venv && uv pip install -e '.[dev]'
ruff check src && mypy
```

`contracts/openapi-routes.json` is the published route list
(`python scripts/refresh-contract.py` updates it). Releases: bump `src/terrascale/_version.py`, tag `vX.Y.Z` and publish a
GitHub release for it; the publish workflow uploads to PyPI with trusted publishing.

`401 key_revoked` and `401 key_expired` raise `KeyRevokedError` and
`KeyExpiredError`, subclasses of `AuthenticationError` retaining `code` and
`message`. They never retry or refresh. Built-in sync and async token sources
drop cached tokens and remain refused, including during in-flight exchanges;
create a new source/client with a replacement key. Token endpoint
`invalid_client` is a terminal `TokenError`. Caller-owned providers must discard
refused tokens themselves. `503 auth_unavailable` follows the existing safe-read
backoff and `Retry-After` policy without discarding the cached token.

Built-in project API-key tokens renew in the background from 60 seconds before expiry
(or at half-life for short tokens). Concurrent callers share one exchange and continue
using a valid token; only missing, actually expired, or refused tokens require a wait.

## Region-set placement

Create and update with one to nine distinct serving region ids in `regions`, separately from `allRegions: true`
and from a rename. Response `regions` are sorted without ordering meaning; integer `placementVersion` changes
with membership or the writer and stays unchanged for an identical set. Transitional response fields are ignored.
Region status exposes every selected region in `regions`, with nullable `role`, `revision`, `lastPulledAt`,
`lagMs` and `lastVerified` (Python uses snake_case; .NET PascalCase).
An SDK needs no knowledge of a database's region: it can connect to any region. A region that does not hold the
database authenticates the client and proxies to a region that holds it, which authenticates again. This adds one
inter-region round trip (about 170 ms between the current regions) and passes two request admissions, one in each
region. Only the destination records requests, operation units and bytes.

A writer move closes pinned WebSocket sessions with 1012 and PostgreSQL sessions with 57P01; an open transaction is
rolled back. Reconnect for later requests. If the proxy leg drops while a statement or commit is in flight, its
outcome is unknown (`write_outcome_unknown` for REST/WebSocket, SQLSTATE 08007 for PostgreSQL). The proxy never
replays that request. Check the committed state before deciding whether to send a new write.

Dropping the region ordering writes can return `503 replica_not_ready` after adding requested regions.
Additions remain selected. Repeat the same request after replication (about 30 seconds for a new region).
A repeat during that move returns the same 503; a different change returns `409 move_in_progress`.
The SDK exposes `ServiceUnavailableError.code == "replica_not_ready"` and `ConflictError.code == "move_in_progress"`. Neither code is automatically retried;
general transient-503 retries do not include `replica_not_ready`, so callers can distinguish it from an outage.
The caller bounds this repeat loop to 90 seconds and propagates other errors:

`replica_not_ready` means the regions you added are stored; send the same request again. `move_unresolved` means an earlier move is unresolved; send the same request again later.

```python
deadline = time.monotonic() + 90
while True:
    try: client.set_database_placement(database, regions=["fra"]); break
    except ServiceUnavailableError as e:
        if e.code != "replica_not_ready" or time.monotonic() >= deadline: raise
        time.sleep(1)
```

## Project Authentication management

Construct `TerraScale(api_key=secret_key, project=project_id)` or `AsyncTerraScale`; project management calls send the secret directly to ts-auth. Reads require `auth:read` and changes require `auth:write`. Step-up operations require a person’s sign-in token. Keep keys in the backend.

Project MFA/session policy fields and sign-out-everywhere source APIs follow the [producer contract](../../docs/auth/mfa-sessions.md); published package qualification follows release.
Project invitation and waitlist management on this branch is described in [the auth contract](../../docs/auth/signup-restrictions.md). These additions are not yet published.
Project user profile creation, merge-patch updates, and partial password-hash imports
are available in the source SDK. See [user profiles](../../docs/auth/user-profiles.md)
for fields, scope requirements, hash encodings and the next-release API names.
Project organization management supports CRUD, memberships, emailed invitations and DNS verified domains. See [the project organizations contract](../../docs/auth/project-organizations.md).
User-owned API key creation, listing, revocation and backend verification are available in the in-repository auth client. See [the API contract](../../docs/auth/machine-tokens-and-user-api-keys.md).

### SQL session outcome rule

An SDK reports `transaction_rolled_back` only after receiving that answer from the server, or after losing an
open transaction for which no committing request was sent. If COMMIT or a request that can commit (including an
autocommit statement or batch) may have left the client and its answer is lost, the outcome is uncertain.
The SDK returns `write_outcome_unknown` (SQLSTATE `08007`) or its existing commit-uncertain error, retains that
failure for every later call on the session, and closes the connection. It never reconnects or automatically
retries an uncertain request. If the client cannot prove no request byte left, it treats the request as sent.
An execute outside a confirmed transaction, or behind unanswered transaction control, is potentially committing.
A lost read or non-committing statement inside a confirmed open transaction retains the transaction-lost behavior.
Only a confirmed refusal or a request proven not sent permits a rollback assertion; reconcile an uncertain write
through another session before deciding whether to repeat it.
