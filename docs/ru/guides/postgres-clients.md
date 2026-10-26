---
title: Connect with psql and Postgres drivers
description: Connect psql, node-postgres, psycopg, pgx, Npgsql and JDBC to a TerraBase database over the PostgreSQL wire protocol, with the supported SQL, transactions, limits and SQLSTATE codes.
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Connect with psql and Postgres drivers

TerraBase provides a PostgreSQL v3 (PGv3) driver adapter over the TerraBase SQL subset. Postgres driver connectivity does not imply full PostgreSQL SQL, native-type or ORM compatibility. SQL writes replicate across regions like Documents API writes. SQL tables and Documents API collections have different schemas and query contracts, but share a database naming namespace. The examples use `sql_customers`, separate from the Documents quickstart's `customers` collection.

::: tip Verified
Checked on 2026-10-24 against production (`pg.terrascale.tech`) with a dashboard API key as the password: `psql` 18, `pg` 8.23.1 and `pgx` v5.11.0 ran tables, writes, joins, parameters, prepared statements, transactions and indexes; a read-only key got `42501` on writes, and a revoked key was refused within a minute.
:::

The [Drizzle](/ru/guides/orm-drizzle), [Prisma](/ru/guides/orm-prisma) and [SQLAlchemy](/ru/guides/orm-sqlalchemy) guides use SQLite/libSQL, not this adapter. The current live adapter does not support `RETURNING`, savepoints, schema-qualified names or general PostgreSQL catalog reflection; native binary UUID, numeric, temporal, bytea and array bindings may be rejected. Foreign-key declarations are not enforced. Test your exact ORM/provider, generated SQL and native types; the dated verification above covers only the listed driver flows.

## Connection URL

```text
postgresql://{label}@pg.terrascale.tech:5432/{database}?sslmode=verify-full&sslrootcert=system
```

| Part | Value |
| --- | --- |
| `{label}` | Any user name. TerraBase ignores it; use it to tell your applications apart in your own logs. |
| Password (supply separately via `PGPASSWORD`) | The password: an API key from the dashboard's **API keys** page (`<key ID>_<secret>`), as is. No token exchange. Its database restriction and permissions apply. A revoked key is refused within about a minute. |
| `{database}` | The database name in the key's project. With an organization-wide key, use `{projectId}/{name}`, written `{projectId}%2F{name}` in a URL. |
| `sslmode=verify-full&sslrootcert=system` | For libpq 16+: require TLS and verify the certificate chain and hostname using installed system CA certificates. Encryption alone (`require`) does not guarantee hostname verification. |

Every connection needs `documents:read`; add `documents:write` for writes and `CREATE`/`DROP`. Create it in the dashboard, keep it in your server's secret store, and pass the URL to your application through an environment variable. Never commit it, put it on a command line or log it.

Complete [Getting started](/ru/getting-started/) first and set `TERRASCALE_DATABASE_NAME` to your database name. Postgres uses the raw API key password; the 60-second lifetime of exchanged HTTP tokens does not apply to this connection. Run the psql table setup before the driver fragments. These snippets use synthetic IDs 1, 2 and 3 in a table reserved for the example.

## psql

```sh
read -rs PGPASSWORD && export PGPASSWORD  # paste the API key, then press Enter
export DATABASE_URL="postgresql://app@pg.terrascale.tech:5432/$TERRASCALE_DATABASE_NAME?sslmode=verify-full&sslrootcert=system"
psql "$DATABASE_URL"
```

```sql
CREATE TABLE IF NOT EXISTS sql_customers (id integer PRIMARY KEY, name text NOT NULL);
INSERT INTO sql_customers (id, name) VALUES (1, 'Ada'), (2, 'Grace') ON CONFLICT DO NOTHING;
SELECT name FROM sql_customers WHERE id = 1;
```

Use libpq 16+ for this system-trust example; older libpq needs `sslrootcert=/path/to/trusted-ca-bundle.pem` with `sslmode=verify-full`. `\dt`, `\d`, `\d sql_customers`, `\l` and `\dn` have limited support; descriptions are not complete PostgreSQL catalog metadata.

## Node.js (pg)

```sh
npm install pg
```

```js
import pg from 'pg'

const client = new pg.Client({
  host: 'pg.terrascale.tech', port: 5432, user: 'app',
  database: process.env.TERRASCALE_DATABASE_NAME, password: process.env.PGPASSWORD,
  ssl: { rejectUnauthorized: true },
})
await client.connect()

await client.query('INSERT INTO sql_customers (id, name) VALUES ($1, $2) ON CONFLICT DO NOTHING', [3, 'Linus'])
const { rows } = await client.query('SELECT name FROM sql_customers WHERE id = $1', [3])

// A named statement is prepared once per connection and reused.
await client.query({ name: 'customer-by-id', text: 'SELECT name FROM sql_customers WHERE id = $1', values: [1] })

await client.end()
```

Node uses its trusted CA certificates. Keep these explicit fields: URL options such as `sslmode` or `sslrootcert` can replace the `ssl` object if you add `connectionString`.

## Python (psycopg)

```sh
pip install "psycopg[binary]"
```

```python
import os
import psycopg

with psycopg.connect(os.environ["DATABASE_URL"], password=os.environ["PGPASSWORD"]) as conn:
    conn.execute("INSERT INTO sql_customers (id, name) VALUES (%s, %s) ON CONFLICT DO NOTHING", (3, "Linus"))
    conn.commit()
    row = conn.execute("SELECT name FROM sql_customers WHERE id = %s", (3,)).fetchone()
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

tag, err := conn.Exec(ctx, "UPDATE sql_customers SET name = $1 WHERE id = $2", "Ada L.", 1)
fmt.Println(tag.RowsAffected())

var name string
err = conn.QueryRow(ctx, "SELECT name FROM sql_customers WHERE id = $1", 1).Scan(&name)
```

## .NET (Npgsql)

```sh
dotnet add package Npgsql
```

Use the same `PGPASSWORD` environment variable as psql. Replace `shop` with your `TERRASCALE_DATABASE_NAME`. Npgsql takes a keyword connection string, not a URL:

```csharp
using Npgsql;

var connectionString = "Host=pg.terrascale.tech;Port=5432;Database=shop;Username=app;SSL Mode=VerifyFull;Password="
    + Environment.GetEnvironmentVariable("PGPASSWORD");
await using var dataSource = NpgsqlDataSource.Create(connectionString);

await using var cmd = dataSource.CreateCommand("SELECT name FROM sql_customers WHERE id = $1");
cmd.Parameters.AddWithValue(1);
var name = (string?)await cmd.ExecuteScalarAsync();
```

## Java (JDBC)

```java
var props = new java.util.Properties();
props.setProperty("user", "app");
props.setProperty("password", System.getenv("PGPASSWORD"));
props.setProperty("sslmode", "require");

try (var conn = java.sql.DriverManager.getConnection("jdbc:postgresql://pg.terrascale.tech:5432/shop", props);
     var stmt = conn.prepareStatement("SELECT name FROM sql_customers WHERE id = ?")) {
    stmt.setInt(1, 1);
    try (var rs = stmt.executeQuery()) {
        while (rs.next()) System.out.println(rs.getString(1));
    }
}
```

Use the PostgreSQL JDBC driver (`org.postgresql:postgresql`), the same `PGPASSWORD` environment variable and your database name in place of `shop`.

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
| `CREATE [UNIQUE] INDEX`, `DROP INDEX` | Supported; a `UNIQUE` index rejects duplicates with `23505` |
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

- **`28000` on connect.** Use the verifying TLS settings above: `sslmode=verify-full&sslrootcert=system` for libpq 16+, `SSL Mode=VerifyFull` for Npgsql, or the explicit Node `ssl` object. Keep certificate verification enabled.
- **`28P01` on connect.** The API key is wrong, revoked or from another project, or the database name does not exist in the key's project. With an organization-wide key, use `{projectId}%2F{name}` as the database.
- **`0A000`.** The statement or protocol feature is not supported; check [Supported](#supported). GUI tools that browse the system catalog are a common source.
- **`40001` at `COMMIT`.** Another writer changed what your transaction read. Retry from `BEGIN`.

Never log the connection URL, the password or the API key while diagnosing a connection.

</div>
