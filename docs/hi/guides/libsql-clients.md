---
title: Connect with libSQL clients
description: Use the official libSQL clients for JavaScript, Go, Rust and Python to run SQL against a TerraBase database over HTTP, with the supported SQL, limits and error codes.
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Connect with libSQL clients

TerraBase databases accept SQL from the official libSQL clients (`@libsql/client`, `go-libsql`, the `libsql` Rust crate and the `libsql` Python package). The clients speak the libSQL remote protocol (Hrana) over HTTPS; you keep your existing client code and change the URL and token.

Verified on 2026-10-23 against production (`gru`) with `@libsql/client` 0.18.0, `go-libsql` (commit `3051e37e6e04`), the `libsql` crate 0.10.0 and `libsql` for Python 0.1.11: create table, parameterized insert, batch, join, and committed and rolled-back transactions.

## URL and token

```text
https://{api-origin}/v1/projects/{project}/databases/{database}/libsql/
```

Replace `{api-origin}` with your provisioned HTTPS API origin, and `{project}` and `{database}` with your IDs.


- Keep the trailing slash. `@libsql/client` resolves `v2/pipeline` relative to the URL; without the slash it drops the last path segment and the request misses your database.
- Use the `https://` URL. Only HTTP is served; WebSocket (`wss://`) URLs are not.
- The token is an access token minted from a project data API key with `documents:read` for reads and `documents:write` for writes and `CREATE`/`DROP` (the client-credentials exchange in [Authentication](/hi/guides/authentication#server-applications)). The raw API key is refused with `401`. Access tokens are short-lived (`expires_in`, 60 seconds today): mint a new one and recreate the client before it expires. Keep the key in your server's secret store and never ship it or a token to a browser.

## JavaScript and TypeScript

```sh
npm install @libsql/client
```

```js
import { createClient } from '@libsql/client'

const db = createClient({ url: process.env.LIBSQL_URL, authToken: process.env.LIBSQL_TOKEN })

await db.execute('CREATE TABLE IF NOT EXISTS customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL)')
await db.execute({ sql: 'INSERT INTO customers (id, name) VALUES (?, ?)', args: [1, 'Ada'] })
const { rows } = await db.execute({ sql: 'SELECT name FROM customers WHERE id = :id', args: { id: 1 } })

// A write batch runs as one transaction: all statements or none.
await db.batch([
  { sql: 'INSERT INTO customers (id, name) VALUES (?, ?)', args: [2, 'Grace'] },
  { sql: 'INSERT INTO customers (id, name) VALUES (?, ?)', args: [3, 'Linus'] },
], 'write')

const tx = await db.transaction('write')
try {
  await tx.execute({ sql: 'UPDATE customers SET name = ? WHERE id = ?', args: ['Ada L.', 1] })
  await tx.commit()
} finally {
  tx.close()
}
```

## Go

`go-libsql` links a native library, so it needs cgo and a C toolchain (prebuilt for linux/amd64, linux/arm64 and darwin/arm64).

```sh
go get github.com/tursodatabase/go-libsql
```

```go
import (
	"database/sql"
	"net/url"
	"os"

	_ "github.com/tursodatabase/go-libsql"
)

// go-libsql reads the token from the authToken query parameter.
u, _ := url.Parse(os.Getenv("LIBSQL_URL"))
u.RawQuery = url.Values{"authToken": {os.Getenv("LIBSQL_TOKEN")}}.Encode()
db, err := sql.Open("libsql", u.String())

_, err = db.ExecContext(ctx, "INSERT INTO customers (id, name) VALUES (?, ?)", 1, "Ada")
var name string
err = db.QueryRowContext(ctx, "SELECT name FROM customers WHERE id = ?", 1).Scan(&name)

// database/sql has no batch call; use a transaction.
tx, err := db.BeginTx(ctx, nil)
defer tx.Rollback()
_, err = tx.ExecContext(ctx, "INSERT INTO customers (id, name) VALUES (?, ?)", 2, "Grace")
_, err = tx.ExecContext(ctx, "INSERT INTO customers (id, name) VALUES (?, ?)", 3, "Linus")
err = tx.Commit()
```

Send one statement per `Exec`: `go-libsql` runs only the first statement of a multi-statement string.

## Rust

```sh
cargo add libsql --no-default-features --features remote,tls
cargo add tokio --features macros,rt-multi-thread
```

```rust
use libsql::{Builder, params};

let db = Builder::new_remote(std::env::var("LIBSQL_URL")?, std::env::var("LIBSQL_TOKEN")?).build().await?;
let conn = db.connect()?;

conn.execute("INSERT INTO customers (id, name) VALUES (?1, ?2)", params![1, "Ada"]).await?;
let mut rows = conn.query("SELECT name FROM customers WHERE id = ?1", params![1]).await?;
while let Some(row) = rows.next().await? {
    println!("{}", row.get::<String>(0)?);
}

// One transaction; the batch API takes SQL text without parameters.
conn.execute_transactional_batch(
    "INSERT INTO customers (id, name) VALUES (2, 'Grace'); INSERT INTO customers (id, name) VALUES (3, 'Linus');",
).await?;

let tx = conn.transaction().await?;
tx.execute("UPDATE customers SET name = ?1 WHERE id = ?2", params!["Ada L.", 1]).await?;
tx.commit().await?;
```

## Python

```sh
pip install libsql
```

```python
import os
import libsql

conn = libsql.connect(os.environ["LIBSQL_URL"], auth_token=os.environ["LIBSQL_TOKEN"])

# Like sqlite3: INSERT, UPDATE and DELETE open a transaction; commit() or rollback() ends it.
conn.execute("INSERT INTO customers (id, name) VALUES (?, ?)", (1, "Ada"))
conn.commit()
print(conn.execute("SELECT name FROM customers WHERE id = ?", (1,)).fetchone())

conn.executemany("INSERT INTO customers (id, name) VALUES (?, ?)", [(2, "Grace"), (3, "Linus")])
conn.commit()
```

The Python package takes positional parameters only and sends integers outside the 32-bit range as floating-point values; pass large integers as text.

## Supported SQL

- `SELECT`, `VALUES` and `WITH … SELECT`, including joins, subqueries, aggregates, `GROUP BY`, window functions, `UNION` and `LIMIT`/`OFFSET`.
- `INSERT` (with `VALUES` or `SELECT`), `INSERT OR REPLACE`, `REPLACE`, `INSERT OR IGNORE`, `ON CONFLICT DO NOTHING`, `UPDATE` and `DELETE`.
- `CREATE TABLE [IF NOT EXISTS]`, `DROP TABLE [IF EXISTS]`, `CREATE [UNIQUE] INDEX` and `DROP INDEX`. A `UNIQUE` index on a table with duplicate values is refused with `SQLITE_CONSTRAINT_UNIQUE`.
- `BEGIN` (`DEFERRED`, `IMMEDIATE`, `EXCLUSIVE`, `READONLY`), `COMMIT`/`END` and `ROLLBACK`.
- Parameters: `?`, `?NNN`, `:name`, `@name` and `$name`. Values: null, integer, real and text.
- Column types follow SQLite affinity (`INTEGER`, `REAL`, `TEXT`, `NUMERIC`, none). `NOT NULL`, literal `DEFAULT`, `PRIMARY KEY` and `UNIQUE` are enforced.
- Keys are yours: an `INTEGER` or `TEXT` primary key is the row's key, and composite keys work. An `INTEGER PRIMARY KEY` left out of an `INSERT` gets a generated positive integer, returned as the last insert rowid.

Refused with an error: `WITH RECURSIVE`, blob values, `RETURNING`, `ON CONFLICT DO UPDATE`, `ALTER TABLE`, triggers, views, `PRAGMA`, `ATTACH`, `VACUUM`, savepoints, `CHECK` and `FOREIGN KEY` constraints, schema-qualified table names and functions outside SQLite's core scalar, aggregate, window, date/time and JSON functions (no table-valued functions).

## Limits

- **Reads scan tables.** Indexes do not speed up SQL reads yet: every read scans each table it references. A read that would scan more than about 150,000 rows fails as too expensive. Keep SQL tables below that size, or use the [Documents API queries](/hi/guides/queries) for larger collections.
- A result holds at most 10,000 rows and 8 MiB. A write that would pass that fails; page larger reads with `LIMIT` and a key condition.
- A request body is at most 1 MiB, a statement's SQL text at most 16 KiB, and a request at most 1000 statements.
- **Transactions are optimistic.** A transaction that spans requests, such as `db.transaction()` in JavaScript or `BEGIN` in Go, Rust or Python, does not lock the database. At `COMMIT`, TerraBase checks that everything the transaction read is unchanged. If another writer changed it, the commit fails with `SQLITE_BUSY` and nothing is written; retry the whole transaction. Statements that use `random()` or the current time inside such a transaction can also fail at commit.
- A transaction holds at most 256 statements, 1 MiB of statements and arguments and 1024 written rows, and expires after 30 seconds without a request. An expired transaction answers "stream not found"; start it again.
- Writes from several regions to the same row merge per column, and `SET c = c + 1` style updates add across regions.

## Errors

SQL errors come back inside a successful HTTP response, and the client raises them with a code:

| Code | Meaning | What to do |
| --- | --- | --- |
| `SQLITE_ERROR` | Syntax error or unsupported statement | Fix the statement; see [Supported SQL](#supported-sql) |
| `SQLITE_CONSTRAINT_PRIMARYKEY`, `SQLITE_CONSTRAINT_UNIQUE`, `SQLITE_CONSTRAINT_NOTNULL` | A constraint rejected the write | Fix the data, or use `INSERT OR REPLACE` / `OR IGNORE` deliberately |
| `SQLITE_AUTH` | The key lacks the scope for this statement | Use a key with `documents:write` for writes and DDL |
| `SQLITE_BUSY` | Transaction conflict or busy database | Retry the whole transaction |
| `SQLITE_TOOBIG` | A size limit was passed | Split the request or result |
| `SQLITE_INTERRUPT` | The statement passed its work or time budget | Narrow the query or reduce the table size |

HTTP errors follow the [error guide](/hi/guides/errors): `401` and `403` for credentials, `429` with `Retry-After` when the project is over its request budget. Never log the token or the authorization header.

</div>
