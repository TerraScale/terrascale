# Query shape statistics

Query shapes group the local workload for index planning. Tenant JSON queries
(including document lists), dashboard document-space queries, and SQL runs use
one tracker. SQL checks, saved-query writes, assertions, replication, schema
sampling, and change streams do not count as query executions.

`GET /management/organizations/{organization}/projects/{project}/databases/{database}/query-shapes`
requires the same organization membership as other database management reads.
`GET /management/query-shapes` reads the separate dashboard document-space
statistics and requires the management service credential on the loopback
administrative listener. Both endpoints return an `items` array, sorted by query
frequency, then collection and shape id. No new versioned route is introduced.

Each item contains `id` (SHA-256 of the canonical shape), `collection`, `shape`,
`queries`, `totalLatencyMs`, `maxLatencyMs`, `rowsScanned`, and `lastSeenAt`.
Average latency is `totalLatencyMs / queries`. Results include persisted and
pending observations without forcing a write.

## Identity

The canonical JSON array starts with `alpha` and the language (`json` or `sql`).
JSON and SQL comparison semantics remain separate. It preserves decoded field
paths, comparison operators, scalar types, negation, AND/OR structure, and
effective sort order. Same-kind AND/OR groups flatten and their children sort
ordinally. Repeated sort paths retain their first occurrence; sorting stops at
the unique document id, and the effective id tie-break is explicit.

Literal values, SQL text, prefix contents, cursor positions, projection, page
size, byte limit, and count options are omitted. IN operands contribute their
distinct scalar types, ignoring value order, duplicates, and list length.
Numbers share one type regardless of integer or floating-point representation.
The literal-free identity is **not** used for result caching: cache keys continue
to retain all parameters affecting results.

## Observations and limits

Frequency counts requests, including cache hits and executions interrupted by
the engine work budget. Latency uses monotonic service timestamps, including
queueing and result projection inside the service. Invalid requests rejected
before execution do not add observations. Cached pages add zero scanned rows.

Scanned rows mean candidate document visits, including candidates rejected by
residual predicates, sort-admission walks, and optional count/overflow walks.
Repeated visits count separately. Fetching the body of an admitted result does
not count again. Index constraints restrict the candidates; returned rows and
SQLite VM instructions are not used as scan estimates. SQLite counts visits
through a non-deterministic observation predicate before residual filtering;
integration coverage exercises both collection scans and indexed residual
rejection. Partial visits remain available after cancellation or early stream
disposal.

Pending observations are bounded to 1,024 shapes per process and 128 per database
(the dashboard has its own share); the persisted/API limit is also 128 per
database. A canonical shape is at most 8 KiB UTF-8, with a smaller conservative
path/tree bound before normalization. Temporary normalization and retained
entries reserve bytes through the process memory budget. New shapes are dropped
when admission is full; existing admitted shapes continue accumulating. The
`terrabase.query_shapes.dropped` metric has bounded reason labels.

## Persistence

On a catalog primary, the tracker flushes every 10 seconds and at clean shutdown
to TerraBase JSON documents in `databases/{id}/query_shapes`; dashboard records
live in `dashboard_query_shapes`. Each stored record declares `format: alpha`.
Separate flush markers resolve a write that committed before throwing, avoiding
double counting on retry. Flush batches are bounded and serialized against
combined reads. Database deletion removes its statistics, and a flush checks
that the database still exists before writing.
An unsupported format or malformed statistics record makes its scope's API
return 503. Flushes leave that record intact and drop its new observations with the `unsupported_format`
metric reason; other shapes and databases continue persisting.

Statistics are best-effort: a crash loses observations since the last flush,
and a full persisted set drops new shapes rather than evicting old ones.
Catalog followers currently combine replicated statistics with their own
bounded pending observations. Those local observations are not forwarded or
persisted and are lost on restart. Cluster-wide forwarding is a separate TS-684
slice; these figures must not be presented as durable cluster-wide totals.
