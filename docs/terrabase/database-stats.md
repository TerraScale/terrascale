# Live per-database stats (TS-806)

Each TerraBase member counts reads, writes, requests, replication bytes and lag per database in memory.
Every 5 seconds it closes a window, exposes bounded aggregates on the existing `/metrics`, and publishes a compact
rollup on ts-orchestrator's NATS. It also answers detail requests over NATS. The feature is always on, has no
configuration, and does not change the storage format.

## What is counted

The counters live in `IDatabaseStats` (`src/TerraScale.TerraBase/Features/Stats/`).

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
| `terrabase.stats.rollups` | `terrabase_stats_rollups_total` | Counter<long> | `result`=published\|failed\|no_cell | 3 |

Each instrument has a `CardinalityLimit` in the view switch in `Hosting/Telemetry.cs`.

No tenant id, database id or collection id is ever a label. The labels are the fixed values in the table above.
`peer_region` is a replication peer's region: it must pass `TerraBaseOptions.IsRegion`, and at most 64 appear.

## NATS

There are two subjects. `{cell}` is this member's cell token.

| Subject | Mode | Scope |
| --- | --- | --- |
| `ts.orch.v1.{cell}.obs.terrabase` | Publish every window | Regional only. The controller subscribes to its own `obs.>`. The link role does not carry it across regions. |
| `ts.orch.v1.{cell}.query.terrabase-stats.detail` | Request and reply | Every member answers once. |

How the cell is derived:

1. Read the `cell:<token>` tag from the NATS user JWT in `nats-credentials-file`.
2. Take the text between `-----BEGIN NATS USER JWT-----` and `------END NATS USER JWT------`.
3. Base64url-decode the middle segment, then parse the JSON.
4. Use the first `nats.tags[]` entry that starts with `cell:`.
5. The token must pass `WanSettings.IsToken`.

The value is read lazily and cached once found. This is the same value that the TS-803 role templates use with
`{{tag(cell)}}`, so no region list is needed. `IRegionBus` is registered whenever `options.Nats` is set, not only for
WAN.

Without a cell:

- The member does not publish and does not serve detail requests.
- A member without NATS configured publishes and counts nothing.
- `no_cell` is counted (`terrabase.stats.rollups{result=no_cell}`) only when NATS is configured and the credentials
  carry no `cell:` tag. The member logs one warning then.
- The warning never includes the credential contents.
- Startup never fails.

Required TS-803 grants for the terrabase role:

1. Publish to `ts.orch.v1.{{tag(cell)}}.obs.terrabase`.
2. Query verb `terrabase-stats`: subscribe to `ts.orch.v1.{{tag(cell)}}.query.terrabase-stats.>` and one response per request, as for `terrabase-wan`.
3. A `cell` tag on the NATS user JWT.

## Rollup v1

The rollup is JSON, registered in `TerraBaseJsonContext`, with web defaults (camelCase). `Counters`, `Peer` and
`Database` are defined below the example.

```json
{
  "v": 1,
  "id": "01J...ULID of this rollup",
  "epoch": "01J...ULID of this process; seq restarts with it",
  "seq": 42,
  "cell": "gru", "region": "gru-1", "member": "gru-1-db-1",
  "at": "2026-10-10T19:00:05.000Z",
  "windowMs": 5000,
  "databases": 1234,
  "totals": { Counters },
  "peers": [ { Peer } ],
  "top": [ { Database } ]
}
```

Fields:

- `Counters`: `reads`, `writes`, `requests`, `errors`, `sentBytes`, `receivedBytes`, `lagGroups`, `lagSeconds`,
  `conflicts`, `storageBytes` (omitted when unknown), and `latency` (19 integers, one per bound; omitted when there
  were no requests).
- `Peer`: `region`, `sentBytes`, `receivedBytes`, `lagGroups`, `lagSeconds`, `conflicts`. Every peer region with
  traffic or state appears.
- `Database`: `org`, `project`, `database` (catalog ids, no names), the `Counters` fields, and `peers` (only peers
  with a nonzero value).

Publishing:

- Each publish has a 2 second timeout. A timeout or error counts `terrabase.stats.rollups{result=failed}`.
- `seq` still advances on a failed publish, so consumers see a gap.

Top list (`top`):

- It lists the databases of the last window that had activity or a storage sample.
- It is the union of three lists: the top 20 by replication bytes (sent plus received), the top 20 by operations
  (reads plus writes), and the top 20 by storage.
- It holds at most 60 entries.
- Order: replication bytes descending, then operations descending, then database id.
- At most 60 entries times at most 64 peers. Keep the message well below 1 MiB, the WAN `MaxMessageBytes`.

Latency bins (milliseconds):

`0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536`

Bin `i` counts requests with elapsed time at or below bound `i`. The last bin includes everything longer. The bounds
are defined once in `StatsLatency.BoundsMs`. `UsageReading.LatencyBounds` returns the same list.

Region merge rules, when rows from members of one region are combined:

| Field | Rule |
| --- | --- |
| `reads`, `writes`, `requests`, `errors`, `sentBytes`, `receivedBytes` | Sum |
| `storageBytes` | Max |
| `lagGroups`, `lagSeconds`, `conflicts` | Max (each member replicates on its own; the region is as far behind as its worst member) |
| `latency` | Sum each bin, then compute p50 and p99 from the merged bins. Percentiles cannot be merged. |

## Detail v1

Request:

```json
{"v":1, "database": "<databaseId>"?, "after": "<databaseId>"?}
```

Reply, one per member, over the last closed window:

```json
{"v":1, "cell", "region", "member", "epoch", "at", "windowMs",
 "databases": [Database...], "next": "<databaseId>" | null}
```

Rules:

- Without `database`: the databases of the last window that had activity or a storage sample, sorted by database id
  (ordinal), after `after`. At most 500 databases and at most 4,096 peer entries per reply, so one reply stays under
  1 MiB.
- `next` is always present: the last returned database id when more remain, `null` on the last page. It is the
  cursor for the next page.
- A member that does not track the requested database replies with an empty `databases` array; the requester merges the replies of every member.
- With `database`: that database only, plus `collections`, an array of `{name, reads, writes, documents, complete}`.
  - At most 256 collections, from `IStorageTransaction.ListCollectionsAsync`.
  - `documents` comes from `CountAsync(collection, null, maxExamined)`.
  - All collections share one budget: 1,000,000 examined rows and 2 seconds.
  - `complete` is `false` once the budget is spent.
- The counts run in one read transaction from `IStorageEngine.BeginTransactionAsync` with the entry's `TenantContext`,
  disposed afterwards.
- A malformed request, or `v` other than 1, gets `{"v":1,"error":"bad_request"}`.
- A storage error while counting gets `{"v":1,"error":"unavailable"}`.

## Not in scope

- Storage per collection.
- Database names. Only catalog ids appear.
- Persisted history. Stats are in memory, and counts restart with each process (`epoch` changes). For persisted
  history, see [`docs/terrabase/analytics.md`](analytics.md).
- Any storage-format change.
