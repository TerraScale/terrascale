# TerraScale Rust SDK

`terrascale::v1` is the Rust client for TerraBase SQL at `https://api.terrascale.tech`. It follows the
shared [SQL SDK API shape](../SQL-API.md).

## Quickstart

```toml
[dependencies]
terrascale = { git = "https://github.com/TerraScale/terrascale" }
tokio = { version = "1", features = ["macros", "rt-multi-thread"] }
serde = { version = "1", features = ["derive"] }
```

```rust,no_run
use terrascale::v1::{new_id, Client, SqlStatement};

#[derive(serde::Deserialize)]
struct Note { id: String, body: String }

# pub async fn example(project: &str, api_key: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project_api_key(project, api_key).build()?;
let db = client.database("main").sql();

db.execute("CREATE TABLE IF NOT EXISTS notes (id TEXT PRIMARY KEY, body TEXT NOT NULL)", &[]).await?;
let id = new_id();
db.execute("INSERT INTO notes (id, body) VALUES (?, ?)", &[id.as_str().into(), "hello".into()]).await?;

let rows = db.query("SELECT id, body FROM notes WHERE id = ?", &[id.as_str().into()]).await?;
assert_eq!(rows[0].get_str("body")?, "hello");

let notes: Vec<Note> = db.query_as_named("SELECT id, body FROM notes WHERE id = :id", [("id", id.as_str())]).await?;

db.batch(vec![
    SqlStatement::new("UPDATE notes SET body = ? WHERE id = ?").args(["edited", id.as_str()]),
    SqlStatement::new("DELETE FROM notes WHERE body = ''"),
]).await?;
# let _ = notes; Ok(()) }
```

The project is a canonical uppercase ULID and the database a name or ULID. Read the API key from secret
storage: `for_project_api_key` exchanges it through ts-auth and refreshes tokens automatically. Reads need
`documents:read`; writes and schema changes also need `documents:write`. `Client::for_project` takes an
already exchanged bearer token.

## SQL

Every call is one `POST /v1/projects/{project}/databases/{database}/sql/execute` request and one transaction:
all statements commit or none do, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused.

| Method | Returns |
|---|---|
| `execute(sql, args)`, `execute_named(sql, named)`, `execute_statement(statement)` | `SqlStatementResult` |
| `query(sql, args)`, `query_named(sql, named)` | `Vec<SqlRecord>` |
| `query_as::<T>(sql, args)`, `query_as_named::<T>(sql, named)` | `Vec<T>` via serde, by column name, blobs as base64 strings |
| `batch(statements)` | one `SqlStatementResult` per statement, 1 to 256 statements |

`SqlValue` is `Null`, `Integer(i64)`, `Real(f64)`, `Text` or `Blob` (`{"base64":"…"}` on the wire). Booleans
bind as `1`/`0`; NaN and infinity are refused before sending. Named argument keys omit `:`, `@` and `$`.
`SqlRecord` reads by ordinal or column name with `get_i64`, `get_f64`, `get_bool`, `get_str`, `get_bytes` and
`is_null`; a repeated column name resolves to its first ordinal.

A refused statement is `Error::Sql`; `Error::sql()` returns its `code` (constants in `sql_error_codes`),
`message`, zero-based `statement` and `sqlstate`, and `is_transient()` is true for `serialization_conflict`
and `tenant_busy`. A `409 serialization_conflict` rolled back and is resent at most twice with jitter; other
refusals return at once. Failures before execution keep the generic error variants (401, 404, 421, 429, 503).

### Sessions: interactive transactions

`database.connect().await?` opens a `SqlSession` on
`GET /v1/projects/{project}/databases/{database}/sql/ws` (subprotocol `terrascale.sql.v1`, `Authorization: Bearer`,
the region pin when set). It has the same `execute`/`query`/`query_as`/`batch` methods as REST, plus `begin`,
`commit`, `rollback`, `transaction(|s| async move { … })` (begin, run, commit; on error roll back if still open and
return the error), `in_transaction()` and `close()`. Outside a transaction each call is its own transaction; a
transaction lasts at most 5 seconds. Calls may overlap and are matched by frame id; a session is `Send + Sync` and
clones share it. Frames are at most 1 MiB, checked before sending.

An error frame is `Error::Sql` with `status: None`. A refused upgrade returns the same errors as REST. Nothing on a
session is resent, including `serialization_conflict`. When the socket drops, calls in flight fail with
`Error::Transport` and the next call opens a new socket with a fresh credential; if a transaction was open, every call
except `rollback` returns `transaction_rolled_back` (40000) until `rollback()`, which only clears local state. After
`close()` the session stays closed.

Gaps: the session is qualified against recorded exchanges
(`sdk/dotnet/contracts/terrabase/sql/websocket-session.json`) on a local socket, and on 2026-11-04 ran the same flow against live gru. It
connects directly, without an HTTP proxy, and reads an upgrade refusal's body only as far as it arrived with the
headers.

## Databases

`Database::create` creates a database; `create_with_placement` takes a `DatabasePlacement` naming its
`home_region`. Each database is stored and served in that one region; without a home region the called
region is the home. `Client::list_databases`, `Database::get_database`, `Client::rename_database`,
`Database::branch_database` and backups/point-in-time restore use the same project client. Call
`Database::delete_database` in the database's home region; HTTP 204 confirms logical deletion and local
cleanup.

### Placement

A database has one home region, which commits its writes, and replica regions, which hold read replicas.
`CreateDatabaseOptions` and `DatabasePlacement` accept `home_region`, `replica_regions` and `all_regions`;
unset options are not sent. `Client::set_database_placement(database, &DatabasePlacement)` needs exactly one
of `replica_regions` (an empty list removes every replica) or `all_regions`, and no `home_region`.
`Client::move_database_home(database, region)` moves the home to one of its replica regions; it waits up to
`MOVE_HOME_TIMEOUT` (5 minutes) and retries only `503 move_unresolved`, at most five times, after
`Retry-After` or 1 second. `ManagedDatabase` carries `home_region`, `replica_regions` and `all_regions`.
Contract fixtures in `contracts/terrabase/placement/` qualify the HTTP boundary.

```rust,no_run
use terrascale::v1::{Client, CreateDatabaseOptions, DatabasePlacement};

# async fn example(project: &str, api_key: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project_api_key(project, api_key).build()?;
let database = client.create_database("orders", &CreateDatabaseOptions {
    home_region: Some("gru".into()),
    replica_regions: Some(vec!["sjc".into()]),
    ..Default::default()
}).await?;
client.set_database_placement(&database.id, &DatabasePlacement {
    all_regions: true,
    ..Default::default()
}).await?;
let moved = client.move_database_home(&database.id, "sjc").await?;
# let _ = moved; Ok(()) }
```

## Databases for agents

The project-bound SQL `Client` exposes `create_database(name, &CreateDatabaseOptions)`,
`fork_database(source, name, &ForkDatabaseOptions)`, `set_database_ttl(database, seconds)`,
`clear_database_ttl(database)` and `delete_database(database)`. They need `databases:write` and
return `ManagedDatabase` metadata, including `expires_at` (`None` without a TTL); deletion returns `()`.
Selectors accept names or ULIDs. TTLs are whole seconds, from 60 to 31,536,000. A fork without
`point_in_time` copies the source now; an RFC 3339 `point_in_time` restores inside its restore window.
Forks do not inherit TTLs. Expiry deletes the database, backups and data keys; billing continues until deletion.

```rust,no_run
use terrascale::v1::{Client, CreateDatabaseOptions, ScopedKeyAccess, ScopedKeyOptions};

# async fn example(project: &str, api_key: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project_api_key(project, api_key).build()?;
let database = client.create_database("agent", &CreateDatabaseOptions {
    ttl_seconds: Some(3600),
    ..Default::default()
}).await?;
let scoped = client.create_scoped_key(&ScopedKeyOptions {
    project: Some(project.into()),
    database: database.id,
    access: ScopedKeyAccess::ReadWrite,
    ttl_seconds: Some(3600),
}).await?;
let agent = Client::for_project_api_key(project, scoped.key).build()?;
# let _ = agent; Ok(()) }
```

`create_scoped_key` sends the client's API key with Basic authentication only to the fixed ts-auth
scoped-keys endpoint. It requires a database ULID and `Read` or `ReadWrite` access. Key TTLs range
from 60 to 2,592,000 seconds, default to 3600 and cannot outlive the parent key. Supply `project` for
an organization key; for a project key it must match that key's project. Scoped keys cannot manage
databases, mint keys or reach another database. Revoking or rotating the parent revokes its scoped keys.
The returned `ScopedKey.key` is shown once and can be passed directly to another client's API-key builder.
Refusals use the existing HTTP error types; mutations are never replayed after an uncertain result.

Qualification: this surface is checked against the local contract fixtures in
`contracts/terrabase/agent/`, equivalent to the shared SQL API contract. Live server qualification is pending.

## Authentication

See [API-key authentication](docs/api-key-auth.md) for error and refresh behavior. Project requests use
the public origin automatically. Customer credentials determine project, database and operation access;
an endpoint does not grant authorization.

API-key `401 key_revoked` / `401 key_expired` errors carry `Error::KeyRevoked` / `Error::KeyExpired` with
the server message (inside `Error::Response`, accessible by matching its boxed `error` field), and `code()`
retains the code. They never retry or re-exchange. The SDK drops cached tokens and retains the refusal
across clones; create a new client with a replacement key. A refused token exchange with HTTP 401 is the
terminal `Error::TokenExchange` code `invalid_key` (upstream bodies remain redacted). `503 auth_unavailable`
uses the existing replay-safe backoff and `Retry-After` policy without discarding the token.

