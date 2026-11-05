# Shape metrics and memory admission

All instruments use the host-scoped `TerraScale.TerraBase` meter and the existing administrative `/metrics` scrape.
Shape telemetry has no labels: organization, project, database, table, shape and handle identifiers are never
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

`terrabase.storage.reads` counts attempted backend SELECT executions rather than returned rows or HTTP requests. Misses and empty results count. Reusing an already-loaded sealed body avoids its payload SELECT, but metadata and generation checks can still read storage.

Shape sealing and snapshots count their table definition and generation checks, change capture scans, head and
boundary lookups and table row reads under `path="shape"`, as do persisted chunk/page
metadata reads and shape retention's floor/candidate SELECTs. Persisted payload
SELECTs use `path="shape_body"`, so a shared flight's one body read can be distinguished from its metadata and
proof reads. One payload read does not mean one total SQL statement. Reusing a stored
seal counts its generation, head and stored-chunk lookups; an absent chunk counts its attempted size SELECT.
The exporter retains its bounded label policy and overflow sentinel.

This is origin storage work even when SQLite serves it from its page cache; it does not measure physical disk I/O.
Benchmark comparisons must take before/after deltas on the same node-scoped metrics listener and name their sampling
window. No API, stored format, authentication or migration behavior changes.
