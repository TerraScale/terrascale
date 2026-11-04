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
short-lived project access token, cached and refreshed automatically. Keep keys
on your server and out of git. See [examples/quickstart.py](examples/quickstart.py).

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
pins every request to one region.

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
drops, calls in flight fail and the next call reconnects with a fresh token. If a
transaction was open, every call except `rollback()` raises
`transaction_rolled_back` until `rollback()`. After `close()` the session stays
closed. Async calls may overlap.

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

## API

| Surface | Methods |
| --- | --- |
| Client | `TerraScale(api_key=, project=, token=, base_url=, region=)`, `database(name)`, `list_databases()`, `create_database(name, home_region=, ttl_seconds=)`, `rename_database(id, name)`, `fork_database`, `set_database_ttl`, `clear_database_ttl`, `delete_database`, `create_scoped_key` |
| Database | `info()`, `delete()`, `branch(name)`, `execute`, `query`, `batch` (see [SQL](#sql)) |

**IDs** are canonical ULIDs (`terrascale.new_id()`).

**Regions**: `create_database("eu", home_region="fra")` stores and serves the
database in its home region; without `home_region` the called region is the home.

For deletion, call `db.delete()` directly in the database's home region (await
for async clients). HTTP 204 confirms logical deletion and local cleanup.

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
not sent. SQL statements and other writes are never replayed after an ambiguous
failure: a transport failure after sending, 5xx or malformed acknowledgement
raises `CommitUncertainError`; reconcile state before retrying. Malformed read responses raise `InvalidResponseError`.

## Development

```sh
uv venv && uv pip install -e '.[dev]'
pytest && ruff check src tests && mypy
```

Tests run against an in-memory HTTP fake (`tests/fake.py`) and check every
request against the published route list in `contracts/openapi-routes.json`
(`python scripts/refresh-contract.py` updates it). They never start a TerraBase
server. Releases: bump `src/terrascale/_version.py`, tag `vX.Y.Z` and publish a
GitHub release for it; the publish workflow uploads to PyPI with trusted publishing.

License: Apache-2.0.

`401 key_revoked` and `401 key_expired` raise `KeyRevokedError` and
`KeyExpiredError`, subclasses of `AuthenticationError` retaining `code` and
`message`. They never retry or refresh. Built-in sync and async token sources
drop cached tokens and remain refused, including during in-flight exchanges;
create a new source/client with a replacement key. Token endpoint
`invalid_client` is a terminal `TokenError`. Caller-owned providers must discard
refused tokens themselves. `503 auth_unavailable` follows the existing safe-read
backoff and `Retry-After` policy without discarding the cached token.
