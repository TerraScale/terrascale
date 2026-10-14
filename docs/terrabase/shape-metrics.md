# Shape metrics and memory admission

All instruments use the host-scoped `TerraScale.TerraBase` meter and the existing administrative `/metrics` scrape.
Shape telemetry has no labels: organization, project, database, collection, shape and handle identifiers are never
recorded. This recorder is the FR-17 contract; FR-10 owns production registration and FR-20 owns component calls.

`IShapeMetrics` records successful 200/304 response reads (`terrabase.shape.reads`), waiting requests
(`terrabase.shape.requests.waiting`), running flights (`terrabase.shape.flights.active`) and redirects
(`terrabase.shape.redirects`). Waiting and flight instruments are up/down counters: callers add on entry and subtract
in `finally`, including cancellation and error paths.

`RecordSeal` records successfully published seals in `terrabase.shape.seals`, their duration in seconds in
`terrabase.shape.seals.duration`, and their cumulative published bytes in `terrabase.shape.sealed_bytes`.
`RecordMirrorLag` records a histogram of seconds from seal publication to mirror completion in
`terrabase.shape.mirror.lag`. These are completion measurements, not a gauge of pending mirror work.

Before allocating a seal, page or flight, call `TryReserve(bytes, tenant)` and retain its `IMemoryReservation` for the
entire lifetime of the admitted memory. Null means refusal: do not allocate or begin that work. Dispose the lease on
completion, cancellation and error. Pass the owning tenant explicitly for shared/background shape work, preserving
both process and organization limits; null charges system work only. Reservations use `MemoryLane.Foreground`.

## Origin storage reads

`terrabase.storage.reads` counts backend document/feed SELECT executions, rather than returned rows or HTTP requests.
Its only application label is `path`, whose bounded values are `point`, `query`, `feed` and `shape`. Each instrument
update adds one immediately before an attempted SELECT; misses and empty results
count, while reads answered from a document or sealed-body cache do not execute those storage sites.

Feed enumeration, continuation, head/floor, retention and prefix-verification SELECTs count individually, as does each
document body SELECT performed for a feed event. A feed page can therefore increment the counter many times.
Collection existence checks, schema-admission metadata size queries, state-only/repair scans, replication-log reads and general
accounting scans are outside this counter. State lookups that materialize document JSON count as point reads, including
write/join preconditions; an in-transaction pending state skips that SELECT. PUT admission shares one collection-state
lookup between schema validation and the retained drop barrier. Point version and size lookups and
write/join uniqueness document lookups are included because they perform document SELECTs. System-database document
reads and collection-metadata documents fetched through the document helper also count. A query or document-list reader counts one execution,
independently of the rows returned; an additional sort-budget pre-scan counts separately. Query body-fetch SELECTs
also count individually under `path="query"`, so a query can perform more than one backend read.

Shape sealing's feed enumeration, size projections, document materialization and resume-prefix reads count under
`path="shape"`, as do persisted chunk size/body reads and shape retention's floor/candidate SELECTs. Reusing a stored
seal counts its head and stored-chunk lookups; an absent chunk counts its attempted size SELECT. Collection-lineage
admission and CDC validation remain excluded, following the metadata/capture exclusions above.

This is origin storage work even when SQLite serves it from its page cache; it does not measure physical disk I/O.
Benchmark comparisons must take before/after deltas on the same node-scoped metrics listener and name their sampling
window. No API, stored format, authentication or migration behavior changes.
