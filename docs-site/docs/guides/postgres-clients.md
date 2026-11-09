---
title: Connect with psql and Postgres drivers
description: Reach a TerraScale database over the Postgres wire protocol with psql, node-postgres, psycopg, pgx or Npgsql.
---

# Connect with psql and Postgres drivers

TerraBase serves the PostgreSQL wire protocol 3.0 at `pg.terrascale.tech:5432`, so a stock Postgres client connects without plugins. The wire is a tested driver transport, not PostgreSQL compatibility: statements run in the [SQLite dialect](/concepts/sql), and Postgres-only syntax and functions are refused. See [SQL](/concepts/sql) for the SQL model and [limits](/limits) for the numbers.

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

## The SQL dialect

Statements use the SQLite dialect, like every TerraScale SQL surface. The wire accepts a few Postgres spellings and translates them on tokens, so literals and identifiers are never rewritten:

| You send | Runs as |
| --- | --- |
| `$1`, `$2` … placeholders | `?1`, `?2` … |
| `ILIKE` | `LIKE` (already case-insensitive for ASCII) |
| `SERIAL`, `BIGSERIAL`, `SMALLSERIAL` in `CREATE TABLE` | `INTEGER` |
| `now()` | `CURRENT_TIMESTAMP` |
| `expr::type` | `CAST(expr AS type)` |

Dollar-quoted strings (`$$…$$`) and `E'…'` escape strings are refused with `0A000`. Parameter numbers must be `$1` to `$32767`.

## Transactions

`BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT`, `RELEASE SAVEPOINT` and `ROLLBACK TO SAVEPOINT` work, including `BEGIN READ ONLY` (a write in it answers `25006`). A transaction runs at most **5 seconds of statement execution**, and rolls back after 5 seconds with no statement at all; past the limit it rolls back with SQLSTATE `40000`. Without a transaction, each statement commits on its own. Several statements sent as one simple query in autocommit mode run as one implicit transaction: a failure rolls back the whole message.

## Catalog queries, settings and notifications

- psql's `\d`, `\d table`, `\dt`, `\l` and `\dn` are answered from the database's own tables. Any other `pg_catalog` query answers `0A000`.
- `LISTEN`, `NOTIFY`, `UNLISTEN` and `CREATE/ALTER/DROP EXTENSION` answer `0A000`.
- `SET` accepts only `client_encoding` (UTF8), `DateStyle` (ISO), `TimeZone` (UTC and its aliases), `extra_float_digits`, `application_name`, `search_path` and `terrascale.write`. `SHOW` answers these and the settings reported at connect (`server_version` is `16.0`).

## Write routing

`SET terrascale.write` chooses where subsequent statements commit: `home` (the database's home region, for read-your-writes), `local` (the closest region, when it accepts local writes) or `auto` (the default). `SHOW terrascale.write_route` reports the route the last write took. See [multi-region writes and conflicts](/guides/multi-region-writes-and-conflicts).

## Prepared statements

The extended protocol works: `Parse`/`Bind`/`Describe`/`Execute`/`Close`, with at most 256 named statements and 256 portals per session (`54000` past either). Untyped parameters are inferred from the statement's tables. Text result format always works; binary format works for the types the server encodes. `Execute` with a row limit (`maxRows`) answers `0A000`, so drivers that fetch in chunks must page with `LIMIT` instead.

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

Npgsql connects with its default settings: its startup type loading is answered by the server. Prefer the [SDKs](/guides/sdks) inside applications; the wire fits tools and dashboards. For SQL without a driver, see [SQL over HTTP](/guides/sql-http).

ORMs that need Postgres catalog introspection aren't supported yet; use the SQL API or an SDK.
