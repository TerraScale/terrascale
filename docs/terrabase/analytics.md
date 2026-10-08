# Project replication and analytics (TS-762)

TerraBase owns these observations. ts-go reads the public project API with a
ts-auth project JWT; ts-dashboard reads them through ts-go. The four routes have
no version component:

| Method and path | operationId |
| --- | --- |
| `GET /projects/{project}/analytics` | `getProjectAnalytics` |
| `GET /projects/{project}/databases/{database}/analytics` | `getDatabaseAnalytics` |
| `GET /projects/{project}/replication` | `getProjectReplication` |
| `GET /projects/{project}/databases/{database}/replication` | `getDatabaseReplication` |

All require `databases:read`, the signed `project_id`, and an existing verified
project binding. Database path selectors are names, as on the existing project
API. The project route's optional `database` filter is a catalog entity ID.
Signed database narrowing applies to both forms; another project or database
returns 404. API keys cannot read these routes. Responses use
`Cache-Control: no-store`. OpenAPI documents the endpoints through Scalar.

## Ranges and regional reads

| `range` | UTC bucket | Buckets |
| --- | --- | --- |
| `1h` | minute | 60 |
| `24h` (default) | hour | 24 |
| `7d` | six hours | 28 |
| `30d` | day | 30 |

`from` and `to` in the response are the aligned bucket boundaries; `to` is
exclusive. The current bucket is partial. Every bucket appears, oldest first.
The optional `region` filter selects the observing region. Each regional
catalog reports its own nodes' observations, including both directions of
replication to its peers. A placement region without observations in that
catalog is omitted. ts-go selects and pins regional client endpoints through
Atlas to read those regions; regions retain independent catalogs and do not
connect to each other's private peer listeners.

Requests read at most 100 databases and 16,384 database-region buckets within
five seconds, with memory admission. Invalid filters/ranges and excessive
queries return 400; an expired read deadline returns 503 `capacity`. Cancellation
releases request admission.

## Analytics response

`ProjectAnalytics` contains `projectId`, `range`, `from`, `to`, `bucketSeconds`
and `items`. Each item has `databaseId`, `databaseName`, `region` and `series`.
Each point contains:

- `start`, request counts by HTTP method, operation counts (`read`, `put`,
  `patch`, `delete`), and errors (HTTP status 400 or above, or an unhandled error).
- `latency`: approximate `p50Ms`, `p95Ms`, `p99Ms` from merged fixed histogram
  bins. Empty request buckets return null percentiles. The highest bin includes
  requests above 65,536 ms and reports that boundary.
- `storageBytes`: the average sampled physical database size, including the
  engine's sidecars; null before any sample. Each node samples every ten seconds,
  with bounded, rotating work over its databases.
- `clientBandwidth` and `replicationBandwidth`, each with `bytesSent` and
  `bytesReceived`. Client bandwidth counts actual HTTP body reads and writes
  after a request resolves an authorized database; it excludes HTTP/TLS headers.

Failed writes do not become committed operation counts. Client request counts,
errors and latency still include failed requests that resolved their database.
Long-lived streaming requests record their final request count and elapsed time
when they finish, while their body wrapper preserves cancellation and streaming.

## Replication response and byte meter

`ProjectReplication` has the same window fields and `items`. Each database-region
item contains `status`, period `replicatedOps`, `lagMs`, `divergence`,
`convergenceMs`, `lastApplied`, `bandwidth`, and `series`. A bandwidth entry has
`sourceRegion`, `destinationRegion`, `kind`, `bytesSent`, `bytesReceived`;
series points have `start`, `replicatedOps` and the same bandwidth breakdown.
An observing sender increments `bytesSent`; an observing receiver increments
`bytesReceived`. When combining regional rows, sum sent and received separately:
the two observations describe the same directed transfer.

The own usage meter is `replication_bytes`, separate from `client_bytes`.
It counts the encoded protobuf messages after LZ4, plus the five-byte gRPC
message prefix on peer streams; existing WAN messages count their encoded NATS
payloads. Protocol replies and ACKs count in their actual direction. This is
message traffic metering, excluding HTTP/2, TLS, TCP and broker framing.
Repeated transfers count bytes each time; replicated operations count only
when the durable journal transaction applies a new group, including with
concurrent duplicate streams. Incomplete writes do not provide an exact
transport-level byte measurement.

Kinds are `delta` (current replication), `anti_entropy` (frontier handshakes and
WAN discovery), and `handoff` (peer or WAN journal replay after a failed transfer or
initial catch-up). Shared discovery payloads are divided across advertised
databases, conserving their encoded length. No tenant identifiers are used as
Prometheus labels; regional/database dimensions live in bounded usage records.

Health builds on TS-681's verified peer prefixes, divergence and catch-up timing,
plus WAN applied-prefix observations. `lagMs` is the observed backlog's age,
not network round-trip latency. `divergence` denotes detected journal conflict,
not a full comparison of document contents. Status is `diverged` after such an
observation, `lagging` with remaining groups, otherwise `healthy`. Without a
health observation in the last 30 seconds, status is `stopped` with null lag and
divergence. `lastApplied` is the last newly applied received group; convergence
is the latest completed observed catch-up in milliseconds.

## Persistence and limits

The existing usage meter flushes observations into the database's catalog state
every ten seconds and at clean shutdown. Minutes roll into the same lifetime,
hourly and daily totals as existing usage. Minute buckets retain 120 minutes,
hours 35 days, and days 400 days. Catalog followers send the observations through
the existing sequenced `CatalogUsage.Add` RPC; retries remain deduplicated and
the catalog feed replicates the saved observations within the region.

These are best-effort dashboard observations. A crash can lose counts since the
last successful flush; memory pressure can omit observations. Pending records
admit at most 256 keys and 256 meter dimensions per key, and persisted maps keep
at most 256 dimensions. `terrabase.usage.observations.dropped` reports bounded
admission drops. A follower batch carries at most 1 MiB of measurement JSON,
256 KiB per entry, with validated meter names, dimensions and histogram sizes.
There are no migrations or backfills; existing entity-ID generation is owned by
TS-754, and this slice introduces no ID encoding or compatibility fallback.
