# Rust: native typed SQL

Source: [Rust SQL guide](https://docs.terrascale.tech/guides/rust-sql).

## 1. Check availability before setup

The Rust SDK is unpublished and remains in sdk/rust. There is no crates.io
package or documented registry install command. Do not invent cargo add commands
for it. Use source only after reviewing that checkout's build instructions;
otherwise choose another documented SQL surface.

sqlx, SeaORM and Diesel are unsupported. sqlx lacks the needed third-party driver
extension point; SeaORM inherits that constraint. Diesel's synchronous SQLite
connection cannot implement this HTTP/WebSocket client.
Use the SDK's typed query_as, parameters, transactions and batches.
Source: [Rust availability and ORMs](https://docs.terrascale.tech/guides/rust-sql).

## 2. Connect and define schema

In an application already wired to the source SDK, supply project ID and key
through TERRASCALE_PROJECT_ID and TERRASCALE_API_KEY:

~~~rust
use terrascale::v1::Client;

let project = std::env::var("TERRASCALE_PROJECT_ID").expect("TERRASCALE_PROJECT_ID");
let api_key = std::env::var("TERRASCALE_API_KEY").expect("TERRASCALE_API_KEY");
let database = std::env::var("TERRASCALE_DATABASE_NAME").expect("TERRASCALE_DATABASE_NAME");
let client = Client::for_project_api_key(project, api_key).build()?;
let db = client.database(&database).sql();
~~~

These statements belong inside the application's async result-returning function.
The client exchanges and refreshes tokens; no driver connection string is needed.
Keep the schema in a ts-owned migration:

~~~sql
CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT NOT NULL);
~~~

Apply it to the preview before querying. The native API also accepts CREATE TABLE
and ALTER TABLE through execute, but do not independently mutate an owned history.
Sources: [Rust SQL](https://docs.terrascale.tech/guides/rust-sql),
[migrations](https://docs.terrascale.tech/guides/migrations).

## 3. Verify typed queries and limits

~~~rust
#[derive(serde::Deserialize)]
struct Person {
    id: String,
    name: String,
}

let people: Vec<Person> = db
    .query_as("SELECT id, name FROM people WHERE name = ?", &["Ada".into()])
    .await?;
~~~

Bind values using SqlValue conversions. Fields match column names; blobs decode
as base64 strings. Each HTTP call is a transaction; batch accepts 1–256 statements.
Interactive transactions use a SQL WebSocket session and last at most five seconds.
If the socket drops mid-transaction, call rollback to clear transaction_rolled_back
before further work. Source: [Rust transactions](https://docs.terrascale.tech/guides/rust-sql).
