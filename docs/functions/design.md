# TerraScale Functions: design (v1)

Customers deploy small JavaScript functions next to a database. A function runs on an HTTPS call or on a cron schedule,
and reads and writes its own database with SQL.

## Where functions run

Functions are a TerraBase feature (`src/TerraBase/Features/Functions`). TerraBase already runs one node per region on the
ts-orch hosts (`gru`, `sjc`, `fra`), owns the public `api.` host, TLS through the ts-orch edge, project API keys,
per-database storage and usage metering. Running functions there adds no new service, host, DNS name or paid capacity.

- **HTTPS**: `POST https://api.terrascale.tech/v1/projects/{project}/databases/{database}/functions/{name}/invoke`, served by
  whichever region the edge picks. A `<project>.fn.terrascale.tech` host is a later edge change (ts-orch), not v1.
- **Cron**: deploying with `cron` (five-field expression, UTC, parsed with Cronos) schedules the function on the node that
  deployed it. A run missed while that node is down is skipped, not replayed.

## Isolation and runtime

v1 runs **JavaScript** (ES2023, one source file) in [Jint](https://github.com/sebastienros/jint), a pure managed
interpreter: no native dependency, single-file compatible, and no CLR, file, network or environment access (CLR interop
stays off). JavaScript runs in strict mode; assigning an undeclared variable throws. SQL uses the SQLite dialect. Each invocation gets a fresh engine. The function defines `handler(event)`, usually `async`. Its only I/O is
the `sql` binding on its own database, which runs through the same SQL session as `POST .../sql/execute`: the same
dialect, SQL sandbox, per-tenant limits, metering and errors.

| Call | Answers |
| --- | --- |
| `await sql.execute(text, args?)` | `{columns, rows, rowsAffected, lastInsertRowid}`; rows are arrays |
| `await sql.query(text, args?)` | Rows as objects keyed by column name |
| `await sql.batch([{sql, args?, named?}, ...])` | One result per statement, all in one transaction |
| `await sql.transaction(async tx => ...)` | Runs `tx.execute`, `tx.query` and `tx.batch` in one transaction; commits when the callback returns, rolls back when it throws |

`args` is an array of positional values or an object of named values (`:name`, `@name` or `$name` in the text). Values
are null, numbers, booleans (1 or 0), strings or `{base64}` blobs; blobs come back the same way. A call outside
`sql.transaction` commits on its own. A refused statement throws an `SqlError` with `code`, `message`, `statement` and
`sqlstate`, the REST error body's fields (for example `sql_sandbox`, `42501` for `ATTACH`).

Wasm (wazero worker in ts-orchestrator `internal/functions`, or Wasmtime) is the follow-up for compiled languages and
for moving execution onto dedicated function hosts; v1 shares the TerraBase process, bounded by the limits below.

## Limits (fixed in code)

| Bound | Value |
| --- | --- |
| Source | 256 KiB, one file |
| Functions | 32 per database |
| Wall time | 10 s per invocation, SQL included (each SQL call also has the 5 s SQL request deadline); 10,000,000 statements; recursion depth 256 |
| Memory | 32 MiB allocated per invocation (Jint memory limit) |
| Input / output | 1 MiB request body; 1 MiB response |
| SQL | 256 `sql` calls per invocation; each statement has the REST statement, row and transaction limits |
| Logs | 50 lines of 1 KiB per invocation; last 100 invocations kept per function |
| Cron | at most once per minute |

## Auth, billing, secrets and logs

- **Auth**: deploy, list, logs and delete need a project API key with `sql:read` and `sql:write`; invoke
  needs `sql:read`. SQL runs with the function's authority (reads and writes), like a backend endpoint.
- **Billing**: every invocation counts one `invoke` operation in the database's usage, next to reads and writes, and its SQL
  counts as normal SQL reads and writes. Pricing treats invocations as operations.
- **Secrets**: each function has a write-only `secrets` map exposed to its handler as `event.secrets` (see Secrets below).
  Do not put credentials in function source.
- **Logs**: `console.log` lines, status, duration and error of each invocation are kept per function and read through
  `GET .../functions/{name}/logs` (CLI `ts functions logs`, dashboard Functions page).

## API (v1)

| Method and path (under `/v1/projects/{project}/databases/{database}`) | Purpose |
| --- | --- |
| `PUT /functions/{name}` `{source, cron?}` | Deploy or replace |
| `GET /functions`, `GET /functions/{name}` | List, read (with source) |
| `DELETE /functions/{name}` | Delete function, schedule and logs |
| `POST /functions/{name}/invoke` | Run with `event = {trigger: "http", body, query}` |
| `GET /functions/{name}/stats?window=24h` | Invocation totals and latency percentiles |
| `GET /functions/{name}/logs` | Invocation logs |
| `GET /functions/{name}/secrets` | Secret names and `updatedAt`, never values |
| `PUT /functions/{name}/secrets/{secret}` `{value}` | Set or replace one secret |
| `DELETE /functions/{name}/secrets/{secret}` | Delete one secret |

Cron events are
`{trigger: "cron", scheduledAt}`. Any returned object with an integer `status` from 200 to 599 is treated as the response wrapper: only `status` and `body` are used, and other properties are discarded. For example, `return {status: 200, items: rows}` produces an empty response. Preserve a data object's own `status` field by returning `{status: 200, body: {id: 1, status: 404}}`. Values without a top-level `status` property use HTTP 200; `undefined` becomes JSON null. Treat top-level `status` as reserved and wrap data containing that field inside `body`.

RPC invoke and MCP `invoke_function` accept an optional `query` map of string keys and string values.
The dashboard test run accepts a URL query string in Query parameters. All three invoke paths forward to the same
HTTPS route, which decodes query keys and values and joins repeated values with commas in `event.query`.
Omitting query parameters produces an empty `event.query` object.

Function names are lowercase, at most 63 characters. Deleting the database deletes its functions.

## Secrets

A function's secrets are a write-only map of name to string. Names match `^[A-Z_][A-Z0-9_]{0,63}$`, values are at most
4 KiB of UTF-8 and a function holds at most 50; breaking a rule returns 400 `invalid_request` or 409 `function_secret_limit`.
Set, list and delete need the same authority as deploy (`sql:read` and `sql:write`). Responses carry only `name` and
`updatedAt`; no REST, RPC, dashboard or MCP response, log, stat or error contains a value.

Each value is sealed before it reaches the system catalog, the same way backup destination credentials are: a fresh
256-bit data key per value encrypts it with AES-256-GCM, and the master keyring (`TERRABASE_MASTER_KEY` or
`TERRABASE_MASTER_KEYS_FILE`) wraps that data key. The record stores the wrapped key and the ciphertext. The
associated data binds the database scope, function name and secret name, so a sealed value copied to another secret,
function or database fails to open. When the active master key changes, the key rewrap pass rewraps these data keys
with the credential records. The local key inventory counts their wrappers, in current records and retained change
images, so a master key still wrapping a secret is not reported unused, startup refuses a missing key id, and a write
whose wrapper does not unwrap fails. Deleting a function deletes its secret records; deleting the database deletes every function's.

Every invocation (HTTP, cron, dashboard, RPC and MCP invoke all run through the same path) reads the function and its
secrets in one catalog read and opens them, so a change applies from the next invocation without a redeploy. A
regional writer whose catalog has not yet received a change uses the previous value until it does. The handler sees
`event.secrets` as a frozen, non-enumerable object: `JSON.stringify(event)` and `console.log(event)` omit it, and a
missing name is `undefined`. Anything the handler itself logs or returns is its own output.

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

Metrics survive the last-100 log limit. Like invocation logs, they cover the answering regional writer;
this endpoint does not combine metrics from different regions. Each function has at most 228 aggregate records: a 60-slot minute ring
and a 168-slot hour ring, each record holding fixed counters and a 64-bin power-of-two millisecond histogram.
That bounds histogram storage to 14,592 counters per function, plus 228 fixed counter sets and record metadata.
Percentiles are nearest-rank histogram upper bounds, capped at the observed maximum; they are estimates rather than
exact samples. Minute data expires outside the current 60-minute bucket window and hour data outside the current
168-hour bucket window. Expired slots are excluded by their timestamp and overwritten on reuse, so idle functions
retain bounded expired records without exposing them in stats. Log and aggregate updates share one catalog
transaction and add no customer-database SQL query. Deleting a function or its database deletes its aggregates.

## Dashboard MCP

Call `get_functions_reference` first for the complete runtime contract and examples. Both signed-in and project API-key MCP connections expose `deploy_function`, `invoke_function`, `list_functions`, `get_function`, `list_function_logs`, `get_function_stats`, `set_function_secret`, `delete_function_secret` and `list_function_secrets`. Deploy returns structured runner validation errors. Invoke requires only `sql:read` and returns status, content type and body; it includes the newest log only when the caller also has SQL write access. Otherwise `log: null` and `log_note` describe that requirement. Concurrent invocations or cron may make that newest log belong to another run.

MCP `invoke_function` accepts an optional `query` object mapping string keys to string values. The dashboard test run accepts a URL query string in the Query parameters field. Both populate `event.query` through the HTTPS invoke route, which decodes keys and values and joins repeated values with commas. Omitting query parameters produces an empty `event.query` object.

`delete_function` returns a dashboard confirmation URL and leaves the function intact.

A completed MCP invocation retains its status and body if the subsequent log read fails; `log` is null and `log_note` identifies the log retrieval failure. Read existing logs instead of invoking the function again.

`get_function_stats` reads aggregate invocation, error, latency and SQL metrics through the same authority as logs. Its `window` is `1h`, `24h` (default) or `7d`; invalid windows return a structured validation error. Results use the stats contract, including oldest-first, zero-filled buckets.
