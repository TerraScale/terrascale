# Write/read performance matrix — durable StorageEngine (2026-09-15)

In-process measurement of the durable `StorageEngine` (WAL append with a
synchronous flush per committed batch, immutable segments on compaction) plus
the `QueryPlanner`/durable-index read path. One harness, one process, five
cases. **Single process, single host, no network — this is not a production
capacity claim.**

## Environment

| Item | Value |
|---|---|
| SDK | `dotnet` 11.0.100-rc.1.26425.128 (building `net10.0`) |
| Runtime | .NET 10.0.12, workstation GC (`IsServerGC=False`, latency mode Interactive) |
| OS | NixOS 26.05, Linux 7.2.4, x64 |
| CPUs | 2 (`taskset -c 12,13`, `DOTNET_PROCESSOR_COUNT=2`, `Cpus_allowed_list: 12-13`) |
| Priority | `nice -n 19` |
| Disk | engine roots under `/tmp` → `/dev/sda2 on / (type xfs)` — a QEMU virtual hard disk on a shared-tenancy VM host, **not** NVMe; every committed batch calls `Flush(flushToDisk: true)` on the WAL |
| Host RAM | 31 GiB total, ~18 GiB available at run start |
| Latency clock | `Stopwatch.GetTimestamp` (1 GHz); allocations `GC.GetTotalAllocatedBytes`; CPU/RSS `Process.GetCurrentProcess` |

Harness: `.codex/evidence/terrabase-perf-write-20260915/source/measurement-harness/`
(copied to `.codex/swarm/measurement/write-read-matrix-20260915/`), assembly
name `TerraBase.Benchmarks` so the repo's existing `InternalsVisibleTo`
exposes the internal query work-receipt fields. Warmups discarded; ops/s is
measured over the sampled window; p50/p95/p99 are nearest-rank, rounded to
0.01 ms.

## Case 1 — sustained writes

Fresh engine root per case. One sample = one `ApplyBatchAsync` call (an
upsert batch of N distinct documents, `DocumentPrecondition.Missing`, bodies
pre-built outside the timed window so the sample times the engine call).
N = 3,000 sampled batches per case after 200 discarded warmup batches. The
WAL is fsynced once per batch by the engine.

| batch | body B | N batches | docs | batches/s | docs/s | p50 ms | p95 ms | p99 ms | alloc/op B | alloc/doc B | cpu ms | rss before MiB | rss after MiB | wal MiB |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 256 | 3,000 | 3,000 | 1,074 | 1,074 | 0.84 | 1.35 | 2.92 | 9,201 | 9,201 | 1,805 | 58 | 80 | 1.7 |
| 1 | 4,096 | 3,000 | 3,000 | 1,119 | 1,119 | 0.80 | 1.49 | 3.01 | 62,802 | 62,802 | 828 | 93 | 97 | 13.4 |
| 16 | 256 | 3,000 | 48,000 | 885 | 14,164 | 1.01 | 1.69 | 4.01 | 119,644 | 7,478 | 1,459 | 113 | 162 | 24.5 |
| 16 | 4,096 | 3,000 | 48,000 | 449 | 7,182 | 1.96 | 3.56 | 5.25 | 916,601 | 57,288 | 4,620 | 327 | 504 | 212.0 |
| 128 | 256 | 3,000 | 384,000 | 314 | 40,199 | 2.47 | 6.01 | 10.88 | 886,834 | 6,928 | 7,307 | 583 | 775 | 195.1 |
| 128 | 4,096 | 3,000 | 384,000 | 70 | 8,965 | 11.91 | 26.56 | 44.57 | 7,280,548 | 56,879 | 41,194 | 2,557 | 3,806 | 1,695.1 |

Method note: batch=1 is the single-`UpsertAsync` path (one mutation per
batch). alloc/op covers the `ApplyBatchAsync` call itself (record encoding,
frame, per-record structures); key/mutation/body construction happens before
the timed window. RSS "before" is not a fresh-process baseline — cases run in
one process, so earlier cases' live sets carry over. The 128×4 KiB case ends
at 3.8 GiB RSS with the full document set resident, as the engine keeps all
documents in memory.

## Case 2 — write amplification

Fresh root; 5,000 × 4,096 B documents in 40 batches of 128 (20,480,000 B
payload). Durable bytes = `Directory.EnumerateFiles` sum over the engine root
(all files, recursively) before writes, after writes, and after one
`CompactAsync`.

| Phase | Durable bytes | Ratio vs payload |
|---|---:|---:|
| before writes | 7 B | — |
| after writes (WAL only) | 21,697,087 B | **1.06x** |
| after one `CompactAsync` | 43,221,599 B | **2.11x** |

File breakdown after compaction: `wal` 0 B (truncated),
`hot/segment-...0001.seg` 21,695,049 B, `CHANGE_FEED_STATE` 21,526,438 B,
`MANIFEST` 105 B, lock/format 7 B. Compaction wrote 5,000 records, 0
tombstones. Two observations from the breakdown: the WAL itself is nearly
payload-sized (1.06x before compaction — record framing dominates the
overhead), and compaction does not reduce total bytes because the publication
state file (`CHANGE_FEED_STATE`, serialized request receipts for the batch
commits, ~payload-proportional here) is rewritten alongside the new segment.
A post-compaction verification read hit.

## Case 3 — point reads

`ReadAsync` hits on the 10,000-document store (256 B bodies), warm process,
200 discarded warmup reads, N = 1,000 sampled reads over pre-shuffled keys.

| N | reads/s | p50 ms | p95 ms | p99 ms | alloc/op B | cpu ms | rss before MiB | rss after MiB |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | 238,943 | 0.00 | 0.00 | 0.00 | 33 | 5 | 3,975 | 3,975 |

Method note: at the required 0.01 ms precision all percentiles round to 0.00
(the window implies ~4 µs mean per hit). The read is a dictionary lookup over
the resident document set; 33 B/op allocated. RSS is the post-case-1 process.

## Case 4 — bounded equality query through QueryPlanner + index

Same 10,000-document store; durable equality index
(`IndexDefinition.Equality("by-name", "name")`) built via
`DurableIndexRuntime.StartAsync` (feed pump poll interval 1 s, idle during
sampling), `QueryPlanner.QueryAsync` with one `IndexPredicate.Equal`,
projection `["name"]`, limit 1. 100 discarded warmup queries, N = 300
sampled queries over pre-shuffled unique names.

| N | queries/s | p50 ms | p95 ms | p99 ms | alloc/op B | cpu ms | rss before MiB | rss after MiB |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 300 | 7,104 | 0.09 | 0.12 | 0.16 | 16,868 | 42 | 3,989 | 3,989 |

- Index catch-up (`StartAsync`): 41,842 ms for 10,000 documents (watermark 79
  of 79 commits). The pump applies catch-up work without sleeping, so this is
  durable index-store work, not poll-interval waiting.
- Plan: index `by-name`, access `EqualitySeek`, covered `False`, snapshot
  fallback `False`, estimated matches 1.
- Work receipt (identical across samples): rowsExamined 3, bytesRead 531,
  workUnits 4, pagesRead null (provider has no page counter), entryReads 2,
  fullDocumentReads 1.

## Case 5 — reopen

`DisposeAsync` the engine over the 10,000-document store, then `new
StorageEngine(root)` in the same process; the 10 first reads after reopen,
each on a document not read before in this process.

| open ms | read 1 | read 2 | read 3 | read 4 | read 5 | read 6 | read 7 | read 8 | read 9 | read 10 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 472.23 | 0.01 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |

Method note: the ~2.5 MB WAL replay cost lands in the constructor/open path
(472 ms); after open, the first read is not measurably slower than the rest
(0.00–0.01 ms at the required precision). OS page cache was not evicted.

## Caveats

- Single process, single host, direct in-process API calls (no loopback
  network involved at all); 2 pinned CPUs at nice 19 under a shared-tenancy
  VM — **not a production capacity claim**.
- The storage is a QEMU virtual disk behind xfs, not NVMe; fsync latency on
  this device shapes every write number above.
- Bodies are pre-parsed and shared per case; caller-side JSON parsing,
  key/mutation construction, and the mutation's own body clone are outside
  the timed windows (allocations/op therefore cover the engine call).
- Case 1 keeps every written document resident (the engine's design), so the
  128×4 KiB case ends at ~3.8 GiB RSS; sustained-write numbers at that scale
  include the memory-management cost of a 384k-document live set.
- Case 4 allocations include at most a couple of idle feed-pump polls (1 s
  interval) inside the sampled window; the receipt fields are read through
  the repo's `InternalsVisibleTo("TerraBase.Benchmarks")`.
- Case 2's post-compaction ratio counts every file in the engine root,
  including the payload-proportional `CHANGE_FEED_STATE` receipt file.
- Percentiles are nearest-rank over the raw sample arrays; 0.00 means
  "below 0.005 ms", not zero.
