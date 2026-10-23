---
title: SQL over HTTP
description: Run SQL statements against a TerraBase database with one HTTPS request, as one transaction, with parameters, typed results and SQLite error codes.
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# SQL over HTTP

Send SQL to a database with one `POST`. Use it from any language with an HTTP client, from serverless functions and from scripts. Tables are TerraBase collections and rows are documents, so SQL writes replicate across regions like writes through the Documents API.

The statements, types and limits are the same as for the [libSQL clients](/ja/guides/libsql-clients#supported-sql). To keep a connection open or reuse driver code, use the [libSQL clients](/ja/guides/libsql-clients) or [Postgres clients](/ja/guides/postgres-clients) instead.

## Request

```text
POST https://api.terrascale.tech/v1/projects/{project}/databases/{database}/sql/execute
Authorization: Bearer {access-token}
Content-Type: application/json
```

`{project}` is your project ID and `{database}` is the database name. The token is an access token minted from a dashboard API key (see [Authentication](/ja/guides/authentication)): `documents:read` for reads, `documents:write` for writes and `CREATE`/`DROP`.

```sh
curl -sS https://api.terrascale.tech/v1/projects/$PROJECT/databases/shop/sql/execute \
  -H "Authorization: Bearer $TERRASCALE_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"statements":[
        {"sql":"CREATE TABLE IF NOT EXISTS customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"},
        {"sql":"INSERT INTO customers (id, name) VALUES (?, ?)", "args":[1, "Ada"]},
        {"sql":"SELECT id, name FROM customers WHERE id = :id", "named":{"id":1}}
      ]}'
```

| Field | Value |
| --- | --- |
| `statements` | 1 to 256 statements |
| `statements[].sql` | One statement, at most 16 KiB |
| `statements[].args` | Positional arguments for `?` and `?NNN`: null, numbers, booleans (sent as 1 or 0) or strings |
| `statements[].named` | Named arguments for `:name`, `@name` and `$name`, keyed without the prefix |

**A request is one transaction.** Every statement runs in order, and either all of them commit or none do. `BEGIN`, `COMMIT` and `ROLLBACK` are refused; put the statements that belong together in one request.

## Response

`200` with one result per statement, in order:

```json
{"results":[
  {"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null},
  {"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":1},
  {"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"}],"rows":[[1,"Ada"]],"rows_affected":0,"last_insert_rowid":null}
]}
```

- `rows` holds one array per row, one value per column: null, a number or a string.
- `decltype` is the column's declared type, or null for an expression.
- Integers are JSON numbers. A value beyond ±2^53 can lose precision in JavaScript's `JSON.parse`; read such columns as text (`CAST(x AS TEXT)`) or use a libSQL client.

## Errors

A failed statement rolls back the whole request and answers:

```json
{"error":{"code":"sql_error","message":"a row with this primary key exists","statement":1,"sqlite_code":"SQLITE_CONSTRAINT_PRIMARYKEY"}}
```

`statement` is the zero-based index of the statement that failed. `message` never repeats your data.

| HTTP | `code` | Meaning |
| --- | --- | --- |
| `400` | `sql_error` | Syntax error, unsupported statement, unknown table or column, or a constraint (`SQLITE_CONSTRAINT_PRIMARYKEY`, `_UNIQUE`, `_NOTNULL`) |
| `400` | `invalid_request` | The body is not a valid request, or has no statements or more than 256 |
| `403` | `forbidden_scope` | A write or `CREATE`/`DROP` with a read-only key (`SQLITE_AUTH`) |
| `409` | `sql_conflict` | A concurrent write conflicted (`SQLITE_BUSY`); retry the request |
| `413` | `sql_too_large` / `payload_too_large` | The result passed 10,000 rows or 8 MiB, or the body passed 1 MiB |

`401` means the token is missing, expired or revoked; mint a new one. `429` comes with `Retry-After`. See the [error guide](/ja/guides/errors).

## Limits

The [libSQL limits](/ja/guides/libsql-clients#limits) apply: reads scan their tables (about 150,000 rows at most), a result holds at most 10,000 rows and 8 MiB, and a request body is at most 1 MiB. Page large reads with a key condition and `LIMIT`:

```sql
SELECT id, name FROM customers WHERE id > ? ORDER BY id LIMIT 1000
```

Pass the last `id` of each page as the argument for the next one.

</div>
