# Rust

The SDK is unpublished. Use this reference only when the application can consume
`sdk/rust` from a TerraScale source checkout; there is no crates.io install.
The quickstart lists Rust 1.99+. Sources:
[Rust quickstart](https://docs.terrascale.tech/getting-started/rust),
[SDK reference](https://docs.terrascale.tech/guides/sdks).

## Source dependency and configuration

Create a binary application with `cargo new terrascale-quickstart`.
Add these dependencies to its `Cargo.toml`, replacing the local path with the
actual immutable checkout location:

```toml
[dependencies]
terrascale = { path = "/absolute/path/to/terrascale/sdk/rust" }
tokio = { version = "1", features = ["macros", "rt-multi-thread"] }
serde = { version = "1", features = ["derive"] }
```

`Client::from_environment()` reads `TERRASCALE_PROJECT_ID`, `TERRASCALE_API_KEY`
and optional `TERRASCALE_BASE_URL`. Explicit construction is
`Client::for_project_api_key(project, key).build()?`; use `for_project` instead
for an already exchanged token. A custom `.endpoint(...)` with an API key
requires `.auth_base_url(...)`. Pin with `.region_pin(...)` and an explicit
static endpoint, as in the quickstart; `TERRASCALE_REGION` is not read implicitly.
Sources: [Rust](https://docs.terrascale.tech/getting-started/rust),
[authentication](https://docs.terrascale.tech/guides/authentication).

## Complete small example

Save as `src/main.rs`, supply the skill's environment variables and run
`cargo run`. REST SQL is on `.sql()`; WebSocket `connect()` is on the database.

```rust
use terrascale::v1::{new_id, Client, SqlStatement};

#[derive(serde::Deserialize)]
struct Person {
    id: String,
    name: String,
}

#[tokio::main]
async fn main() -> terrascale::v1::Result<()> {
    let client = Client::from_environment()?;
    let name = std::env::var("TERRASCALE_DATABASE_NAME").expect("TERRASCALE_DATABASE_NAME");
    let database = client.database(name);
    let db = database.sql();
    db.execute("CREATE TABLE IF NOT EXISTS sdk_people (id TEXT PRIMARY KEY, name TEXT NOT NULL)", &[]).await?;
    let id = new_id();
    db.batch(vec![
        SqlStatement::new("INSERT INTO sdk_people (id, name) VALUES (?, ?)")
            .args([id.as_str(), "Ada"]),
        SqlStatement::new("INSERT INTO sdk_people (id, name) VALUES (:id, :name)")
            .named("id", new_id()).named("name", "Grace"),
    ]).await?;
    let people: Vec<Person> = db.query_as_named(
        "SELECT id, name FROM sdk_people WHERE id = :id", [("id", id.as_str())],
    ).await?;
    println!("{} {}", people[0].id, people[0].name);

    let session = database.connect().await?;
    let transaction_id = id.clone();
    let result = session.transaction(|tx| async move {
        let rows = tx.query("SELECT name FROM sdk_people WHERE id = ?", &[transaction_id.as_str().into()]).await?;
        if rows[0].get_str("name")? == "Ada" {
            tx.execute("UPDATE sdk_people SET name = ? WHERE id = ?", &["Ada Lovelace".into(), transaction_id.as_str().into()]).await?;
        }
        Ok(())
    }).await;
    session.close().await;
    result?;

    match db.execute("INSERT INTO sdk_people (id, name) VALUES (?, ?)", &[id.as_str().into(), "duplicate".into()]).await {
        Err(error) => {
            if let Some(sql) = error.sql() {
                if sql.code == "primary_key_violation" {
                    println!("{} {} {:?}", sql.code, sql.statement, sql.sqlstate);
                    return Ok(());
                }
            }
            Err(error)
        }
        Ok(_) => panic!("expected duplicate refusal"),
    }
}
```

Expected: the generated ID and `Ada`, then code `primary_key_violation`,
statement `0`, SQLSTATE `23505`. The session is closed before propagating a
transaction error. Sources: [Rust](https://docs.terrascale.tech/getting-started/rust),
[transactions](https://docs.terrascale.tech/guides/sql-http).

## Values, timeouts and errors

`execute`, `query`, `query_as::<T>` accept `&[SqlValue]`; named counterparts
include `execute_named`, `query_named`, `query_as_named`. `SqlStatement::new`
accepts `.args(...)` or repeated `.named(...)`; `batch` takes a vector of these.
`SqlRecord` has `get_i64`, `get_str`, `get_bytes`, `is_null` returning checked
results where applicable. Typed mapping uses serde column names; aliases or
serde field attributes must match. Integers use `i64`, blobs use bytes.

Builder `.timeout(Some(Duration::from_secs(...)))` controls per-attempt HTTP
timeouts; `.connect_timeout(...)` controls connection establishment. A caller
can cancel an async operation by dropping its future. That does not undo a
request already accepted by the server.

Use `error.sql()` rather than matching only `Error::Sql`: REST errors can be
wrapped in `Error::Response`, while session SQL errors are direct. `SqlError`
has `code`, `message`, `statement`, optional `sqlstate`, and `is_transient()`.
Read HTTP status from the enclosing `error.status()` (`Option<u16>`), not from
`error.sql()`: `SqlError` has no `status` field. A session SQL error has no HTTP status.
`Error::CommitUncertain` is terminal for the session; use a new connection to
reconcile. Without a committing request, a lost transaction can be cleared
with `rollback().await` before reconnection. Sources:
[Rust](https://docs.terrascale.tech/getting-started/rust),
[errors](https://docs.terrascale.tech/guides/errors).
Source audit: `sdk/rust/README.md`, `Cargo.toml`, `src/v1/client.rs`,
`sql_exec.rs`, `sql_session.rs`, `errors.rs`, `ids.rs` at the revision in
[the skill](../SKILL.md); the SQL fields and enclosing `Error::status()` were
rechecked at `6b786568d60373e2fa16836a8ab4354bbb642651`.
Source dependency availability does not establish a release.
