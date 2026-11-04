---
title: SQL
---

# SQL

Each TerraScale database is a SQL database in the SQLite dialect. It is stored and served in one home region, chosen when you create it: São Paulo (`gru`), the San Francisco Bay Area (`sjc`) or Frankfurt (`fra`). Its tables are not copied to other regions.

## Quickstart

You need a database named `app` and an API key with read and write access to it. The [getting started guide](/getting-started/) creates both.

### CLI

[Install the CLI](/cli), then:

```sh
ts auth login
ts exec app "CREATE TABLE customers (id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, seats INTEGER NOT NULL)"
ts exec app "INSERT INTO customers (email, seats) VALUES ('team@example.com', 12)"
ts exec app "SELECT email, seats FROM customers WHERE seats >= 10"
```

`ts exec` runs one statement in its own transaction and prints rows as JSON lines. With several projects, put `-project <name>` before `exec`.

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

Exchange the API key for an access token as in [getting started](/getting-started/#exchange-the-key-for-an-access-token), then send statements to `sql/execute`. Use your project ID from the dashboard or `ts projects`.

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

The C#, TypeScript, Go, Python and Rust SDKs call the same endpoint; see [SDKs](/guides/sdks).

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

Errors keep SQLite codes such as `SQLITE_CONSTRAINT_UNIQUE`, `SQLITE_CONSTRAINT_FOREIGNKEY`, `SQLITE_BUSY` and `SQLITE_TOOBIG`. Over HTTP a refused statement rolls back the request and answers `{"error":{"code","message","statement","sqlite_code"}}`. The Postgres wire maps them to SQLSTATE `23505`, `23503`, `40001`, `54000` and similar. A write with a read-only key answers `403 forbidden_scope` over HTTP and `42501` over the Postgres wire.

Reads need the `sql:read` scope; writes and schema changes also need `sql:write`. A **Read and write** key from the dashboard has both.
