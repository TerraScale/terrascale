---
title: Connect with psql and Postgres drivers
description: Connect psql, node-postgres, psycopg, pgx, Npgsql and JDBC to a TerraBase database over the PostgreSQL wire protocol, with the supported SQL, transactions, limits and SQLSTATE codes.
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Connect with psql and Postgres drivers

TerraBase databases accept connections over the PostgreSQL wire protocol (version 3). Use `psql` or a standard Postgres driver: you change the connection URL, not your client code. Tables are TerraBase collections and rows are documents, so SQL writes replicate across regions like writes through the Documents API.

::: tip Verified
Checked on 2026-10-23 against production (`pg.terrascale.tech`) with `psql` 18, `pg` 8.23.1, `psycopg` 3.3.6, `pgx` v5.11.0 and `Npgsql` 10.0.3: tables, writes, joins, parameters, prepared statements and transactions.
:::

## Connection URL

::: warning Dashboard credentials are not accepted yet
`pg.terrascale.tech` checks the password against TerraBase-internal keys. Customers cannot obtain those keys. API keys from the dashboard's **API keys** page, and the access tokens exchanged from them, fail with `28P01 password authentication failed`. Support for access tokens as the password is in progress; this page will document it once it is live. Until then, use [SQL over HTTP](/de/guides/sql-http) (`POST /sql`) with your API key.
:::

```text
postgresql://{label}:{api-key}@pg.terrascale.tech:5432/{database}?sslmode=require
```

| Part | Value |
| --- | --- |
| `{label}` | Any user name. TerraBase ignores it; use it to tell your applications apart in your own logs. |
| `{api-key}` | The password. See the note above for the credential it accepts today. |
| `{database}` | The database name in the key's project. With an organization-wide key, use `{projectId}/{name}`, written `{projectId}%2F{name}` in a URL. |
| `sslmode=require` | Required. TLS is mandatory and plaintext connections are refused. |

Use a key with `documents:read` for reads and `documents:write` for writes and `CREATE`/`DROP`. Create it in the dashboard, keep it in your server's secret store, and pass the URL to your application through an environment variable. Never commit it, put it on a command line or log it.

## psql

```sh
export DATABASE_URL='postgresql://app:YOUR_API_KEY@pg.terrascale.tech:5432/shop?sslmode=require'
psql "$DATABASE_URL"
```

```sql
CREATE TABLE customers (id integer PRIMARY KEY, name text NOT NULL);
INSERT INTO customers (id, name) VALUES (1, 'Ada'), (2, 'Grace');
SELECT name FROM customers WHERE id = 1;
```

`\dt`, `\d`, `\d customers`, `\l` and `\dn` work. With libpq 16 or later, `sslmode=verify-full&sslrootcert=system` also verifies the server certificate against the system trust store.

## Node.js (pg)

```sh
npm install pg
```

```js
import pg from 'pg'

const client = new pg.Client({ connectionString: process.env.DATABASE_URL })
await client.connect()

await client.query('INSERT INTO customers (id, name) VALUES ($1, $2)', [3, 'Linus'])
const { rows } = await client.query('SELECT name FROM customers WHERE id = $1', [3])

// A named statement is prepared once per connection and reused.
await client.query({ name: 'customer-by-id', text: 'SELECT name FROM customers WHERE id = $1', values: [1] })

await client.end()
```

## Python (psycopg)

```sh
pip install "psycopg[binary]"
```

```python
import os
import psycopg

with psycopg.connect(os.environ["DATABASE_URL"]) as conn:
    conn.execute("INSERT INTO customers (id, name) VALUES (%s, %s)", (3, "Linus"))
    conn.commit()
    row = conn.execute("SELECT name FROM customers WHERE id = %s", (3,)).fetchone()
```

psycopg opens a transaction for the first statement and holds it until `commit()`. Pass `autocommit=True` to `connect` to run each statement on its own.

## Go (pgx)

```sh
go get github.com/jackc/pgx/v5
```

```go
import (
	"context"
	"fmt"
	"os"

	"github.com/jackc/pgx/v5"
)

conn, err := pgx.Connect(ctx, os.Getenv("DATABASE_URL"))
defer conn.Close(ctx)

tag, err := conn.Exec(ctx, "UPDATE customers SET name = $1 WHERE id = $2", "Ada L.", 1)
fmt.Println(tag.RowsAffected())

var name string
err = conn.QueryRow(ctx, "SELECT name FROM customers WHERE id = $1", 1).Scan(&name)
```

## .NET (Npgsql)

```sh
dotnet add package Npgsql
```

Npgsql takes a keyword connection string, not a URL:

```csharp
using Npgsql;

var connectionString = "Host=pg.terrascale.tech;Port=5432;Database=shop;Username=app;SSL Mode=Require;Password="
    + Environment.GetEnvironmentVariable("TERRABASE_API_KEY");
await using var dataSource = NpgsqlDataSource.Create(connectionString);

await using var cmd = dataSource.CreateCommand("SELECT name FROM customers WHERE id = $1");
cmd.Parameters.AddWithValue(1);
var name = (string?)await cmd.ExecuteScalarAsync();
```

## Java (JDBC)

```java
var props = new java.util.Properties();
props.setProperty("user", "app");
props.setProperty("password", System.getenv("TERRABASE_API_KEY"));
props.setProperty("sslmode", "require");

try (var conn = java.sql.DriverManager.getConnection("jdbc:postgresql://pg.terrascale.tech:5432/shop", props);
     var stmt = conn.prepareStatement("SELECT name FROM customers WHERE id = ?")) {
    stmt.setInt(1, 1);
    try (var rs = stmt.executeQuery()) {
        while (rs.next()) System.out.println(rs.getString(1));
    }
}
```

Use the PostgreSQL JDBC driver (`org.postgresql:postgresql`).

## Transactions

- A statement outside `BEGIN` commits on its own.
- `BEGIN` … `COMMIT` is optimistic: it takes no locks. At `COMMIT`, TerraBase replays the transaction and checks that everything it read is unchanged. If another writer changed it, `COMMIT` fails with SQLSTATE `40001` and nothing is written. Retry the whole transaction, from `BEGIN`.
- `ROLLBACK` discards the transaction.
- Committed writes replicate to every region like Documents API writes.

## Supported

| Feature | Status |
| --- | --- |
| Simple and extended query protocol, parameters (`$1`, `$2`, …) | Supported |
| Prepared statements (named and unnamed) | Supported, up to 256 per connection |
| `CREATE TABLE`, `DROP TABLE` | Supported |
| `INSERT`, `UPDATE`, `DELETE` with affected-row counts | Supported |
| `SELECT` with joins | Supported |
| `BEGIN`, `COMMIT`, `ROLLBACK` | Supported, optimistic (see [Transactions](#transactions)) |
| `psql` `\dt`, `\d`, `\d table`, `\l`, `\dn` | Supported |
| Query cancel and statement timeout | Supported |
| `COPY` | Not supported |
| `LISTEN` / `NOTIFY` | Not supported |
| Extensions (`CREATE EXTENSION`) | Not supported |
| Server-side cursors (fetching a portal in row batches) | Not supported |
| Large objects | Not supported |
| Catalog introspection by GUI tools beyond the `psql` commands above | Not supported |
| Superuser and server administration commands | Not supported |

Drivers that fetch results in batches through a cursor need that turned off: for example, leave JDBC `setFetchSize` at `0`.

## Limits

- Statement timeouts and request rate limits are the same as for SQL over the TerraBase HTTP API. A busy project gets SQLSTATE `53300` with a retry hint; wait and retry.
- A statement that passes a row, size or work limit fails with `54000`.
- A connection holds at most 256 prepared statements. Close statements you no longer use, or use unnamed ones.

## SQLSTATE codes

| SQLSTATE | Meaning | What to do |
| --- | --- | --- |
| `23505` | Unique or primary key violation | Fix the data, or handle the duplicate |
| `28000` | Connection refused: TLS missing | Add `sslmode=require` |
| `28P01` | Authentication failed | Check the API key and the database name |
| `40001` | Transaction conflict at `COMMIT` | Retry the whole transaction |
| `42501` | The key cannot do this, for example a write with a read-only key | Use a key with `documents:write` |
| `53300` | The project is busy | Wait for the retry hint, then retry |
| `54000` | A limit was passed | Narrow the query or split the work |
| `57014` | Statement timeout or cancel | Narrow the query; retry if you cancelled it |
| `0A000` | Feature not supported | See [Supported](#supported) |

## Troubleshooting

- **`28000` on connect.** The client connected without TLS. Add `sslmode=require` to the URL, or `SSL Mode=Require` for Npgsql.
- **`28P01` on connect.** The API key is wrong, revoked or from another project, or the database name does not exist in the key's project. With an organization-wide key, use `{projectId}%2F{name}` as the database.
- **`0A000`.** The statement or protocol feature is not supported; check [Supported](#supported). GUI tools that browse the system catalog are a common source.
- **`40001` at `COMMIT`.** Another writer changed what your transaction read. Retry from `BEGIN`.

Never log the connection URL, the password or the API key while diagnosing a connection.

</div>
