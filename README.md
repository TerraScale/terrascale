# TerraScale Rust SDK

`terrascale::v1` provides typed document and SQL APIs through `https://api.terrascale.tech`.
Start with the [current API guide](docs/current-api.md). The [document walkthrough](docs/csharp-api.md)
covers ULID identities, conditional writes, pagination and errors.

Use an existing database name and a new collection name for this example:

```rust,no_run
use terrascale::v1::{Client, WriteOptions};
# pub async fn example(project: &str, access_token: &str, database_name: &str, collection_name: &str) -> terrascale::v1::Result<()> {
let client = Client::for_project(project, access_token).build()?;
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

The project must be a canonical uppercase ULID. Customer API keys must first be exchanged
for a project access token through ts-auth. The project client accepts that bearer token;
it does not currently acquire or refresh tokens. Construct a new client with a fresh token
when it expires. Never embed credentials in source.

Project requests use the public origin automatically. Customer credentials determine
project, database and operation access; an endpoint does not grant authorization.

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
`Database::change_bytes` (raw SSE). Keep the access token lifetime in mind when resuming streams.
