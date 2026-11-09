---
title: SQL over HTTP
description: Run SQL statements against a TerraScale database with a plain HTTP client.
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# SQL over HTTP

Any HTTP client runs SQL against a TerraScale database: one `POST` request runs 1 to 256 statements as one transaction, all or nothing. The same statements run through the [SDKs](/ja/guides/sdks), over WebSocket and over the [Postgres wire](/ja/guides/postgres-clients), with the same results and errors.

## The request

```
POST https://api.terrascale.tech/v1/projects/{project}/databases/{database}/sql/execute
Authorization: Bearer <access token>
Content-Type: application/json
```

Exchange a project API key for the access token first, as in [getting started](/ja/getting-started/#exchange-the-key-for-an-access-token); tokens live at most 60 seconds and the key itself is refused as a bearer token. Get the project ID from the dashboard or `ts projects`.

The body carries `statements`, each with `sql` and either positional `args` or a `named` map:

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

Named parameters work the same way: `{"sql":"SELECT email FROM customers WHERE seats >= :min","named":{"min":10}}`.

## The response

`results` holds one entry per statement, in order: `columns` (name and declared type), `rows`, `rows_affected` and `last_insert_rowid` (null unless the statement inserted into an `INTEGER PRIMARY KEY`). Row values are null, numbers, strings, or blobs as `{"base64":"…"}`. Arguments accept the same shapes; booleans bind as 1 or 0.

A refused statement rolls back the whole request and answers an `error` object:

```json
{"error":{"code":"unique_violation","message":"UNIQUE constraint failed: customers.email","statement":0,"sqlstate":"23505"}}
```

`code` is a TerraScale code and `sqlstate` its PostgreSQL SQLSTATE, identical on every SQL surface; `statement` indexes the failing statement. See [errors and safe retries](/ja/guides/errors) for recovery, and [SQL](/ja/concepts/sql) for the dialect and what SQL supports. Reads need the `sql:read` scope; writes and schema changes also need `sql:write` (`403 forbidden_scope`).

## Transactions

One request is one transaction. `BEGIN`, `COMMIT` and `ROLLBACK` are refused; `SAVEPOINT`, `RELEASE` and `ROLLBACK TO` are accepted within the request. For interactive transactions use the WebSocket session (`…/sql/ws`, subprotocol `terrascale.sql.v1`) or the Postgres wire, both limited to 5 seconds per transaction. See [SQL](/ja/concepts/sql#transactions).

## Read and write routing

Read-only requests run on a ready replica in the serving region; everything else is forwarded whole to the database's home in one hop. The answer carries `TerraScale-Write-Route` (`route=local|home; reason=…; commit=home|local`) and `Server-Timing`. Send `TerraScale-Write: home` for read-your-writes, `local` to prefer the region, or `auto` (the default). Send `TerraScale-Durability: home` to answer only after the home applied the write. Set the body field `durability` to `all` to answer only after every replica region holds the commit (a 10-second wait answers `503 unavailable`). See [multi-region writes and conflicts](/ja/guides/multi-region-writes-and-conflicts).

## Export a database

```
GET https://api.terrascale.tech/v1/projects/{project}/databases/{database}/export
Authorization: Bearer <access token>
```

The response is one SQL script (`application/sql`, `attachment`) holding the whole database: every table's schema and rows, then its indexes, views and triggers, wrapped in one transaction. The dump comes from a single read snapshot, so it is consistent — no half-applied writes appear in it. The same statements run back into a fresh database through `sql/execute` or the SDKs.

Exports need the `sql:read` scope and are served by the database's home region. If an export fails partway, the connection is closed instead of returning a partial file, so retry the whole request.

## Limits

Statements are at most 16 KiB each, the body at most 1 MiB, results at most 10,000 rows and 8 MiB, and at most 1,024 rows changed or 8 MiB of file growth per transaction — past them, `413 too_large` / SQLSTATE `54000`. Rate limits answer `429 tenant_busy` (SQLSTATE `53300`) with `Retry-After`; the plan's monthly write cap and SQL read fair use answer `429 quota_exceeded` (SQLSTATE `53100`). The full tables live on the [limits](/ja/limits) page.

</div>
