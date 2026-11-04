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
stays off). Each invocation gets a fresh engine. The function defines `handler(event)`, usually `async`. Its only I/O is
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
- **Secrets**: v1 has none. Per-function secrets will be stored encrypted with the database key (`secrets` map, write-only)
  and exposed as `event.secrets`; until then do not put credentials in function source.
- **Logs**: `console.log` lines, status, duration and error of each invocation are kept per function and read through
  `GET .../functions/{name}/logs` (CLI `ts functions logs`, dashboard Functions page).

## API (v1)

| Method and path (under `/v1/projects/{project}/databases/{database}`) | Purpose |
| --- | --- |
| `PUT /functions/{name}` `{source, cron?}` | Deploy or replace |
| `GET /functions`, `GET /functions/{name}` | List, read (with source) |
| `DELETE /functions/{name}` | Delete function, schedule and logs |
| `POST /functions/{name}/invoke` | Run with `event = {trigger: "http", body, query}` |
| `GET /functions/{name}/logs` | Invocation logs |

Cron events are
`{trigger: "cron", scheduledAt}`. A handler returns `{status, body}` or any JSON value (status 200).

Function names are lowercase, at most 63 characters. Deleting the database deletes its functions.
