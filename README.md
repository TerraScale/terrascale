# TerraScale Rust SDK

`terrascale::v1` provides typed document and SQL APIs through `https://api.terrascale.tech`.
Start with the [current API guide](docs/current-api.md). The [document walkthrough](docs/csharp-api.md)
covers ULID identities, conditional writes, pagination and errors.

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

Writable databases require at least two distinct serving regions: a home region
and at least one replica. On creation, omitting `DatabasePlacement::replica_regions`
uses all current serving regions, even when `home_region` is set.
`all_regions: Some(true)` also follows future serving regions. Creating with an
explicit empty replica list returns HTTP 400.

For deletion, call `Database::delete_database` directly in the database's home
region, without a preceding placement update. Deletion covers every replica,
including all-regions placement. HTTP 204 confirms logical deletion and local
cleanup; remote erasure is retried durably and may complete later.
Updating to an empty replica list can temporarily disable document writes with
HTTP 409 `placement_needs_two_regions`; it does not provide supported
single-region placement.

### Relational SQL

`Database::sql()` runs parameterized SQL on `POST /v1/projects/{project}/databases/{database}/sql/execute`
([guide](https://docs.terrascale.tech/guides/sql-http)). Each request is one transaction: every
statement commits or none does, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused.

```rust,no_run
# pub async fn demo(client: terrascale::v1::Client, database_name: &str, table_name: &str) -> terrascale::v1::Result<()> {
use terrascale::v1::{SqlStatement, SqlValue};
#[derive(serde::Deserialize)]
struct User { id: i64, name: String }

let sql = client.database(database_name).sql();
// Quote the caller's new table name as an SQL identifier; bind all data values.
let table = format!("\"{}\"", table_name.replace('"', "\"\""));
sql.execute(&format!("CREATE TABLE {table} (id INTEGER PRIMARY KEY, name TEXT)"), &[]).await?;
sql.execute(&format!("INSERT INTO {table}(id, name) VALUES (?, ?)"), &[1.into(), "Ada".into()]).await?;
let users: Vec<User> = sql.query(&format!("SELECT id, name FROM {table} WHERE id = ?"), &[1.into()]).await?;
let results = sql.batch(vec![
    SqlStatement::new(format!("UPDATE {table} SET name = :name WHERE id = :id")).named("name", "Grace").named("id", 1),
    SqlStatement::new(format!("SELECT count(*) FROM {table}")),
]).await?;
assert!(matches!(results[1].rows[0][0], SqlValue::Integer(1)));
sql.execute(&format!("DROP TABLE {table}"), &[]).await?;
# let _ = users; Ok(()) }
```

Results carry `columns` (name, `decltype`), `rows` of `SqlValue` (`Null`, `Integer(i64)`, `Real(f64)`,
`Text`), `rows_affected` and `last_insert_rowid`; `decode_rows` maps rows into any `Deserialize` type
by column name. Booleans bind as `1`/`0`. Integers are JSON numbers: this client keeps `i64`, but
JavaScript peers lose precision beyond 2^53. SQL failures (`sql_error`, `forbidden_scope`,
`sql_conflict`, `sql_too_large`) are `Error::Sql`, available through `Error::sql()` with `code`,
`message`, `statement` and `sqlite_code`; other failures keep the generic error variants. The
read-only `run_sql` route queries document collections through a separate SELECT subset.

Use `Database::sql()` for parameterized relational SQL. Use `Database::run_sql`,
`check_sql` and `explain_sql` for the read-only document SELECT subset; these methods
do not provide parameter binding.

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
