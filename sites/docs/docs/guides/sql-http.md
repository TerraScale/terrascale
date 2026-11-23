---
title: SQL over HTTP
description: Run SQL statements against a TerraScale database with a plain HTTP client.
---

# SQL over HTTP

Any HTTP client runs SQL against a TerraScale database: one `POST` request runs 1 to 256 statements as one transaction, all or nothing. The same statements run through the [SDKs](/guides/sdks), over WebSocket and over the [Postgres wire](/guides/postgres-clients), with the same results and errors.

## The request

```
POST https://api.terrascale.tech/v1/projects/{project}/databases/{database}/sql/execute
Authorization: Bearer <access token>
Content-Type: application/json
```

Exchange a project API key for the access token first, as in [getting started](/getting-started/#exchange-the-key-for-an-access-token); tokens live at most 60 seconds and the key itself is refused as a bearer token. Get the project ID from the dashboard or `ts projects`.

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

`code` is a TerraScale code and `sqlstate` its PostgreSQL SQLSTATE, identical on every SQL surface; `statement` indexes the failing statement. See [errors and safe retries](/guides/errors) for recovery, and [SQL](/concepts/sql) for the dialect and what SQL supports. Reads need the `sql:read` scope; writes and schema changes also need `sql:write` (`403 forbidden_scope`).

## Transactions

One request is one transaction. `SAVEPOINT`, `RELEASE` and `ROLLBACK TO` are accepted within the request. `BEGIN STRONG` (or `BEGIN ISOLATION LEVEL SERIALIZABLE`) as the first statement and `COMMIT` as the last run the whole batch in strong mode; any other `BEGIN`, `COMMIT` or `ROLLBACK` is refused. For interactive transactions use the WebSocket session (`…/sql/ws`, subprotocol `terrascale.sql.v2`) or the Postgres wire, both limited to 5 seconds per transaction. See [SQL](/concepts/sql#transactions).

## Consistency and bookmarks

Every region of the database's set runs the request where you called it; there is no home region and no forwarding. Two optional headers control how it runs:

| Header | Values | Effect |
| --- | --- | --- |
| `TerraScale-Consistency` | `fast` (default) or `strong` | `fast` commits in the region you called and replicates afterwards. `strong` commits on two of three regions after checking every row the transaction read and wrote. |
| `TerraScale-Bookmark` | A bookmark from an earlier response | The region waits until it has applied the writes the bookmark covers, then runs the request: read-your-writes in any region. |

Every response carries `TerraScale-Bookmark` (the newest bookmark covering this request) and `TerraScale-Consistency` (the mode it actually ran in; schema changes and writes to tables with a secondary `UNIQUE` constraint always run `strong`). Keep the newest bookmark and send it on the next request:

```bash
curl -sS --fail-with-body -D headers.txt --header @/dev/fd/3 \
  -H 'Content-Type: application/json' -H 'TerraScale-Consistency: strong' \
  -d '{"statements":[{"sql":"UPDATE accounts SET balance = balance - ? WHERE id = ?","args":[25,"acct_1"]}]}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/sql/execute" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
BOOKMARK=$(awk -F': ' 'tolower($1)=="terrascale-bookmark"{print $2}' headers.txt | tr -d '\r')

curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' -H "TerraScale-Bookmark: $BOOKMARK" \
  -d '{"statements":[{"sql":"SELECT balance FROM accounts WHERE id = ?","args":["acct_1"]}]}' \
  "https://sjc.api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/sql/execute" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

A strong request that loses a race answers `409 serialization_conflict` (SQLSTATE `40001`) and applied nothing; send it again. A malformed bookmark answers `400 invalid_bookmark`. See [replication and consistency](/concepts/replication) and [multi-region writes and conflicts](/guides/multi-region-writes-and-conflicts).

## Export a database

```
GET https://api.terrascale.tech/v1/projects/{project}/databases/{database}/export
Authorization: Bearer <access token>
```

The response is one SQL script (`application/sql`, `attachment`) holding the whole database: every table's schema and rows, then its indexes, views and triggers, wrapped in one transaction. The dump comes from a single read snapshot, so it is consistent — no half-applied writes appear in it. The same statements run back into a fresh database through `sql/execute` or the SDKs.

Exports need the `sql:read` scope and are served by the region you call, from the data that region has applied. If an export fails partway, the connection is closed instead of returning a partial file, so retry the whole request.

## Limits

Statements are at most 16 KiB each, the body at most 1 MiB, results at most 10,000 rows and 8 MiB, and at most 1,024 rows changed or 8 MiB of file growth per transaction — past them, `413 too_large` / SQLSTATE `54000`. Rate limits answer `429 tenant_busy` (SQLSTATE `53300`) with `Retry-After`; the plan's monthly write cap and SQL read fair use answer `429 quota_exceeded` (SQLSTATE `53100`). The full tables live on the [limits](/limits) page.
