# TerraScale Functions: design (v1)

Customers deploy small JavaScript functions next to a database. A function runs on an HTTPS call, on changes to a
collection, or on a cron schedule, and can write documents to its own database.

## Where functions run

Functions are a TerraBase feature (`src/TerraBase/Features/Functions`). TerraBase already runs one node per region on the
ts-orch hosts (`gru`, `sjc`, `fra`), owns the public `api.` host, TLS through the ts-orch edge, project API keys,
per-database storage and usage metering. Running functions there adds no new service, host, DNS name or paid capacity.

- **HTTPS**: `POST https://api.terrascale.tech/v1/projects/{project}/databases/{database}/functions/{name}/invoke`, served by
  whichever region the edge picks. A `<project>.fn.terrascale.tech` host is a later edge change (ts-orch), not v1.
- **Change trigger**: deploying with `trigger.collection` registers a change webhook for that collection whose receiver is
  the function itself (`terrabase-function:` URL, delivered in process). It reuses the webhook worker: durable cursor,
  at-least-once delivery, exponential retries and delivery history (the dead-letter view). The receiver accepts a delivery
  only when its project and database equal the ones the URL names, so no tenant can aim a webhook at another's function.
- **Cron**: deploying with `cron` (five-field expression, UTC, parsed with Cronos) schedules the function on the node that
  deployed it, like webhook dispatch. A run missed while that node is down is skipped, not replayed.

## Isolation and runtime

v1 runs **JavaScript** (ES2023, one source file) in [Jint](https://github.com/sebastienros/jint), a pure managed
interpreter: no native dependency, single-file compatible, and no CLR, file, network or environment access (CLR interop
stays off). Each invocation gets a fresh engine. The function defines `handler(event)`. It cannot do I/O; it returns a
response and records effects through `db.insert(collection, doc)`, `db.put(collection, id, doc)` and
`db.delete(collection, id)`. TerraBase applies the effects atomically after a successful return, with the function's
database as the only scope. A change-triggered run may not write its own trigger collection (no loops).

Wasm (wazero worker in ts-orchestrator `internal/functions`, or Wasmtime) is the follow-up for compiled languages and
for moving execution onto dedicated function hosts; v1 shares the TerraBase process, bounded by the limits below.

## Limits (fixed in code)

| Bound | Value |
| --- | --- |
| Source | 256 KiB, one file |
| Functions | 32 per database |
| Wall time | 2 s per invocation; 10,000,000 statements; recursion depth 256 |
| Memory | 32 MiB allocated per invocation (Jint memory limit) |
| Input / output | 1 MiB request body; 1 MiB response |
| Effects | 25 document writes per invocation, 256 KiB per document |
| Logs | 50 lines of 1 KiB per invocation; last 100 invocations kept per function |
| Cron | at most once per minute |

## Auth, billing, secrets and logs

- **Auth**: deploy, list, logs and delete need a project API key with `documents:read` and `documents:write`; invoke
  needs `documents:read`. Effects run with the function's authority, like a backend endpoint.
- **Billing**: every invocation counts one `invoke` operation in the database's usage, next to reads and writes, and its
  effects count as normal document writes. Pricing treats invocations as operations.
- **Secrets**: v1 has none. Per-function secrets will be stored encrypted with the database key (`secrets` map, write-only)
  and exposed as `event.secrets`; until then do not put credentials in function source.
- **Logs**: `console.log` lines, status, duration and error of each invocation are kept per function and read through
  `GET .../functions/{name}/logs` (CLI `ts functions logs`, dashboard Functions page). Change-trigger delivery state is
  `GET .../functions/{name}/deliveries`.

## API (v1)

| Method and path (under `/v1/projects/{project}/databases/{database}`) | Purpose |
| --- | --- |
| `PUT /functions/{name}` `{source, trigger?: {collection, events?}, cron?}` | Deploy or replace |
| `GET /functions`, `GET /functions/{name}` | List, read (with source) |
| `DELETE /functions/{name}` | Delete function, trigger, schedule and logs |
| `POST /functions/{name}/invoke` | Run with `event = {trigger: "http", body, query}` |
| `GET /functions/{name}/logs`, `GET /functions/{name}/deliveries` | Invocation logs; trigger deliveries |

`events` filters a trigger to `upsert` (insert or update) and/or `delete`; v1 change identities do not separate inserts
from updates. Change events are `{trigger: "change", collection, changes: [{id, deleted, document}]}`; cron events are
`{trigger: "cron", scheduledAt}`. A handler returns `{status, body}` or any JSON value (status 200).

Function names are lowercase, at most 63 characters. Deleting the database deletes its functions.
