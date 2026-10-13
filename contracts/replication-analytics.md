# TS-762 replication and analytics client contract

Reconciled with TerraScale/terrascale main
`e30ffdc287530551b161b0130220be53d3c0475e`, specifically
`src/TerraBase/Features/Management/Usage/{IUsageAnalytics,AnalyticsRoute,AnalyticsWindow}.cs`.
These are implemented versioned routes. Local fixtures establish SDK decoding;
deployed observations and regional completeness remain unqualified.

## Requests

`GET /v1/projects/{project}/analytics` returns `AnalyticsResponse`.
`GET /v1/projects/{project}/replication` returns `ReplicationResponse`.

Both use `Authorization: Bearer <ts-auth project access token>` and the same
optional query parameters: `range` (`1h`, `24h`, `7d`, `30d`), `database` (plain
ULID) and `region` (observing region identifier).
Project and database IDs in requests and responses are canonical uppercase,
26-character Crockford ULIDs. The SDK rejects UUIDs, prefixes and noncanonical
case without normalizing them. Omitted range selects `24h`; omitted
database/region select all observations the grant reaches in this regional
catalog. There is no `after`, `limit` or `next` contract. Windows use 1-minute,
1-hour, 6-hour and 1-day buckets respectively. Requests are bounded to 100
databases, 16384 database-region buckets and 5 seconds by the server.

## Responses

Both responses contain `projectId`, `range`, UTC `from` (inclusive), `to`
(exclusive), `bucketSeconds`, and `items`.

Analytics items contain `databaseId`, `databaseName`, `region` and `series`.
Each series point contains `start`, `requests`, `operations` (counts keyed by
type), `errors`, `latency` (`p50Ms`, `p95Ms`, `p99Ms`), nullable `storageBytes`,
`clientBandwidth` and `replicationBandwidth`. Both bandwidth objects contain
`bytesSent` and `bytesReceived`.

Each replication item contains `databaseId`, `databaseName`, `region`, period `replicatedOps`,
`bandwidth` (directed region pairs), latest `lagMs`, `divergence` (boolean),
`convergenceMs`, `lastApplied` (UTC timestamp), `series`, and `status`
(`healthy`, `lagging`, `diverged`, `stopped`). Unknown future statuses decode
without error. `divergence` is a nullable boolean, preserving measured false.
Latency, storage, lag, convergence and last-applied observations preserve null
versus measured zero. Replication series points contain `start`,
`replicatedOps` and directed `bandwidth`.

Each region pair contains `sourceRegion`, `destinationRegion`, `bytesSent`,
`bytesReceived`, and `kind` (`delta`, `anti_entropy`, `handoff`). `gru -> sfo` and `sfo -> gru`
are separate pairs. Sent/received observations must not be summed as distinct
billable transfers. Replication bytes remain separate from client bandwidth.
Counters are best-effort observations flushed every 10 seconds, not billing.
All counters decode as signed 64-bit integers without passing through float64.

TerraBase owns metering, replication state, aggregation and pagination. The SDK
reads those values without storing data or computing replacement measurements.
Missing required windows, item arrays and nested observations fail decoding
instead of becoming empty successful data. Unknown response fields remain accepted.
Errors use TerraBase's existing `{"error":{"code":...,"message":...}}`
envelope and the SDK's `APIError`/safe-read retry policy.
