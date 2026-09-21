# End-to-end functions qualification — all database functions at volume (2026-09-20)

> Superseded for scale coverage by [`e2e-volume-ladder-20260920.md`](e2e-volume-ladder-20260920.md),
> which reruns this harness with relationships at 10k/100k/1M/10M. The
> product-fix and capacity narrative below remains the reference.

Extends the e2e-volume harness from two operations (insert + scan) to the
engine's full common surface, every operation verified exactly against a
statelessly regenerated expectation, then backup and restore with the
strictest no-data-loss check yet: **deleted documents must be absent** after
restore, not merely absent from a count.

Functions covered, in phase order: batched insert (`ApplyBatchAsync`),
full-collection scan (`ScanAsync`), point read (`ReadAsync`), bulk update
(v1→v2 with `DocumentPrecondition.Version(1)`), bulk delete (tombstones,
versioned preconditions), full backup, incremental backup, interior PITR
restore, final-chain restore, point-read absence sweeps, automatic index
build + activation, indexed range queries through the planner, and
compaction.

Receipt: `bench/receipts/e2e-volume-1m-20260920/` (summary, receipt JSON,
object store, engine roots). A 10× smaller configuration was additionally
hammered ten-for-ten clean at 100k (`/tmp` scratch runs, same binary).

## Run command

```text
nice -n 19 env DOTNET_gcServer=1 DOTNET_PROCESSOR_COUNT=8 \
  dotnet bench/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll \
  --workload e2e-volume --documents 1000000 --batch-size 5000 --point-reads 50000 \
  --updates 25000 --deletes 25000 --queries 1000 --seed 1337 \
  --output bench/receipts/e2e-volume-1m-20260920
```

## Results (1,000,000 documents)

| Phase | Elapsed | Note |
| --- | ---: | --- |
| Bulk write | 19,503 ms | 51,275 docs/s |
| Read-back verify | 8,076 ms | 1,000,000 exact matches |
| Point reads | 428 ms | 740,849 ops/s, p50 0.001 ms, p95 0.002 ms, p99 0.003 ms |
| Full backup | 19,805 ms | artifact 109,911,582 B |
| Bulk updates | 523 ms | 25,000 documents to v2 |
| Bulk deletes | 391 ms | 25,000 tombstones |
| Incremental backup | 9,897 ms | artifact 51,904,579 B |
| Full-boundary restore | 30,931 ms | 0 log entries |
| Full restore verify | 7,421 ms | 1,000,000 exact matches |
| PITR restore | 30,870 ms | 5 of 10 log entries |
| PITR restore verify | 7,459 ms | 1,000,000 exact matches (updates applied, deletes not) |
| Final restore | 32,938 ms | full incremental chain |
| Final restore verify | 7,755 ms | 975,000 exact, 25,000 absent |
| Index build | 55,507 ms | automatic B-tree on `sequence`, shadow-verified, active |
| Indexed queries | 921,689 ms | 1,000 queries, p50 5.065 ms, p95 9.730 ms, p99 2,394.774 ms |
| Compaction | skipped | corpus exceeds the 1,000,000-record per-segment cap |

Correctness invariants all held: exact bodies + versions on every surviving
document at every restore cut, no duplicate or missing ids, updates present
only at or after their publication cut, and every one of the 25,000 deleted
ids reads null on the restored engine.

## Product defects found and fixed

Each fix below is in production code, verified by the full suites
(TerraBase.Api.Tests 1,137/1,137, TerraBase.Tests 480/480 — 4 skipped
baseline) and, where noted, a discriminating regression test.

1. **A snapshot data rebuild destroyed the active-index lifecycle row**
   (`DurableIndexStore.StageRebuild`). The feed pump's rebuild-from-snapshot
   deletes every key in the ZoneTree generation — including the
   coordinator-owned physical-state row it never rewrote. After any feed
   reset on a restored engine, an active index lost its identity, applied
   position, and visibility; every subsequent read observed an empty
   generation and the planner rejected the index permanently (queries threw
   `QueryPlanningException` with zero candidates). The rebuild now skips the
   physical-state key. Regression:
   `AutomaticIndexRecoveryTests.SnapshotRebuildPreservesPhysicalLifecycleState`
   — verified to fail on the old code.
2. **Backup capture disposal masked the real reader failure.** An
   `await using` around the capture let the dispose signal ("disposed before
   all readers reached terminal verification") replace the reader's
   actionable exception — including the `OutOfMemoryException` that was the
   actual trigger during this investigation. The snapshot path
   (`CaptureSnapshotCoreAsync`) and the log-read path (new
   `PrepareLogReadAsync`) now dispose explicitly and never let a secondary
   dispose fault mask the original failure.
3. **Paged snapshot scans were quadratic in pages**
   (`StorageEngine.ScanSnapshotAsync`). Each page re-scanned and re-sorted
   the entire snapshot dictionary: at 5M documents the full backup spent
   30+ minutes in document streaming (a 10M run hung past two hours).
   `SnapshotState` now lazily sorts each collection's keys once and pages by
   binary search. The 5M full-backup phase dropped from 30+ minutes to 2.
4. **The backup capture duplicated the corpus as parsed JSON.**
   `BeginBackupCaptureAsync` re-wrapped every document through a constructor
   that clones the body — a second full copy of the corpus (10+ GiB at 10M
   documents) on top of the engine's own state. Stored documents are
   immutable and were already body-cloned once at construction; the capture
   now shares them and copies only the dictionary structure.
5. **The raw authority scan retained decoded records.**
   `StorageEngineBackupCapture` held every physical record as a decoded
   `StorageLogRecord` (body JSON included) in addition to its raw TBREC
   bytes. New frame-typed readers (`RawStorageRecord`) index and hash raw
   bytes only, with a header-only `StorageRecordCodec.ReadSequence`
   (dispatching per wire family: replication frames carry the sequence at
   byte 8, internal at 10, published/unpublished at 9) and records decoded
   transiently at yield. The capture dictionary is also pre-sized from the
   captured frontier so it never doubles (each doubling at volume strands a
   several-hundred-MB contiguous array as fragmentation).

An adversarial review workflow (four lenses, per-finding refutation pass —
16 agents) over the harness change confirmed ten findings, all fixed: the
PITR expected-state modelled partially applied deletes as the tail-most
subrange instead of the head of the tail (guaranteed spurious failure for
deletes-only configurations above two batches), `--updates 0 --deletes 0`
was accepted but structurally unrunnable, three phase timers folded
minutes of engine reopen into the reported phase, throughput and percentile
figures measured different work (now derived from summed per-operation
time), and first-query activation retries were recorded as query latency.

## Capacity findings (measured, not fixed tonight)

- **The .NET 11 capture ceiling.** At 10M documents the backup phases no
  longer fit this 31 GiB host under .NET 11 RC1, even with fixes 4 and 5
  (which removed roughly 12 GiB from the capture's former profile): the
  engine's own resident state is ~16.5 GiB (measured), the capture's
  remaining structures ~4 GiB, and the WAL frame replay's transient churn
  tips allocations over the available ceiling non-deterministically. The
  same binary passes the identical corpus at 100k (50+ runs) and every
  phase at 1M. The engine-side lazy-body representation named in the
  handoff ("a raw-UTF8 body fast path in StorageLogRecord") is the
  structural fix.
- **Incremental artifacts cost ~3 KB per mutation** (before-images,
  per-publication authority chains, base64 framing): 50,000 mutations → a
  52 MB artifact, and `BackupManager.EncodeLog` holds the entries, the
  whole-log JSON, and the encrypted form simultaneously (~3× peak). Volume
  incremental chains will need streamed log encoding.
- **Compaction cannot publish a corpus above 1,000,000 records**
  (`StorageLimits.MaximumRecordCount` per segment) — 975k live + 25k
  retained tombstones + internal rows already exceeds it. Multi-segment
  compaction output is required for volume corpora.
- **Indexed queries on a freshly restored engine stall behind feed-pump
  snapshot rebuilds**: correct but heavily tailed (p50 5 ms, p99 2.4 s over
  1,000 queries; the phase's summed operation time is dominated by dozens
  of multi-second stalls). The rebuild also materializes the full corpus
  twice (`List<Document>` + `RebuildAsync`'s own copy) — streaming it is
  the fix.
- **Point-read latency is effectively memory access**: 740k ops/s with
  0.003 ms p99 at 1M documents.

## Environment

| Item | Value |
| --- | --- |
| SDK | .NET 11.0.100-rc1 (`global.json` rollForward), net11.0, Release |
| OS | NixOS 26.05, Linux 7.2.5, x64, shared-tenancy VM |
| CPU | Xeon E5-2660 v4; server GC pinned to 8 processors; `nice -n 19` |
| Memory | 31 GB total; sequential engine sessions with LOH compaction between phases |
| Disk | engine roots and object store under `bench/receipts/` |

## Limits

- Single process, single host, local filesystem object store.
- One deterministic corpus shape (lean ~110 B bodies); the 10M e2e report
  from 2026-09-19 covers the codec and PITR-chain behavior at that scale
  under .NET 10.
- Query correctness is verified exactly (count, ids, bodies, versions,
  index-served plans); the query tail latency finding above is from this
  single run.
