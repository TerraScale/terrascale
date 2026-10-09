# TS-762 replication and analytics client contract

This is the provisional wire shape used by the SDK's `httptest` tests, pending
TerraBase's `cos/ts-762-replication-usage-analytics-api` OpenAPI. Reconcile paths,
query keys, pagination and JSON fields with that OpenAPI before merging the SDK
PR. The TerraBase PR must merge first. These routes have no version component.

## Requests

`GET /projects/{project}/analytics` returns `AnalyticsResponse`.
`GET /projects/{project}/replication` returns `ReplicationResponse`.

Both use `Authorization: Bearer <ts-auth project access token>` and the same
optional query parameters: `range` (`1h`, `24h`, `7d`, `30d`), `database` (plain
ULID), `region` (region identifier), `after` (opaque cursor) and `limit` (1-100).
The project path parameter is a plain ULID. ULIDs accept upper, lower and mixed
ASCII case; the SDK preserves the supplied encoding. Omitted range/limit use server
defaults; omitted database/region select all. `next` is empty at the last page.
Pass it back as `after`, retaining the other filters. The SDK fetches one page
per call and delegates bucketing to TerraBase.

## Responses

Both responses contain `projectId`, UTC `from` (inclusive), `to` (exclusive),
`bucketSeconds`, and `next`. Analytics has `series`; replication has `items`.

Each analytics point contains `start`, `databaseId`, `region`, `requests` and
`ops` (counts keyed by type), `errors`, `p50LatencyMs`, `p95LatencyMs`,
`p99LatencyMs`, `storageBytes`, `clientBytesSent`, `clientBytesReceived`, and
`replicationBandwidth` (directed region pairs).

Each replication item contains `databaseId`, `region`, period `replicatedOps`,
`bandwidth` (directed region pairs), latest `lagMs`, `divergence` (count),
`convergenceTimeMs`, `lastApplied` (UTC timestamp) and `status`
(`healthy`, `lagging`, `diverged`, `stopped`). Unknown future statuses decode
without error. Unavailable latency, lag, divergence, convergence and last-applied
observations are nullable, preserving measured zero.

Each region pair contains `sourceRegion`, `destinationRegion`, `bytesSent`,
`bytesReceived`, and `meter` (`replication_bytes`). `gru -> sfo` and `sfo -> gru`
are separate pairs. Sent/received observations must not be summed as distinct
billable transfers. Replication bytes remain separate from client bandwidth.
All counters decode as signed 64-bit integers without passing through float64.

TerraBase owns metering, replication state, aggregation and pagination. The SDK
reads those values without storing data or computing replacement measurements.
Errors use TerraBase's existing `{"error":{"code":...,"message":...}}`
envelope and the SDK's `APIError`/safe-read retry policy.
