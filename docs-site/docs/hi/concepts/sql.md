---
title: SQL
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# SQL

Each TerraScale database is a SQL database in the SQLite dialect. It commits writes in one home region: São Paulo (`gru`), the San Francisco Bay Area (`sjc`) or Frankfurt (`fra`). Encrypted read replicas can serve REST reads in other regions. See [regions](/hi/concepts/regions) and [replication](/hi/concepts/replication).

## Quickstart

You need a database named `app` and an API key with read and write access to it. The [getting started guide](/hi/getting-started/) creates both.

### CLI

[Install the CLI](/hi/cli), then:

```sh
ts auth login
ts sql app "CREATE TABLE customers (id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, seats INTEGER NOT NULL)"
ts sql app "INSERT INTO customers (email, seats) VALUES ('team@example.com', 12)"
ts sql app "SELECT email, seats FROM customers WHERE seats >= 10"
```

`ts sql` runs one statement in its own transaction and prints rows as JSON lines. With several projects, put `-project <name>` before `sql`.

### psql

The API key is the password; the database name in the URL selects the database.

```sh
read -rs PGPASSWORD && export PGPASSWORD  # paste the API key, then press Enter
psql "postgresql://app@pg.terrascale.tech:5432/app?sslmode=verify-full&sslrootcert=system"
```

```sql
SELECT email, seats FROM customers WHERE seats >= 10;
```

Any Postgres driver connects the same way. Statements still use the SQLite dialect, so Postgres-only syntax and functions are refused.

### HTTP

Exchange the API key for an access token as in [getting started](/hi/getting-started/#exchange-the-key-for-an-access-token), then send statements to `sql/execute`. Use your project ID from the dashboard or `ts projects`.

```bash
curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' \
  -d '{"statements":[{"sql":"SELECT email, seats FROM customers WHERE seats >= ?","args":[10]}]}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/sql/execute" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

```json
{"results":[{"columns":[{"name":"email","decltype":"TEXT"},{"name":"seats","decltype":"INTEGER"}],"rows":[["team@example.com",12]],"rows_affected":0,"last_insert_rowid":null}]}
```

The C#, TypeScript, Go, Python and Rust SDKs call the same endpoint; see [SDKs](/hi/guides/sdks).

## Query from your application

<SdkQueryTabs />

## What SQL supports

- Tables, indexes (including partial and expression indexes), views and triggers.
- Foreign keys are always enforced, `ON DELETE CASCADE` included.
- JSON is stored as `TEXT`; `json_extract`, `->`, `->>` and `json_each` work on it.
- Values are null, 64-bit integers, doubles, text and blobs. Over HTTP a blob is `{"base64":"…"}`; over the Postgres wire it is `bytea`.

## Transactions

- HTTP: each request is one transaction of 1 to 256 statements, all or nothing. `BEGIN`, `COMMIT` and `ROLLBACK` are refused.
- WebSocket: `GET /v1/projects/{project}/databases/{database}/sql/ws` with subprotocol `terrascale.sql.v1`. Send the access token as an `Authorization` header or, from a browser, as a first `{"type":"auth","token":"…"}` frame. `execute` frames run statements as over HTTP; `begin`, `commit` and `rollback` frames hold a transaction of at most 5 seconds.
- Postgres wire: `BEGIN`, `COMMIT`, `ROLLBACK` and savepoints work. A transaction lives at most 5 seconds, then rolls back.
- Without a transaction, each statement commits on its own.

## Limits

| Limit | Value |
| --- | --- |
| Statement | 16 KiB |
| HTTP request body | 1 MiB |
| Result | 10,000 rows and 8 MiB |
| Rows changed per transaction | 1,024 |
| File growth per transaction | 8 MiB |

## Refused statements

`ATTACH`, `DETACH`, `VACUUM`, `ANALYZE`, temporary and virtual tables, and names starting with `_tb_`, `turso_` or `sqlite_stat` are refused. `PRAGMA` is limited to `table_info`, `table_xinfo`, `table_list`, `index_list`, `index_info`, `index_xinfo`, `foreign_key_list`, `foreign_key_check` and `foreign_keys`. `sqlite_schema` is readable.

## Errors

Errors use TerraScale codes and PostgreSQL SQLSTATEs across REST, WebSocket and the Postgres wire. For example, `unique_violation` carries `23505`, `foreign_key_violation` carries `23503`, and a retryable `serialization_conflict` carries `40001`. A refused HTTP statement rolls back the request and returns an `error` with `code`, `message`, `statement` and `sqlstate`. Writes with a read-only key answer `403 forbidden_scope` over HTTP and `42501` over the Postgres wire.

Reads need the `sql:read` scope; writes and schema changes also need `sql:write`. A **Read and write** key from the dashboard has both.

## Live shapes and table grants

Live shapes send a table snapshot followed by committed changes. Select rows with `where` and select columns with
`columns`; rows enter or leave the shape as they start or stop matching. In the JavaScript SDK:

```js
const open = db.shape("todos", { where: "done = 0", columns: ["id", "title"] })
  .subscribe(({ rows }) => render(rows));
// When the view closes:
open.close();
```

End-user tokens can read live shapes for tables with a table read grant. A filter limits the shape result; authorization comes from
the grant, not the filter. Keep privileged API keys on your server. See the
[JavaScript SDK](https://github.com/TerraScale/terrascale/tree/main/sdk/js#live-sql-table-shapes).

## Agent databases

Create databases with a TTL, fork an existing database or a point in time, and issue a key scoped to one database
with read or read-write access. The database is deleted on expiry. See [agent databases](/hi/guides/agent-databases).

## Recovery and deletion

Point-in-time recovery restores into a new database within the source's recovery window and needs a free database slot. In-place point-in-time
restore is coming. Database deletion destroys backup data keys, making retained encrypted backups unreadable;
physical cleanup is separate. This does not apply to deleting individual SQL rows. Independent forks remain available.

## Usage caps

Plans enforce a monthly write cap and SQL read fair use. Reaching the write cap stops new writes; reaching SQL read
fair use stops SQL reads, while shape reads stay available. Customer-set hard dollar spending caps are coming.
See [pricing](/hi/pricing).

</div>
