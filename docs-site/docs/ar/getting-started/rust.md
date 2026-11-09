---
title: Rust quickstart
description: Create tables, insert rows and query typed rows with the Rust SDK.
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Rust quickstart

Run a complete SQL workflow against your TerraScale database in about five minutes: create a table, insert rows with parameters, read typed rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- Rust 1.99 or newer
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/ar/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The crate is not on crates.io yet; install it from the monorepo:

```bash
cargo new quickstart && cd quickstart
```

Then add the dependency exactly as documented to `Cargo.toml`:

```toml
[dependencies]
terrascale = { git = "https://github.com/TerraScale/terrascale.git" }
tokio = { version = "1", features = ["macros", "rt-multi-thread"] }
serde = { version = "1", features = ["derive"] }
```

## Run the quickstart

Save this as `src/main.rs`. The client exchanges the API key for short-lived project access tokens automatically.

```rust
use terrascale::v1::{new_id, sql_error_codes, Client, SqlStatement};

#[derive(serde::Deserialize)]
struct Person {
    id: String,
    name: String,
}

#[tokio::main]
async fn main() -> terrascale::v1::Result<()> {
    let project = std::env::var("TERRASCALE_PROJECT_ID").expect("TERRASCALE_PROJECT_ID");
    let api_key = std::env::var("TERRASCALE_API_KEY").expect("TERRASCALE_API_KEY");
    let database =
        std::env::var("TERRASCALE_DATABASE_NAME").unwrap_or_else(|_| "main".to_string());

    // for_project_api_key exchanges the dashboard key for short-lived project
    // access tokens; the client refreshes them automatically.
    let client = Client::for_project_api_key(project, api_key).build()?;
    let db = client.database(&database).sql();

    // One statement, one transaction. IF NOT EXISTS makes the quickstart safe to run twice.
    db.execute(
        "CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
        &[],
    )
    .await?;

    // Insert with parameters; new_id() mints a ULID primary key.
    let ada = new_id();
    db.execute(
        "INSERT INTO people (id, name) VALUES (?, ?)",
        &[ada.as_str().into(), "Ada".into()],
    )
    .await?;

    // Select into typed rows: query_as maps each row by column name through serde.
    let people: Vec<Person> = db
        .query_as_named(
            "SELECT id, name FROM people WHERE id = :id",
            [("id", ada.as_str())],
        )
        .await?;
    println!("query matched {} {}", people[0].id, people[0].name);

    // A batch of 1 to 256 statements runs as one transaction: all commit or none do.
    let grace = new_id();
    db.batch(vec![
        SqlStatement::new("INSERT INTO people (id, name) VALUES (:id, :name)")
            .named("id", grace.as_str())
            .named("name", "Grace"),
        SqlStatement::new("SELECT count(*) AS n FROM people"),
    ])
    .await?;
    let rows = db.query("SELECT count(*) AS n FROM people", &[]).await?;
    println!("batch inserted, people count: {}", rows[0].get_i64("n")?);

    // A refused statement returns Error::Sql; the whole request rolled back.
    let refused = db
        .execute(
            "INSERT INTO people (id, name) VALUES (?, 'dup')",
            &[ada.as_str().into()],
        )
        .await
        .expect_err("duplicate primary key");
    let sql = refused.sql().expect("typed SQL error");
    println!(
        "refused: code {} statement {} sqlstate {}",
        sql.code,
        sql.statement,
        sql.sqlstate.clone().unwrap_or_default()
    );
    assert_eq!(sql.code, sql_error_codes::PRIMARY_KEY_VIOLATION);
    Ok(())
}
```

Run it with the key exported in your shell:

```bash
cargo run
```

Expected output (ids vary):

```text
query matched 01M6NM00V0GSG7YRE2WSYER3WD Ada
batch inserted, people count: 2
refused: code primary_key_violation statement 0 sqlstate 23505
```

## Read from another region

REST reads can use a ready local replica. Pin a region with `region_pin` (it needs the static public endpoint), and set `TERRASCALE_REGION` to a replica region of your database:

```rust
let replica = Client::for_project_api_key(project, api_key)
    .endpoint("https://api.terrascale.tech")
    .region_pin(&std::env::var("TERRASCALE_REGION").expect("TERRASCALE_REGION"))
    .build()?;
let rows = replica
    .database(&database)
    .sql()
    .query("SELECT count(*) AS n FROM people", &[])
    .await?;
println!("people in region: {}", rows[0].get_i64("n")?);
```

Replica reads are eventually consistent; see [regions](/ar/concepts/regions).

## How it works

- **The key never reaches TerraBase.** `Client::for_project_api_key` exchanges it with ts-auth for a short-lived project access token; the client refreshes it automatically. Keep the key on your server; see [authentication](/ar/guides/authentication).
- **Every call is one request and one transaction.** `execute`, `query` and `batch` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/ar/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional `?` values and named (`:name`) values are sent as typed JSON; blobs travel as base64. `query_as::<T>` maps rows by column name into your struct through serde.
- **Refused statements are `Error::Sql`.** `Error::sql()` returns its `code`, `message`, zero-based `statement` and `sqlstate`, and `is_transient()` marks `serialization_conflict` and `tenant_busy`. A `serialization_conflict` is resent at most twice; nothing else is.

## Next steps

- [SDK reference](/ar/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/ar/getting-started/javascript), [C#](/ar/getting-started/csharp), [Go](/ar/getting-started/go) and [Python](/ar/getting-started/python) quickstarts
- [SQL over HTTP](/ar/guides/sql-http) for the raw HTTP interface and [concepts](/ar/concepts/sql) for the SQL dialect
- [Typed SQL with the Rust SDK](/ar/guides/rust-sql) for typed `query_as` selects, session transactions and batches

</div>
