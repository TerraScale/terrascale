# Recent database request observations

Producer pin: `49acbd764902660dfd4c8e44dd5933bacd99c7aa`. This read contract already exists; the SDK does not introduce a route or durable export protocol.

`TerraBaseDatabase.ListRequestLogsAsync(status, cancellationToken)` sends one GET to
`/v1/projects/{project}/databases/{database}/logs`, operation `listDatabaseLogs`.
A project Bearer token needs `databases:read`; project and optional database scope
are enforced by the producer. The producer accepts immutable database ULIDs or current names; this SDK Database client selects the current name.
The optional `status` query is empty, `2xx`, `3xx`, `4xx` or `5xx`; null omits it.
Invalid values are refused locally. There is no request body, cursor or page-size parameter.

Success is HTTP200 `application/json`, with `Cache-Control: no-store`:
`{databaseId, items, truncated, recordingLimited}`. All four fields are required;
`databaseId` is a canonical ULID and `items` is a nonnull array, including when empty.
Each item requires `id` (ULID), `time` (RFC3339), `region`, `method`, `route`,
`status` (HTTP integer), `latencyMs` (nonnegative finite number), `operations`
(nonnegative int64), and `isQuery` (boolean). `requestId` is an optional nullable
correlation string, preserved without treating it as an entity ULID. Missing
required fields, null items and invalid entity IDs become `invalid_response`.
Unknown fields are tolerated. Error responses retain normal typed SDK exceptions:
400 invalid filter, 401 invalid authority, 403 missing scope, 404 unknown or
out-of-scope project/database, 503 unavailable storage/admission. There is no partial-success response.

The producer returns at most100 recent observations within24hours, newest first.
Filtering applies within that already bounded window. `truncated` means the
recent window was full, even if a filter returns fewer items. `recordingLimited`
means this server omitted observations at capacity since startup. A route is a
redacted template, not request text or a URL. Operations count committed document
operations. SQL/query observations with latency at least100ms are slow queries.
Recording is best effort; crashes or late remote observations can cause loss.
The result provides no durable ordering, completeness, checkpoint or retry promise.

The SDK uses the existing connection deadline, response bound, response disposal
and cancellation. It does not poll, cache, retry or dispose the caller's HttpClient.
The new feature has its own source-generated JSON context with required
constructor and nullable-member enforcement. TUnit fixtures use HTTP fakes only;
no TerraBase binaries are referenced. Source pinning is not live qualification.
Broad generated manifest/export parity remains separately tracked under TS1274;
these exact handler, DTO and route constants establish this operation's contract.

| Producer source | SHA256 |
|---|---|
| `src/TerraBase/Features/Management/Logs/ListDatabaseLogsEndpoint.cs` | `ea20148e9904f29e818394f88a09dc72e97befdd193d5197677c2d6b129220b9` |
| `src/TerraBase/Features/Management/Logs/IRequestLogs.cs` | `5f88f1c3b15edb18530cfa804bda2f91f733be19b97bd00f098b86699ce15d57` |
| `src/TerraBase/Features/Management/Logs/RequestLogRecorder.cs` | `d049bfde08eef5034df675413de71ed38ed59dea711d7aa6ddb63819ae153dda` |
| `src/TerraBase/Features/Management/ProjectApiRoute.cs` | `307c08a1fcd0a633638b1e5d971ae1dceea45a9f8f04a882ee8bcd52ed75ac6f` |
