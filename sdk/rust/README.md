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
The WebSocket session for interactive transactions ships once TerraBase serves it.

## Documents

Use an existing database name and a new collection name for this example:
Use an existing database name and a new collection name for this example:

```rust,no_run
use terrascale::v1::{Client, WriteOptions};
# pub async fn example(project: &str, api_key: &str, database_name: &str, collection_name: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project_api_key(project, api_key).build()?;
let database = client.database(database_name);
database.create_collection(collection_name).await?;
let items = database.collection(collection_name);
let written = items.create(&serde_json::json!({"quantity": 1}), WriteOptions::new()).await?;
let document = items.get::<serde_json::Value>(written.id).await?;
assert_eq!(document.value().unwrap()["quantity"], 1);
items.delete(written.id, WriteOptions::new().if_match(written.version)).await?;
items.drop_collection().await?;
# Ok(())
# }
```

The project must be a canonical uppercase ULID. Read the API key from secret storage;
`for_project_api_key` exchanges it through ts-auth and refreshes tokens automatically,
including across client clones and stream reconnects. Never embed credentials in source.
See [API-key authentication](docs/api-key-auth.md) for error and refresh behavior.
`Client::for_project` accepts an already exchanged static bearer token without refresh.

Project requests use the public origin automatically. Customer credentials determine
project, database and operation access; an endpoint does not grant authorization.

Each database is stored and served in one home region; without
`DatabasePlacement::home_region` the called region is the home. Region replicas
are not available yet: a non-empty `replica_regions` or `all_regions: Some(true)`
returns HTTP 400 `replicas_unavailable`.

For deletion, call `Database::delete_database` directly in the database's home
region. HTTP 204 confirms logical deletion and local cleanup.

Project-scoped collection shapes are documented in [shape reads](docs/shape-reads.md).
Database-wide changes are available through `Database::changes` (typed events) and
`Database::change_bytes` (raw SSE). API-key clients refresh credentials when reconnecting.

API-key `401 key_revoked` / `401 key_expired` errors carry `Error::KeyRevoked` /
`Error::KeyExpired` with the server message (inside `Error::Response`, accessible
by matching its boxed `error` field), and `code()` retains the code. They never retry or
re-exchange. The SDK drops cached tokens and retains the refusal across clones;
create a new client with a replacement key. A refused token exchange with HTTP
401 is the terminal `Error::TokenExchange` code `invalid_key` (upstream bodies
remain redacted). `503 auth_unavailable` uses the existing replay-safe backoff
and `Retry-After` policy without discarding the token.
