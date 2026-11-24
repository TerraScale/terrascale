---
title: Functions
---

# Functions

A function is one JavaScript file deployed next to a database. It runs on an HTTPS call or a cron schedule and reads and writes that database with SQL.

## Write a function

Define `handler(event)`. The `sql` binding runs statements on the function's own database:

```js
async function handler(event) {
  await sql.execute("CREATE TABLE IF NOT EXISTS visits (id INTEGER PRIMARY KEY, page TEXT NOT NULL)");
  await sql.execute("INSERT INTO visits (page) VALUES (?)", [event.body.page]);
  const rows = await sql.query("SELECT page, count(*) AS visits FROM visits GROUP BY page ORDER BY visits DESC LIMIT 5");
  return { status: 200, body: rows };
}
```

| Call | Answers |
| --- | --- |
| `sql.execute(text, args?)` | `{columns, rows, rowsAffected, lastInsertRowid}`; rows are arrays |
| `sql.query(text, args?)` | Rows as objects keyed by column name |
| `sql.batch([{sql, args?, named?}, ...])` | One result per statement, all in one transaction |
| `sql.transaction(async tx => ...)` | `tx.execute`, `tx.query` and `tx.batch` in one transaction; commits when the callback returns, rolls back when it throws |

`args` is an array of positional values (`?`) or an object of named values (`:name`). Every call returns a promise. A call outside `sql.transaction` commits on its own.

```js
async function handler(event) {
  return sql.transaction(async tx => {
    await tx.execute("UPDATE accounts SET balance = balance - :amount WHERE id = :from", event.body);
    await tx.execute("UPDATE accounts SET balance = balance + :amount WHERE id = :to", event.body);
    return { status: 204 };
  });
}
```

Statements use the same SQL as the [SQL API](/concepts/sql), with the same sandbox and limits. A refused statement throws an error with the REST error fields:

```js
try {
  await sql.execute("INSERT INTO visits (id, page) VALUES (1, 'home')");
} catch (error) {
  // error.code "primary_key_violation", error.sqlstate "23505", error.statement 0, error.message
}
```

The handler returns `{status, body}`, or any JSON value for status 200. `event` is `{trigger: "http", body, query}` for HTTPS calls and `{trigger: "cron", scheduledAt}` for cron runs. `console.log` lines go to the function's logs.

## Deploy and call

```bash
jq -n --rawfile source visits.js '{source: $source}' | curl -sS --fail-with-body --header @/dev/fd/3 \
  -X PUT -H 'Content-Type: application/json' --data-binary @- \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/functions/visits" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")

curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' -d '{"page":"home"}' \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/app/functions/visits/invoke" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

Add `"cron": "*/5 * * * *"` (UTC) to the deploy body to run on a schedule. Deploying, reading logs and deleting need a key with read and write access; invoking needs read access. Every function route, including invoke, needs a project API key or developer token: end-user and guest tokens answer `403 developer_credential_required`, so call a function from your own backend rather than from a signed-in browser. The function's SQL runs with the function's own authority.

| API (under `/v1/projects/{project}/databases/{database}`) | Purpose |
| --- | --- |
| `PUT /functions/{name}` `{source, cron?}` | Deploy or replace |
| `GET /functions`, `GET /functions/{name}` | List, read with source |
| `POST /functions/{name}/invoke` | Run |
| `GET /functions/{name}/stats?window=24h` | Invocation totals and latency percentiles |
| `GET /functions/{name}/logs` | Last 100 invocations |
| `DELETE /functions/{name}` | Delete with schedule and logs |

## Secrets

Keep credentials out of source. Set them as secrets on the function, from the Secrets panel on its dashboard page,
the MCP `set_function_secret` tool or the API, and read them from `event.secrets`:

```sh
curl -X PUT "https://api.terrascale.tech/v1/projects/$PROJECT/databases/$DATABASE/functions/notify/secrets/API_TOKEN" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"value\": \"$API_TOKEN\"}"
```

```js
async function handler(event) {
  const token = event.secrets.API_TOKEN;
  if (token === undefined) throw new Error("set API_TOKEN first");
  return { configured: true };
}
```

Values are write-only: listing (`GET .../functions/notify/secrets`) returns names and update times, never values. A
change applies from the next invocation without redeploying, and deleting the function deletes its secrets. Values are
encrypted at rest under TerraScale's master keyring. `event.secrets` is frozen and left out of `JSON.stringify(event)`; don't log
or return a value yourself.

## Limits

| Bound | Value |
| --- | --- |
| Source | 256 KiB |
| Functions | 32 per database |
| Run time | 10 s including SQL, memory 32 MiB |
| Request and response | 1 MiB each |
| SQL | 256 `sql` calls per invocation |
| Logs | 50 lines of 1 KiB per invocation |
| Secrets | 50 per function; names `[A-Z_][A-Z0-9_]{0,63}`, values 4 KiB |

## Invocation metrics

Every HTTP and cron invocation records wall-clock `durationMs`, `sqlCalls`, `sqlMs`, `rowsRead`, `rowsWritten`,
`responseBytes` and `httpStatus` alongside its status, console lines and error in `GET .../functions/{name}/logs`.
REST fields use camelCase, as the other functions responses do. SQL calls count binding calls, including transaction
controls and refused calls. SQL time excludes waiting behind another binding call. Durations round up to milliseconds.
Rows read count the rows returned by successful SQL binding results, including RETURNING rows; rows written sum
their affected-row counts. These are result counts, not engine scan counts or committed-row counts: rolling back
an explicit transaction does not subtract earlier successful results. Response bytes count the UTF-8 HTTP body; HTTP status is null for cron runs and execution errors.

`GET .../functions/{name}/stats?window=1h|24h|7d` defaults to `24h`; invalid windows return HTTP 400 in the standard
error envelope. It requires the same project authority and `sql:read` plus `sql:write` scopes as logs. The RPC
`IFunctionsService.StatsAsync` exposes the same response.

The response contains `window`, `invocations`, `errors`, `p50Ms`, `p95Ms`, `p99Ms`, `maxMs`, `sqlCalls`,
`rowsRead`, `rowsWritten` and `buckets`. Each bucket contains `at`, `invocations`, `errors`, `p50Ms` and `p95Ms`.
Errors count execution failures; a handler returning an HTTP error status still completed successfully.
Buckets use UTC, oldest first, including zero-filled empty intervals and the current incomplete interval.
The `1h` window contains 60 minute buckets; `24h` and `7d` contain 24 and 168 hour buckets.

Metrics survive the last-100 log limit. Like invocation logs, they cover the region that answered;
this endpoint does not combine metrics from different regions. Each function has at most 228 aggregate records: a 60-slot minute ring
and a 168-slot hour ring, each record holding fixed counters and a 64-bin power-of-two millisecond histogram.
That bounds histogram storage to 14,592 counters per function, plus 228 fixed counter sets and record metadata.
Percentiles are nearest-rank histogram upper bounds, capped at the observed maximum; they are estimates rather than
exact samples. Minute data expires outside the current 60-minute bucket window and hour data outside the current
168-hour bucket window. Expired slots are excluded by their timestamp and overwritten on reuse, so idle functions
retain bounded expired records without exposing them in stats. Log and aggregate updates share one catalog
transaction and add no customer-database SQL query. Deleting a function or its database deletes its aggregates.
