# TS-762 analytics client contract

Reconciled with TerraScale/terrascale main
`e30ffdc287530551b161b0130220be53d3c0475e`, specifically
`src/TerraBase/Features/Management/Usage/{IUsageAnalytics,AnalyticsRoute,AnalyticsWindow}.cs`.
These are implemented versioned routes. Local fixtures establish SDK decoding;
deployed observations and regional completeness remain unqualified.

## Requests

`GET /v1/projects/{project}/analytics` returns `AnalyticsResponse`.
It uses `Authorization: Bearer <ts-auth project access token>` and the same
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

The response contains `projectId`, `range`, UTC `from` (inclusive), `to`
(exclusive), `bucketSeconds`, and `items`.

Analytics items contain `databaseId`, `databaseName`, `region` and `series`.
Each series point contains `start`, `requests`, `operations` (counts keyed by
type), `errors`, `latency` (`p50Ms`, `p95Ms`, `p99Ms`), nullable `storageBytes`,
and `clientBandwidth` with `bytesSent` and `bytesReceived`.

Counters are best-effort observations flushed every 10 seconds, not billing.
All counters decode as signed 64-bit integers without passing through float64.

TerraBase owns metering, aggregation and pagination. The SDK
reads those values without storing data or computing replacement measurements.
Missing required windows, item arrays and nested observations fail decoding
instead of becoming empty successful data. Unknown response fields remain accepted.
Errors use TerraBase's existing `{"error":{"code":...,"message":...}}`
envelope and the SDK's `APIError`/safe-read retry policy.
