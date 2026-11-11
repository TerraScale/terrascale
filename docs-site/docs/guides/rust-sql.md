---
title: Typed SQL with the Rust SDK
description: Run queries, typed query_as selects, parameters, transactions and batches with the Rust SDK.
---

# Typed SQL with the Rust SDK

The Rust SDK speaks TerraScale's SQL API natively: one statement or batch per call, typed row decoding through serde, and a WebSocket session for interactive transactions. This guide covers setup, parameterized statements, typed `query_as` selects, transactions and batches. For the SQL model itself, see [SQL](/concepts/sql).

## Install

The Rust SDK is not published. The source stays in `sdk/rust`; there is no crates.io package and no install command.

Export your project ID and API key. The key needs **Read and write** access to the database for the inserts and transactions below:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
```

## Connect

`Client::for_project_api_key` exchanges the API key for short-lived project access tokens and refreshes them automatically. `database("main").sql()` is the SQL handle; every call on it is one HTTP request and one transaction:

```rust
use terrascale::v1::{new_id, Client, SqlStatement};

#[tokio::main]
async fn main() -> terrascale::v1::Result<()> {
    let project = std::env::var("TERRASCALE_PROJECT_ID").expect("TERRASCALE_PROJECT_ID");
    let api_key = std::env::var("TERRASCALE_API_KEY").expect("TERRASCALE_API_KEY");

    let client = Client::for_project_api_key(project, api_key).build()?;
    let db = client.database("main").sql();

    db.execute(
        "CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
        &[],
    )
    .await?;
    Ok(())
}
```

## Insert with parameters

Always bind values as parameters; `SqlValue` covers every SQL type and converts from Rust scalars with `.into()`:

```rust
let ada = new_id();
let inserted = db
    .execute(
        "INSERT INTO people (id, name) VALUES (?, ?)",
        &[ada.as_str().into(), "Ada".into()],
    )
    .await?;
```

`SqlStatementResult` reports `rows_affected` and `last_insert_rowid`. Named parameters work with `execute_named`, keyed without the `:`, `@` or `$` prefix:

```rust
db.execute_named(
    "INSERT INTO people (id, name) VALUES (:id, :name)",
    [("id", ada.as_str()), ("name", "Grace")],
)
.await?;
```

## Typed rows with query_as

`query_as::<T>` maps each row to your struct by column name through serde, so the struct's field names must match the column names. Blobs arrive as base64 strings:

```rust
#[derive(serde::Deserialize)]
struct Person {
    id: String,
    name: String,
}

let people: Vec<Person> = db
    .query_as("SELECT id, name FROM people WHERE name = ?", &["Ada".into()])
    .await?;
```

Untyped rows work too: `query` returns `SqlRecord` values you read by column name or ordinal with `get_str`, `get_i64`, `get_f64`, `get_bool` or `get_bytes`.

## Transactions over the session

`database("main").connect()` opens a SQL WebSocket session. Outside a transaction every call is its own transaction; `transaction` begins one, runs your closure and commits, rolling back if the closure fails. **A transaction lasts at most 5 seconds**: past the limit the server rolls it back, so keep transactions small:

```rust
let session = client.database("main").connect().await?;

session
    .transaction(|s| async move {
        s.execute("INSERT INTO ledger (id, amount) VALUES (?, ?)", &[new_id().as_str().into(), 25.into()]).await?;
        s.execute("UPDATE totals SET n = n + 1", &[]).await
    })
    .await?;

session.close().await;
```

`begin`, `commit` and `rollback` are also available directly when you need manual control. If the socket drops mid-transaction, the server has already rolled it back: every call except `rollback` returns `transaction_rolled_back` until you call it to clear the lost state.

## Batches

`batch` runs 1 to 256 statements as one all-or-nothing transaction over HTTP, no session needed, and returns one result per statement in order:

```rust
db.batch(vec![
    SqlStatement::new("UPDATE people SET name = ? WHERE id = ?").args(["Grace", ada.as_str()]),
    SqlStatement::new("DELETE FROM people WHERE name = ''"),
])
.await?;
```

## ORMs

sqlx, SeaORM and Diesel are **not supported** against TerraScale today. sqlx has no third-party backend extension point for an HTTP/WebSocket database driver; SeaORM is built on sqlx and inherits that limit; Diesel's SQLite backend links the SQLite C library directly and is synchronous, with connection traits TerraScale's HTTP/WebSocket client cannot implement. Use the SDK's typed `query_as` queries, parameters, transactions and batches instead — for anything schema-shaped, plain `CREATE TABLE` and `ALTER TABLE` statements over `execute` cover it.

For errors, typed failures and safe retries, see [Errors and safe retries](/guides/errors).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
