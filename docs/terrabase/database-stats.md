# Live per-database stats (TS-806)

Each TerraBase member counts reads, writes, requests, replication bytes and lag per database in memory.
Every 5 seconds it closes a window and exposes bounded aggregates on the existing `/metrics`, for metrics collection. The feature is always on, has no configuration, and does not change the storage format.

## What is counted

The counters live in `IDatabaseStats` (`src/TerraBase/Features/Stats/`).

Database level. Only `UsageMeter` feeds these, through its existing metering calls:

| Stats call | Fed by `UsageMeter` | Counts |
| --- | --- | --- |
| `Read` | `RecordRead` | reads |
| `Wrote` | `RecordWrites` | writes |
| `Request` | `RecordRequest` | requests, errors (status 400 or above), latency |
| `ReplicationBytes` | `RecordReplicationBytes` | sent and received bytes, per peer region |
| `ReplicationState` | `RecordReplicationState`, `RecordPeerState` | remaining groups, lag, diverged, per peer region |
| `StorageSampled` | `RecordStorage` | replica size (gauge) |

Collection level. Only `DocumentService` feeds these, because only the document API knows the collection:

- `CollectionRead` counts each get and query.
- `CollectionWrote` counts each committed Put, Patch and Delete.

Rules for the hot path:

- A call does not allocate once its database, peer or collection entry exists.
- Counters use `Interlocked`. Gauges use `Volatile.Write`.
- The database key is the organization, project and database ids. The plan is not part of the key.

## Windows

- `StatsService` closes a window every 5 seconds on one thread.
- Closing a window reads and resets every counter. All counters are deltas for that window.
- Rates use the real elapsed duration of the window, not the nominal 5 seconds.
- Gauges (storage, remaining groups, lag, diverged) keep their last observed value. They are not reset.
- The last closed window is kept for `/metrics` and for detail requests.
- Cumulative totals since process start feed the Prometheus counters.

## Semantics

Lag means something different for each peer region:

| Peer region | `lagGroups` counts |
| --- | --- |
| This member's own region | Change groups that in-cell peers have not durably acknowledged (`UsageFlushService.RecordPeers`, then `RecordPeerState`). |
| Another region | Change groups that region committed and this member has not applied (`WanPuller`, then `RecordReplicationState`). |

Aggregation within one member (a database, a peer region, or the member's totals):

| Field | Rule |
| --- | --- |
| `lagGroups` | Sum of `Remaining` over the scope. |
| `lagSeconds` | Largest lag over the scope: the observed lag in milliseconds divided by 1,000. |
| `conflicts` | Number of (database, peer region) pairs whose last observation said diverged. |

Storage:

- `storageBytes` is this member's replica size. The usage storage sampler takes it, 128 databases per minute.
- Each member of a region holds a replica. Across members of one region, take the max. Never sum.

What sums across members:

- Reads, writes, requests, errors, and sent and received bytes are per member. They sum across members.

## Bounds

Every cap is a constant. There are no knobs.

| Cap | Value | Applies to |
| --- | --- | --- |
| `MaxDatabases` | 16,384 | Database entries |
| `MaxPeers` | 65,536 | Peer entries, all databases together |
| `MaxCollections` | 65,536 | Collection entries, all databases together |
| `MaxPeersPerDatabase` | 64 | Peers of one database |
| `MaxCollectionsPerDatabase` | 256 | Collections of one database |
| Latency bins | 19 | Per database, allocated once |
| Idle eviction | 3 hours | Database entries |

Overflow behaviour:

- When the database table is full, the event goes to one shared `Overflow` entry. It counts in totals only, never in
  top lists or detail.
- When a peer or collection cap is hit, database-level counters still count. Only the breakdown is skipped.
- Every refused event adds to `terrabase.stats.dropped{kind=database|peer|collection}`.
- Cumulative per-peer bytes are bounded by 64 regions. Beyond that, the event is dropped and counted.
- A peer region must pass `TerraBaseOptions.IsRegion`. Otherwise the event is ignored.

Idle eviction:

- On each close, an entry whose last activity is more than 3 hours old is removed, with its peers and collections.
- Activity means any nonzero counter in a window, or a storage sample.
- A late write that races eviction can lose one event. This is accepted.

Memory:

- Worst case is about 20 MB. This is the design estimate.
- Counts are tracked with `Interlocked` integers. The code never reads `ConcurrentDictionary.Count`, because it takes
  every lock.

## Prometheus

The instruments are on the `TerraScale.TerraBase` meter (`Hosting/Telemetry.cs`). They are scraped at `/metrics` on
the administrative listener.

Names follow the exporter's rules: dots become underscores, units become suffixes (`By` → `_bytes`, `/s` → `_per_second`), and counters get `_total`.

| Instrument | Prometheus name | Type | Tags | Series limit |
| --- | --- | --- | --- | --- |
| `terrabase.stats.databases` | `terrabase_stats_databases` | ObservableGauge<long> | none | 1 |
| `terrabase.stats.operations` | `terrabase_stats_operations_total` | ObservableCounter<long>, cumulative reads and writes | `kind`=read\|write | 2 |
| `terrabase.stats.replication` (unit `By`) | `terrabase_stats_replication_bytes_total` | ObservableCounter<long>, cumulative | `peer_region`, `direction`=sent\|received | 129 |
| `terrabase.stats.top.replication` (unit `By/s`) | `terrabase_stats_top_replication_bytes_per_second` | ObservableGauge<double>, last window, sent plus received | `rank`=1..10 | 10 |
| `terrabase.stats.top.operations` (unit `{operation}/s`) | `terrabase_stats_top_operations_per_second` | ObservableGauge<double>, last window, reads plus writes | `rank`=1..10 | 10 |
| `terrabase.stats.dropped` | `terrabase_stats_dropped_total` | Counter<long> | `kind`=database\|peer\|collection | 3 |

Each instrument has a `CardinalityLimit` in the view switch in `Hosting/Telemetry.cs`.

No tenant id, database id or collection id is ever a label. The labels are the fixed values in the table above.
`peer_region` is a replication peer's region: it must pass `TerraBaseOptions.IsRegion`, and at most 64 appear.

## Not in scope

- Storage per collection.
- Database names. Only catalog ids appear.
- Persisted history. Stats are in memory, and counts restart with each process (`epoch` changes). For persisted
  history, see [`docs/terrabase/analytics.md`](analytics.md).
- Any storage-format change.
