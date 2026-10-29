# Project replication and analytics (TS-762)

TerraBase owns these observations. ts-go reads the public project API with a
ts-auth project JWT; ts-dashboard reads them through ts-go. The four routes use
the v1 API:

| Method and path | operationId |
| --- | --- |
| `GET /v1/projects/{project}/analytics` | `getProjectAnalytics` |
| `GET /v1/projects/{project}/databases/{database}/analytics` | `getDatabaseAnalytics` |
| `GET /v1/projects/{project}/replication` | `getProjectReplication` |
| `GET /v1/projects/{project}/databases/{database}/replication` | `getDatabaseReplication` |

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

Requests read at most 100 databases and 16,384 emitted database-region buckets
(after the `region` filter; unobserved remote placements count nothing) within five seconds, with memory admission held until the
response has been sent. Invalid
filters/ranges and excessive queries return 400; exceeding the five-second
execution budget returns 503 `capacity`. Cancellation
releases request admission.

## Analytics response

`ProjectAnalytics` contains `projectId`, `range`, `from`, `to`, `bucketSeconds`
and `items`. Each item has `databaseId`, `databaseName`, `region` and `series`.
Items cover the local region while the database is placed there and every region
with retained observations, also one no longer in its placement; unobserved
remote placements are omitted.
Each point contains:

- `start`, request counts by HTTP method, operation counts (`read`, `put`,
  `patch`, `delete`), and errors (HTTP status 400 or above, or an unhandled error).
- `latency`: approximate `p50Ms`, `p95Ms`, `p99Ms` from merged power-of-two histogram
  bins (0.25 ms to 65,536 ms), summed in 128-bit integers. As Prometheus'
  `histogram_quantile`, a percentile is interpolated linearly by rank between the
  bounds of the bin holding it (0 below the first). Empty request buckets return
  null percentiles. The highest bin includes requests above 65,536 ms; a
  percentile there reports its lower bound, 32,768 ms.
- `storageBytes`: the average sampled physical database size, including the
  engine's sidecars; null before any sample. Each node samples once per minute:
  the next 128 databases in owner order after the last one sampled, wrapping,
  so every database is sampled within ceil(databases / 128) minutes whatever
  order the engine lists them in. A database whose size cannot be read loses
  only its own sample, and a pass that fails is retried within the minute
  without sampling a database twice. Every sample of a pass belongs to the
  minute the pass started in, however long sizing takes. Sampling runs on its own loop, so a slow
  database listing never delays usage delivery.
- `clientBandwidth` and `replicationBandwidth`, each with `bytesSent` and
  `bytesReceived`. Client bandwidth counts actual HTTP body reads and writes
  after a request resolves an authorized database; it excludes HTTP/TLS headers.
  Endpoints that resolve their database themselves, such as SQL EXPLAIN, attribute
  the request to it without counting a document operation.
  Requests naming an unknown database record nothing.
  A PostgreSQL wire exchange (a simple query, or extended-protocol messages up to
  their Sync) counts as one `other` request once a statement in it resolved the
  authorized database, with its latency, an error when any of its messages
  failed, and its protocol message bytes in each direction.

- `statements`: SQL statements for protocol `postgres` (the PostgreSQL wire),
  with `statements`, `errors` and `latency` (the same percentiles as requests,
  of each statement's own run).
  See [Statements and requests](#statements-and-requests).

Failed writes do not become committed operation counts. Client request counts,
errors and latency still include failed requests that resolved their database.
Body bytes count in the minute they were transferred: a request reports its
pending bytes when it moves on to another minute, on every ten-second flush tick
while it stays open (an idle subscription included), and when it finishes, with
its request count and elapsed time. The body wrapper preserves cancellation and
streaming.

## Statements and requests

A request is one HTTP request, or one PostgreSQL wire exchange; a statement is
one SQL statement it carried. They are measured separately and never change
each other's status:

- A PostgreSQL exchange is a request error when any message in it failed; each
  statement that reached the database is one statement, its failure one
  statement error. Describing a statement is not one, unless describing it fails
  (it then never runs). Session-local commands (`SET`, `SHOW`, type loads) and
  statements a revoked or wrong key sends are not attributed to the database.
- Statement latency is the statement's own run, including admission; request
  latency is the whole request or exchange.

Request logs (`GET .../databases/{database}/logs`) carry `statements` and
`statementErrors` per entry, and record one entry per PostgreSQL exchange with
route `postgres`, method `OTHER`, status 400 when the exchange failed, and a
`requestId`. A PostgreSQL error response's `DETAIL` is `request id <ULID>`, the
same ID as its log entry, as `X-Request-ID` is for HTTP. Entries never contain
SQL text, parameters or keys.

Isolation, limits and retention are those of the other meters: observations are
attributed only after the database is authorized, read only within the
project's verified grant, kept per database and observing region with the usage
history, and request logs keep at most the latest 100 entries within 24 hours,
best effort.

## Replication response and byte meter

`ProjectReplication` has the same window fields and `items`. Each database-region
item contains `status`, period `replicatedOps`, `lagMs`, `divergence`,
`convergenceMs`, `lastApplied`, `bandwidth`, and `series`. A bandwidth entry has
`sourceRegion`, `destinationRegion`, `kind`, `bytesSent`, `bytesReceived`;
series points have `start`, `replicatedOps` and the same bandwidth breakdown.
An observing sender increments `bytesSent`; an observing receiver increments
`bytesReceived`. A sender counts each `delta` batch it writes to a peer stream
and each `anti_entropy` repair page it answers a peer's bucket read with, by
the same encoded size the receiver counts. A receiver counts only the batches it
applies, so a refused batch counts as sent alone; digest replies count only at
the requester, which alone knows whether its round differed. When combining regional rows, sum sent and received separately:
the two observations describe the same directed transfer.

The own usage meter is `replication_bytes`, separate from `client_bytes`.
It counts the encoded protobuf messages after LZ4, plus the five-byte gRPC
message prefix on peer streams; existing WAN messages count their request and reply
payloads. Protocol replies and ACKs count in their actual direction. This is
message traffic metering, excluding HTTP/2, TLS, TCP and broker framing.
Repeated transfers count bytes each time; replicated operations count only
when the durable journal transaction applies a new group, including with
concurrent duplicate streams. Incomplete writes do not provide an exact
transport-level byte measurement.
A gRPC Hello is charged to the region its Welcome names; a handshake that fails
without any answer falls back to the region of that peer's previous stream, and
before any stream identified it, cannot be attributed to a region pair.

Kinds are `delta` (current replication), `anti_entropy` (frontier handshakes, WAN
discovery and TS-678 Merkle repair: digests, range pages and merges, attributed by
the regions the caller sends in its shard identity and the peer returns with its
digest; a request counts once its identity is validated and its database found,
before the state work that may still reject it; an initiator counts each request
before awaiting its response, and an unanswered digest goes to the region that
peer named in its last answered digest, whether or not that repair then succeeded), and `handoff` (peer or WAN journal replay after a failed transfer,
initial catch-up, or a backlog longer than one 256-group sender turn). A peer
replay stays `handoff` across turns until a turn ends with the peer at the head. Shared discovery payloads are divided across advertised
databases, conserving their encoded length. No tenant identifiers are used as
Prometheus labels; regional/database dimensions live in bounded usage records.

Health builds on TS-681's verified peer prefixes, divergence and catch-up timing,
plus WAN applied-prefix observations. Outbound peer health comes from the sender's
TS-681 detail snapshot, which holds at most 128 databases per node; databases
beyond it report inbound and WAN health only, within the 256 pending measurement
keys per node. `lagMs` is the observed backlog's age,
not network round-trip latency. `divergence` denotes detected journal conflict
(a verified prefix conflict, an origin's explicit ahead answer, or a group that
conflicts with the applied journal prefix; malformed or oversized messages are
failed pulls, not divergence), not a full comparison of document contents. WAN
health is kept per peer region, database and origin: the highest sequence an
origin advertised stays its target until the verified applied prefix reaches
it, so neither a round that does not reach it nor a stale lower advertisement
can report its database caught up; an ahead answer or journal conflict holds, through later failed or
unverified pulls, until a group from that origin verifiably extends the applied
journal, or matches the journal head when another stream committed that group first
(an older duplicate is not compared, so it resolves nothing). Entries go with their database, and an undiverged one when a complete
discovery sweep did not freshly advertise it (a failing retry is no
advertisement); an incomplete sweep keeps every outstanding target however long
it runs. A database a sweep certifies this way gets its updated health published. At most 4,096 origins are retained per member; a divergence beyond that
is reported only in the rounds where it fails, and logged. Health sources
refresh every ten seconds: each sender's peer observations and each WAN round.
Every peer observed in the last ten minutes counts. Without any observation from
the last 30 seconds, status is `stopped` with null lag and divergence. Otherwise
it is `diverged` if a fresh observation reports divergence (a silent peer's old
divergence is not current), `stopped` if any has been silent for more than 30
seconds, `lagging` with remaining groups, otherwise `healthy`. A WAN
round reports a backlog for any origin it finds behind. A peer silent for ten minutes is
treated as removed. `lastApplied` is the last newly applied received group; convergence
is the latest completed observed catch-up in milliseconds.

## Persistence and limits

The existing usage meter flushes observations into the database's catalog state
every ten seconds and at clean shutdown. Minutes roll into the same lifetime,
hourly and daily totals as existing usage. Minute buckets retain 120 minutes,
hours 35 days, and days 400 days. Each machine saves its own observations in its
local catalog. Catalogs are not replicated until R10a, so these totals are per machine
([multi-region-replication.md](multi-region-replication.md) section 15).

These are best-effort dashboard observations. A crash can lose counts since the
last successful flush; memory pressure can omit observations. Pending records
admit at most 256 keys and 256 meter dimensions per key; open replication
backlogs keep at most 256 database/peer pairs, evicting the least recently
observed. Rolled-up hour, day and lifetime maps are not capped: they hold the
deployed regions' dimensions, and capping them would break subtraction.
`terrabase.usage.observations.dropped` reports
bounded admission drops. Usage
totals skip the meters JSON when reading. Analytics reads keep only the requested
region's dimensions of each entry as they read it and admit memory for what they
keep; exhausted admission returns 400. Byte meters (client and
replication bandwidth, storage) are bounded only by their 64-bit range, since
their aggregates outgrow the 10^12 operation-count bound; merging and every
analytics total saturate at that range instead of wrapping negative or failing.
There are no migrations or backfills; existing entity-ID generation is owned by
TS-754, and this slice introduces no ID encoding or compatibility fallback.
