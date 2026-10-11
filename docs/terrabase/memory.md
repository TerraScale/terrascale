# Memory budget, caches and the storage pool

## Budget

`memory-budget` (bytes, default a quarter of the memory available to the process, container limits
included; at least 1 MiB) is the memory TerraBase plans for caches. `IMemoryBudget`
(`Features/Memory`) admits reservations against it; today one limit is shared by every lane.

| Share | Used by |
| --- | --- |
| ¼ | Document and list-page cache (`Features/Documents/DocumentCache.cs`); every entry is also reserved from `IMemoryBudget` |
| ¼ | SQLite page caches: `cache_size` per connection is this ÷ `storage-threads`, and `soft_heap_limit` requests this quarter as a process-wide soft limit (a second engine only raises it; this is not hard memory admission) (`docs/terrabase/sqlite-query-plans.md`) |
| all | SQLite `mmap_size`: address space backed by the OS page cache, not the heap |
| rest | Request buffers and everything not yet reserved |

## Document and list-page cache

Read-through, in front of `IStorageEngine`, for `GET` of one document (keyed by organization, project
and database ULIDs, collection and id) and list/query pages (keyed by the same tenant identity, collection,
normalized typed filter, sort, cursor,
limit, byte bound and optional count limit). Projection happens after the cache, so projections
share raw pages; a count is cached with the page from the same transaction. Cache admission reserves
memory before retaining detached rows, sort keys and JSON buffers. Each hit returns a separate copy,
so mutations through a caller's JSON backing array or page collections cannot change later reads.
API-key lookups use a separate system key namespace in the same cache, opened only through
`OpenSystemDatabaseAsync`; tenant identities cannot address it. Hot document, query-page and API-key
values bypass SQLite; tenant-directory checks still read the catalog. Keys stored before tenancy
still cannot authenticate. Cache hits honor caller cancellation.
Absent documents and databases are not cached; tombstone metadata can be cached.

The clean read tier uses CLOCK eviction (TS-649). It holds at most 4 096 entries; estimated charged bytes
are bounded to a quarter of the node's `memory-budget`, including key and entry overhead. This is not
a bound on process RSS. A read sets an entry's reference bit.
New entries start unreferenced; a hit earns a second chance. When a read needs space, the clock hand
clears referenced entries' bits and evicts the first
unreferenced entry, releasing its reservation immediately. Oversized reads bypass the tier without
evicting useful entries. SQLite remains the durable tier: evicting a clean copy requires no write-back,
and the next read loads it from SQLite. Disposal releases every reservation synchronously.

This is the independent memory-tier groundwork for TS-649. Selective hot-key replication, SLO policy
actions and SDK-based node autoscaling remain pending; replacing the cache does not implement them.
The remaining work depends on TS-647's persisted lattice state, replica placement and per-origin streams
([Anna design](design/anna-core.md)), which have not landed. At the inspected ts-orchestrator `origin/main`
(`6643f0e0`), `docs/machines-api.md` still lists node-pool and scaling mutations as planned;
`internal/hostpool/pool.go` explicitly excludes dedicated TerraBase members from generic host pools.
The inspected ts-dotnet `origin/main` (`13f795b`) has no orchestrator client. Autoscaling needs the public
producer contract and its .NET SDK before TerraBase can call it under the service-ownership rules.

Freshness comes from write stamps (`Engine/IWriteStamps.cs`), not from deleting entries. Every write
through `IStorageEngine` (document API, Management, and replication apply, which must write through
an engine transaction) moves the stamp of the document and of its collection before the write reaches
SQLite and again after it commits; deleting a database or creating/dropping a collection moves every
stamp conservatively, including on commit. A cache entry keeps the stamp
read before its storage read and is served only while that stamp is unchanged, so:

- a read never returns a version older than a write this node acknowledged;
- a write invalidates every cached page of its collection.

Tenant stamps hash organization, project and database ULIDs, independently of plan changes; system
stamps use a separate identity. All wrapper transaction reads, writes and commits join the same ordered
gate. Disposal closes admission and shares one cleanup task that drains already admitted operations.

Stamps live in 4 096 stripes: unrelated keys that share a stripe also invalidate each other.

## Storage pool

Microsoft.Data.Sqlite has no true async I/O, so every SQLite call runs on `storage-threads`
dedicated threads (`Engine/Sqlite/SqliteIoPool.cs`) and request threads only await it. At most
`storage-queue` transactions (and maintenance calls) are admitted at once, each with at most one queued
call; beyond that the request gets `503 Service Unavailable` with `Retry-After: 1`. Writers to one
database queue asynchronously before `BEGIN IMMEDIATE`, so they never hold pool threads while waiting
for the database's write lock (at most 30 s, the `busy_timeout`, then `503`).

An admission stays held through returning or closing its session, including WAL checkpoints on
eviction. SQLite cleanup runs on the same dedicated threads. Shutdown refuses new admissions and
starts draining admitted operations before closing idle sessions and stopping the workers. The
`shutdown-grace-seconds` setting bounds how long shutdown waits, never the lifetime of a connection:
if that wait expires, draining continues and the data directory stays owned until every resource is
closed. Nothing is forcibly closed. An admitted copy can need further SQLite calls after a long
`VACUUM INTO`, and callers must dispose their transactions for the drain to finish.

## Metrics

| Instrument | Meaning |
| --- | --- |
| `terrabase.document_cache.lookups` (`cache` = `document`, `page`; `result` = `hit`, `miss`) | Cache lookups |
| `terrabase.document_cache.promotions` | Clean snapshots admitted from SQLite |
| `terrabase.document_cache.evictions` (`reason` = `capacity`, `stale`, `disposed`) | Clean snapshots removed |
| `terrabase.document_cache.bytes`, `terrabase.document_cache.entries` | Estimated retained bytes and entry count |
| `terrabase.memory.reserved` | Bytes reserved from `memory-budget` |
| `terrabase.storage.admitted` | Storage transactions and maintenance calls admitted |
| `terrabase.storage.rejected` | Storage work rejected as busy (full admission or a 30 s writer wait), each a `503` |
| `terrabase.storage.running` | SQLite calls running on the storage threads |

## Organization shares

[Organization QoS](qos.md) admits foreground request bodies, materialized results and retained cache
entries against both process capacity and the current organization plan. CLOCK cache eviction is
confined to the organization's partition, with a global 4096-entry and quarter-process byte ceiling.
Downgrades trim retained entries before admitting the next request. Null/system reservations retain
process limits without a commercial share. Shape responses share reference-counted result admission
through each consumer's serialization; subscriptions release each event's result admission after flush.
