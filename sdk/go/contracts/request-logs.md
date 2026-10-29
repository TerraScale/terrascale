# Recent database request observations

`Database.RequestLogs(ctx, status)` calls the already-shipped operation
`listDatabaseLogs`: `GET /v1/projects/{project}/databases/{database}/logs`.
Use a customer project access token with `databases:read`; the project claim
must match, and database-narrowed authority cannot access a different database.
The producer accepts immutable database ULIDs or current names. This SDK Database
client selects the current name and uses its existing validation and authenticated GET transport.

`status` is empty (omitted) or exactly `2xx`, `3xx`, `4xx`, or `5xx`. Invalid
values fail locally before dispatch. There is no request body. HTTP200 JSON
contains required non-null `databaseId`, `items`, `truncated`, and
`recordingLimited`. Each item requires `id`, `time`, `region`, `method`, `route`,
`status`, `latencyMs`, `operations`, and `isQuery`; `requestId` may be absent or
null and is an opaque correlation string. Entity IDs are canonical ULIDs.
Required false/zero values and int64 operation counters are preserved; malformed
responses fail atomically. Route values are producer-redacted templates.

This is the newest100 recorded observations within24 hours, in newest-first
order. Recording is best effort: pending records may be lost on crash and
remote observations arrive through replication. `truncated` indicates a full
recent window, not a cursor. `recordingLimited` reports capacity omissions on
the answering server since startup, not global durable loss. A query with
latencyMs >=100 is a slow query. There is no cursor, export checkpoint or
promise that a just-completed request appears immediately. Do not poll this API
as a lossless log drain.

Documented failures include400 validation,404 missing/inaccessible database and
503 unavailable. The existing transport preserves APIError status/body/headers,
uses fixed response/deadline bounds and only its existing safe-GET transient
retry policy. Caller cancellation remains effective; no extra retry or worker
is introduced. The producer sets Cache-Control:no-store. This contract does not
implement the broader TS1155 durable-log scope or resolve generated OpenAPI's
missing status-query metadata.

Owning TerraBase source: `49acbd764902660dfd4c8e44dd5933bacd99c7aa` (landed source, not a deployed-SHA claim).
Method, route, operation name, permission, response requiredness/nullability and
filter grammar were compared directly with these immutable sources. This
feature-local pin does not claim full generated-manifest parity (TS1274).

| Source | SHA256 |
|---|---|
| `src/TerraBase/Features/Management/Logs/ListDatabaseLogsEndpoint.cs` | `ea20148e9904f29e818394f88a09dc72e97befdd193d5197677c2d6b129220b9` |
| `src/TerraBase/Features/Management/Logs/IRequestLogs.cs` | `5f88f1c3b15edb18530cfa804bda2f91f733be19b97bd00f098b86699ce15d57` |
| `src/TerraBase/Features/Management/Logs/RequestLogRecorder.cs` | `d049bfde08eef5034df675413de71ed38ed59dea711d7aa6ddb63819ae153dda` |
| `src/TerraBase/Features/Management/ProjectApiRoute.cs` | `307c08a1fcd0a633638b1e5d971ae1dceea45a9f8f04a882ee8bcd52ed75ac6f` |
