---
title: Connect with psql and Postgres drivers
description: Reach a TerraScale database over the Postgres wire protocol with psql, node-postgres, psycopg, pgx or Npgsql.
---

# Connect with psql and Postgres drivers

TerraBase serves the PostgreSQL wire protocol 3.0 at `pg.terrascale.tech:5432`, so a stock Postgres client connects without plugins. The wire aims at the roughly 95% of Postgres that applications, drivers and ORMs actually use: `psql`, `node-postgres`, `Npgsql` and EF Core, Drizzle, Prisma, `pgx`, `psycopg` and `sqlx`. Postgres syntax is translated on tokens before it runs, so literals and identifiers are never rewritten. Every region of the database's set accepts the connection and commits writes locally. See [SQL](/concepts/sql) for the SQL model and [limits](/limits) for the numbers.

## Connection parameters

| Parameter | Value |
| --- | --- |
| Host, port | `pg.terrascale.tech`, `5432` |
| Database | `<project ID>/<database>`, or just the database name (the key's project is used) |
| User | Any name; it is not checked |
| Password | A project API key from the [dashboard](https://dash.terrascale.tech/databases) |
| TLS | Required. Use `sslmode=verify-full` with the system roots (a public CA signs the certificate) |

Without TLS the server refuses the connection with `28000 TLS is required` before any password is sent. psql 17 and newer can also connect with `sslnegotiation=direct`.

```sh
read -rs PGPASSWORD && export PGPASSWORD  # paste the API key, then press Enter
psql "host=pg.terrascale.tech port=5432 dbname=<project ID>/<database> user=any sslmode=verify-full sslrootcert=system"
```

## Credentials

The password is a **project API key** created in the dashboard (**Connect → Create an API key**; see [authentication](/guides/authentication)). Any user name works; the server reports it back as `current_user`.

- Reads need the key's `sql:read` scope; writes and schema changes also need `sql:write`. A write with a read-only key answers `42501`.
- A key restricted to one database only connects to that database.
- A wrong, unknown or expired key, or another project's database, answers `28P01 password authentication failed`. A revoked key answers `28000`; a client outside the key's IP allowlist answers `28000 ip_not_allowed`.

## Postgres syntax

You write Postgres; TerraBase translates it. The common spellings drivers and ORMs send work as-is:

| You send | Notes |
| --- | --- |
| `$1`, `$2` … placeholders | Up to `$32767`. |
| `expr::type`, `CAST(expr AS type)` | `int`, `bigint`, `text`, `boolean`, `numeric`, `uuid`, `jsonb`, `timestamptz`, `date` and the other common types. |
| `SERIAL`, `BIGSERIAL`, `GENERATED … AS IDENTITY` | Ids are generated so that two regions never pick the same value. `CREATE SEQUENCE` answers `0A000`. |
| `BOOLEAN`, `UUID`, `JSONB`, `TIMESTAMPTZ` columns | Stored and returned with Postgres type OIDs, in text or binary format. |
| `ILIKE`, `NOW()`, `CURRENT_TIMESTAMP`, `EXTRACT(… FROM …)` | Case-insensitive matching and the usual date functions. |
| `INSERT … ON CONFLICT … DO UPDATE / DO NOTHING`, `RETURNING` | Upserts and returned rows, including on `UPDATE` and `DELETE`. |
| `$$…$$` dollar quotes and `E'…'` escape strings | Accepted in any statement. |
| `COPY … FROM STDIN`, `COPY … TO STDOUT` | Text, CSV and binary formats. `COPY` to or from a server file or program answers `0A000`. |

The SQLite-dialect spellings every other TerraScale surface uses keep working on the wire too.

## Transactions and consistency

`BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT`, `RELEASE SAVEPOINT` and `ROLLBACK TO SAVEPOINT` work, including `BEGIN READ ONLY` (a write in it answers `25006`). A transaction lives at most **5 seconds**; past the limit it rolls back with SQLSTATE `40000`. Without a transaction, each statement commits on its own. Several statements sent as one simple query in autocommit mode run as one implicit transaction: a failure rolls back the whole message.

Transactions run in [fast mode](/concepts/replication#fast-mode) by default: committed in the region you are connected to, then replicated. A serializable transaction runs in [strong mode](/concepts/replication#strong-mode), agreed on two of three regions:

```sql
BEGIN ISOLATION LEVEL SERIALIZABLE;   -- or BEGIN STRONG, or SET TRANSACTION ISOLATION LEVEL SERIALIZABLE
UPDATE accounts SET balance = balance - 25 WHERE id = 'acct_1';
UPDATE accounts SET balance = balance + 25 WHERE id = 'acct_2';
COMMIT;
```

A strong transaction that loses a race answers SQLSTATE `40001`; run it again. `SET terrascale.consistency = strong` makes every later transaction of the session strong, and `SET default_transaction_isolation = 'serializable'` does the same. Schema changes and writes to tables with a `UNIQUE` constraint other than the primary key always run strong.

After every commit the server reports the session's newest bookmark as the `terrascale.bookmark` parameter (`SHOW terrascale.bookmark` reads it). A session always reads its own writes. To read them from another connection, possibly in another region, run `SET terrascale.bookmark = '<value>'` there first; the next statement waits until that region has applied those writes.

## Catalog queries, settings and notifications

- The `pg_catalog` and `information_schema` views that drivers, ORMs and migration tools introspect are answered from the database's own tables: `pg_class`, `pg_attribute`, `pg_type`, `pg_index`, `pg_constraint`, `pg_namespace`, `pg_proc`, `pg_settings`, `information_schema.tables`, `information_schema.columns` and others. psql's `\d`, `\dt`, `\di`, `\l` and `\dn` work.
- `LISTEN channel` delivers a notification for every committed change of the database, from any region, with a JSON payload carrying `database`, `tx`, `origin`, `sequence`, `schemaEpoch`, `mode` and `committedAt`. `UNLISTEN` stops it. A session listens on at most 64 channels (`54000` past that). `NOTIFY` answers `0A000`: notifications come from committed changes.
- `SET` and `SHOW` work for the usual session settings (`client_encoding`, `DateStyle`, `TimeZone`, `search_path`, `application_name`, `statement_timeout`, `extra_float_digits` and others) and for `terrascale.consistency` and `terrascale.bookmark`. `server_version` reports Postgres 16.
- Extensions, `CREATE SEQUENCE`, SQL-level `PREPARE`/`EXECUTE` (use the extended protocol), two-phase commit and `DROP SCHEMA` answer `0A000`.

## Prepared statements

The extended protocol works: `Parse`/`Bind`/`Describe`/`Execute`/`Close`, with at most 256 named statements and 256 portals per session, and at most 16 MiB of bound parameters and pending rows across all portals (`54000` past any of them). `Sync` closes the unnamed portal. Untyped parameters are inferred from the statement's tables. Text and binary result formats both work. `Execute` with a row limit (`maxRows`) returns the rows in chunks, so drivers that fetch with a cursor work unchanged.

## Timeouts and cancellation

The handshake must finish within 10 seconds and each protocol message is at most 1 MiB. Statements run under the plan's [query timeout and scan budget](/limits); past them the statement is canceled with `57014`, like a client cancel (Ctrl+C in psql), which the server also honors. Past the request-rate limits the wire answers SQLSTATE `53300`.

## Drivers

Any Postgres driver connects with the parameters above. Short examples, all running `SELECT email, seats FROM customers WHERE seats >= $1`:

::: code-group

```sh [psql]
psql "host=pg.terrascale.tech port=5432 dbname=<project ID>/<database> user=any sslmode=verify-full sslrootcert=system"
```

```js [node-postgres]
import pg from "pg";

const client = new pg.Client({
  host: "pg.terrascale.tech",
  port: 5432,
  database: `${process.env.TERRASCALE_PROJECT_ID}/${process.env.TERRASCALE_DATABASE_NAME}`,
  user: "any",
  password: process.env.TERRASCALE_API_KEY,
  ssl: true, // verifies the certificate against the system roots
});
await client.connect();
const { rows } = await client.query(
  "SELECT email, seats FROM customers WHERE seats >= $1", [10]);
await client.end();
```

```python [psycopg]
import os, psycopg

with psycopg.connect(
    host="pg.terrascale.tech", port=5432,
    dbname=f"{os.environ['TERRASCALE_PROJECT_ID']}/{os.environ['TERRASCALE_DATABASE_NAME']}",
    user="any", password=os.environ["TERRASCALE_API_KEY"],
    sslmode="verify-full") as conn:
    with conn.cursor() as cur:
        cur.execute("SELECT email, seats FROM customers WHERE seats >= %s", (10,))
        rows = cur.fetchall()
```

```go [Go pgx]
ctx := context.Background()
cfg, err := pgx.ParseConfig(
    "postgres://any@pg.terrascale.tech:5432/<project ID>/<database>?sslmode=verify-full")
if err != nil {
    return err
}
cfg.Password = os.Getenv("TERRASCALE_API_KEY")
conn, err := pgx.ConnectConfig(ctx, cfg)
if err != nil {
    return err
}
defer conn.Close(ctx)
var email string
var seats int
err = conn.QueryRow(ctx,
    "SELECT email, seats FROM customers WHERE seats >= $1", 10).
    Scan(&email, &seats)
```

```cs [.NET Npgsql]
await using var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
{
    Host = "pg.terrascale.tech",
    Port = 5432,
    Database = $"{projectId}/{database}",
    Username = "any",
    Password = apiKey,
    SslMode = SslMode.VerifyFull,
}.ConnectionString);
await using var command = dataSource.CreateCommand(
    "SELECT email, seats FROM customers WHERE seats >= $1");
command.Parameters.AddWithValue(10);
await using var reader = await command.ExecuteReaderAsync();
```

:::

Npgsql connects with its default settings: its startup type loading is answered by the server. `sqlx` connects with `PgConnectOptions` and the same parameters, and its compile-time query checks run against the catalog views. For ORMs see [Drizzle](/guides/orm-drizzle), [Prisma](/guides/orm-prisma), [.NET and EF Core](/guides/orm-dotnet) and [Go](/guides/orm-go). For SQL without a driver, see [SQL over HTTP](/guides/sql-http); the [SDKs](/guides/sdks) add automatic bookmarks over HTTP.
