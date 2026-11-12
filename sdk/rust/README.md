# TerraScale Rust SDK

`terrascale::v1` is the Rust client for TerraBase SQL at `https://api.terrascale.tech`. It follows the
shared [SQL SDK API shape](../SQL-API.md).

The Rust SDK is not published. This directory is source only; there is no crates.io package and no install command.

## Quickstart

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

The project is a canonical uppercase ULID and the database a name or ULID; `new_id()` generates a ULID and
`is_valid_id(id)` checks one. Read the API key from secret
storage: `for_project_api_key` exchanges it through ts-auth and refreshes tokens automatically. Reads need
`sql:read`; writes and schema changes also need `sql:write`. `Client::for_project` takes an
already exchanged bearer token. `ClientBuilder::region_pin` sends `X-TerraScale-Region-Pin`, and an
empty region is rejected so no header is sent.
`webhooks::verify_webhook` verifies a Standard Webhooks
`v1` delivery of ts-auth end-user lifecycle events and returns the parsed event or the
SDK's typed `Error`.

## Base URL rules

The base URLs given to `Client::builder`, `ClientBuilder::endpoint`, `ClientBuilder::auth_base_url` and
`ClientConfigBuilder::auth_base_url` follow the origin rule of [`sdk/SQL-API.md`](../SQL-API.md#client): `build()`
returns an error, before any request, for a base URL with user info or a path, a string of more than 2,048 characters,
a name of more than 253 characters or with a label of more than 63, a label that begins or ends with a hyphen or
begins with `xn--`, the name `loopback`, and the unspecified addresses `0.0.0.0`, `[::]` and `[::ffff:0:0]` in any
spelling.

## SQL

Every call is one `POST /v1/projects/{project}/databases/{database}/sql/execute` request and one transaction:
all statements commit or none do, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. The server forwards a timed-out local publish with
`TerraBase-Publish-Id` set to the same transaction id, and the home applies that write once.

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

A refused statement over REST is `Error::Response` wrapping `Error::Sql` (`error.sql()`, or `Error::sql()`
on the outer error, returns it); over a WebSocket session it is `Error::Sql` directly. `sql()` exposes `code`
(constants in `sql_error_codes`), `message`, zero-based `statement` and `sqlstate`, and `is_transient()` is
true for `serialization_conflict` and `tenant_busy`. A `409 serialization_conflict` rolled back and is
resent at most twice with jitter; other refusals return at once. Failures before execution keep the generic
error variants (401, 404, 421, 429, 503).

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
`regions`. Without regions the called region holds the database. `Client::list_databases`,
`Database::get_database`, `Client::rename_database`, `Database::branch_database` (plus `branch_database_if_id`/`delete_database_if_id` guarded by the immutable database ID) and backups/point-in-time restore
use the same project client. Backup operations are typed: `create_backup`/`list_backups` return
`Backup`/`BackupList`, `get_backup_policy`/`set_backup_policy` use `BackupPolicy` and
`BackupPolicyRequest`, `get_restore_window` returns `RestoreWindow`, `plan_point_in_time` returns
`PointInTimePlan` from a `PointInTimeRequest`, and `restore_backup`/`restore_point_in_time` poll a
`RestoreOperation` to its ten-minute deadline before returning the `RestoredDatabase`.
`get_backup_destination`/`set_backup_destination` use `ProjectBackupDestination`; its request's
credentials are write-only and never printed.

Every region that holds a database is equal.
A region can accept a local SQL REST write; TerraBase applies it once. The default
`TerraScale-Durability` is `local`. REST reads can use a ready local region. Copies are eventually
consistent. REST, psql and SQL WebSocket can read a ready local copy. Postgres and WebSocket sessions
preserve read-your-writes; use the writer for REST reads that must see a previous write. All-regions
placement (`-all-regions` on `ts db create` and `ts db placement`) holds the database in every
serving region; otherwise set an explicit region list.

The [published pricing catalog](https://terrascale.tech/pricing.json) meters database writes and storage; selecting additional regions does not multiply storage billing. Deleting a database destroys its backup data
keys, making retained encrypted backups unreadable; physical cleanup is separate. Forks remain independent.

### Placement

Every region that holds a database is equal. TerraBase picks which region writes and moves that on its own
when the set changes.
`CreateDatabaseOptions` and `DatabasePlacement` accept `regions` and `all_regions`;
unset options are not sent. `regions` names at least one region id, with no duplicates.
`Client::set_database_placement(database, &DatabasePlacement)` needs exactly one
of `regions` or `all_regions`. `ManagedDatabase` carries `regions` (every region that holds the database,
sorted by region id; the order has no meaning) and `all_regions`.
`Database::get_database_regions` reads each region's pull progress; an entry's `role` is `Some("writer")`
for the region that commits writes.
Contract fixtures in `contracts/terrabase/placement/` qualify the HTTP boundary.

```rust,no_run
use terrascale::v1::{Client, CreateDatabaseOptions, DatabasePlacement};

# async fn example(project: &str, api_key: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project_api_key(project, api_key).build()?;
let database = client.create_database("orders", &CreateDatabaseOptions {
    regions: Some(vec!["gru".into(), "sjc".into()]),
    ..Default::default()
}).await?;
client.set_database_placement(&database.id, &DatabasePlacement {
    regions: Some(vec!["fra".into(), "gru".into(), "sjc".into()]),
    ..Default::default()
}).await?;
# Ok(()) }
```

All-regions placement (`all_regions`) holds the database in every serving region, including regions added later
(`ts db placement ... -all-regions`).

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

`create_scoped_key` sends the client's API key with Basic authentication only to the ts-auth
scoped-keys endpoint, on `https://auth.terrascale.tech` or the `auth_base_url` origin. It requires a database ULID and `Read` or `ReadWrite` access. Key TTLs range
from 60 to 2,592,000 seconds, default to 3600 and cannot outlive the parent key. Supply `project` for
an organization key; for a project key it must match that key's project. Scoped keys cannot manage
databases, mint keys or reach another database. Revoking or rotating the parent revokes its scoped keys.
The returned `ScopedKey.key` is shown once and can be passed directly to another client's API-key builder.
Refusals use the existing HTTP error types.

A failed write is uncertain if no response arrives after sending, if a 5xx response has no server error code, or if the server code is `commit_uncertain`, `write_outcome_unknown` or `internal_error`. Other coded 5xx responses and SQL errors (the request rolled back) are definite refusals. Reads are never uncertain. Reconcile state before repeating an uncertain write; a malformed success acknowledgement is also uncertain.

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

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.


## Region-set placement

Create and update with one to nine distinct serving region ids in `regions`, separately from `allRegions: true`
and from a rename. Response `regions` are sorted without ordering meaning; integer `placementVersion` changes
with membership or the writer and stays unchanged for an identical set. Transitional response fields are ignored.
Region status exposes every selected region in `regions`, with nullable `role`, `revision`, `lastPulledAt`,
`lagMs` and `lastVerified` (Python uses snake_case; .NET PascalCase).
A removed regional endpoint returns `421 wrong_region`; use the global API or a current selected region.

Dropping the region ordering writes can return `503 replica_not_ready` after adding requested regions.
Additions remain selected. Repeat the same request after replication (about 30 seconds for a new region).
A repeat during that move returns the same 503; a different change returns `409 move_in_progress`.
The SDK exposes `Error::code()` returning `Some("replica_not_ready")` or `Some("move_in_progress")`, preserved by `Error::Response` around `Error::Api`. Neither code is automatically retried;
general transient-503 retries do not include `replica_not_ready`, so callers can distinguish it from an outage.
The caller bounds this repeat loop to 90 seconds and propagates other errors:

`replica_not_ready` means the regions you added are stored; send the same request again. `move_unresolved` means an earlier move is unresolved; send the same request again later.

```rust
let change = DatabasePlacement { regions: Some(vec!["fra".into()]), ..Default::default() };
tokio::time::timeout(Duration::from_secs(90), async { loop { match client.set_database_placement(database, &change).await {
    Ok(db) => break Ok(db), Err(e) if e.code() == Some("replica_not_ready") => tokio::time::sleep(Duration::from_secs(1)).await, Err(e) => break Err(e) } } }).await??;
```

## Project Authentication management

Pass the project secret API key as the credential to `client.projects()?.disable_user(secret_key, project_id, user_id).await`. Reads require `auth:read` and changes require `auth:write`. Step-up operations require a person’s sign-in token. Keep keys in the backend.

Project organization management supports CRUD, memberships, emailed invitations and DNS verified domains. See [the project organizations contract](../../docs/auth/project-organizations.md).
