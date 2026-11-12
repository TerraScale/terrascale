---
title: Rust quickstart
description: Create tables, insert rows and query typed rows with the Rust SDK.
---

# Rust quickstart

Run a complete SQL workflow against your TerraScale database in about five minutes: create a table, insert rows with parameters, read typed rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- Rust 1.99 or newer
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/getting-started/): the key needs the **sql:read** and **sql:write** scopes on that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The Rust SDK is not published. The source stays in `sdk/rust`; there is no crates.io package and no install command.

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

    // from_environment reads TERRASCALE_PROJECT_ID and TERRASCALE_API_KEY and
    // exchanges the dashboard key for short-lived project access tokens; the
    // client refreshes them automatically.
    let client = Client::from_environment()?;
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

Reads are served in the region you call. Pin a region of the database's set with `region_pin` (it needs the static public endpoint), and set `TERRASCALE_REGION` to that region:

```rust
let client = Client::for_project_api_key(project, api_key)
    .endpoint("https://api.terrascale.tech")
    .region_pin(&std::env::var("TERRASCALE_REGION").expect("TERRASCALE_REGION"))
    .build()?;
let rows = client
    .database(&database)
    .sql()
    .query("SELECT count(*) AS n FROM people", &[])
    .await?;
println!("people in region: {}", rows[0].get_i64("n")?);
```

A read in that region can lag behind writes ordered in another region of the set; see [regions](/concepts/regions).

## How it works

- **The key never reaches TerraBase.** `Client::from_environment` exchanges it with ts-auth for a short-lived project access token; the client refreshes it automatically. Keep the key on your server; see [authentication](/guides/authentication).
- **Every call is one request and one transaction.** `execute`, `query` and `batch` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional `?` values and named (`:name`) values are sent as typed JSON; blobs travel as base64. `query_as::<T>` maps rows by column name into your struct through serde.
- **Refused statements are `Error::Sql`.** `Error::sql()` returns its `code`, `message`, zero-based `statement` and `sqlstate`, and `is_transient()` marks `serialization_conflict` and `tenant_busy`. A `serialization_conflict` is resent at most twice; nothing else is.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## Next steps

- [SDK reference](/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/getting-started/javascript), [C#](/getting-started/csharp), [Go](/getting-started/go) and [Python](/getting-started/python) quickstarts
- [SQL over HTTP](/guides/sql-http) for the raw HTTP interface and [concepts](/concepts/sql) for the SQL dialect
- [Typed SQL with the Rust SDK](/guides/rust-sql) for typed `query_as` selects, session transactions and batches
