# Historical scale ladder plan — all database functions at 10k / 100k / 1M / 10M / 100M (2026-09-20)

Status: the 10k, 100k, and 1M rungs have historical receipts; the 10M rung
did not complete and the 100M rung has not been run. This document records the
planned contract, not current-source qualification.

The planned contract uses one harness and five scales: every function (insert, update,
delete, scan, point read, indexed query, relationships, backup, PITR and
final restore, compaction) exercised at each rung and verified exactly —
survivors byte-identical, deleted documents provably absent, relationship
sets derivable and complete. Rungs ran sequentially on one host through a
workflow orchestrator; the completed rungs wrote immutable receipts.

Receipts: `bench/receipts/e2e-ladder-{10k,100k,1m}-20260920/` are historical.
The 10M rung left `bench/receipts/e2e-ladder-10m-20260920/` with only a source
engine and no completed receipt because the run died in full backup. No 100M
receipt exists.

These local-filesystem receipts do not establish S3 restore activation. Current
source behavior keeps same-identity S3 activation fenced for distributed writer
safety, while an offline fresh-identity S3 restore is a separate supported path
with a new database identity; focused restore tests cover that path, not this
ladder.

Functions per rung, in phase order: batched insert, relationship write
(batched documents + schema-driven constraint rebuild), read-back verify,
point-read benchmark, relationship verify (exact related sets, bounded
traversal, restrict-delete rejection), full backup, bulk updates, bulk
deletes, incremental backup, full-boundary restore + verify, interior PITR
restore + verify, final-chain restore + verify + absence sweep,
relationship re-verify on every restored engine (rebuild + reads),
automatic index build + activation, 1,000-or-fewer indexed range queries
(all index-served, all exact), compaction where the corpus fits the
per-segment record cap.

## Results

| Metric | 10k | 100k | 1M | 10M |
| --- | ---: | ---: | ---: | ---: |
| Bulk write | 140.3 s (cold-start rung) | 4.40 s / 22,707 docs/s | 20.2 s / 49,496 docs/s | 4m 10s (log) |
| Relationship write (10k rel default) | 139.5 s | 1.2 s | 1.0 s | 5m 07s (log) |
| Full backup | 0.77 s | 2.91 s | 26.0 s | **OOM** |
| Full artifact | 1.14 MB (114 B/doc) | 11.1 MB (111 B/doc) | 110.1 MB (110 B/doc) | — |
| Incremental artifact | 4.13 MB | 20.7 MB | 51.9 MB | — |
| Full-boundary restore | 0.74 s | 4.04 s | 44.3 s | not reached |
| PITR restore (entries) | 0.61 s (2) | 4.34 s (2) | 35.3 s (5) | not reached |
| Final restore | 0.70 s | 4.57 s | 34.8 s | not reached |
| Point reads | 879k ops/s, p99 0.003 ms | 463k ops/s, p99 0.002 ms | 761k ops/s, p99 0.003 ms | not reached |
| Indexed queries (p50/p95/p99) | 404/474/535 ms | 5.2/5,560/6,076 ms | 5.8/15.7/222 ms | not reached |
| Relationship verify | 14.3 s | 65.9 s | 65.3 s | 5m+ (log) |
| Compaction | 0.35 s, verified | 1.85 s, verified | skipped (>1M-record cap) | not reached |

**Verification counts, all exact:** read-back = corpus at every rung;
full-boundary restore = corpus; PITR restore = corpus with updates only;
final restore = corpus − deletes exact **and every deleted id reads null**
(2,000 / 10,000 / 25,000); relationship related-sets exact and restrict
deletes rejected on the source and on **all three restored engines** per
rung; compaction verify exact where compaction ran (10k, 100k). The 1M
compaction row is the documented capacity skip: 975k live + 25k tombstones
+ relationship + internal rows exceed the 1,000,000-record per-segment
limit, so the engine was left untouched and the row carries the
already-verified final-restore count.

## The walls, measured

- **10M rung — host memory in the backup capture (.NET 11 RC1).** Bulk
  write (10M docs, ~5 min) and relationships passed; the run died in
  `CreateFullBackupAsync` with `OutOfMemoryException`. Measured engine
  residency: 1,771.67 B/document (each body held twice plus dictionaries
  and sequence indexes) → ~16.5 GiB at 10M, plus ~4 GiB capture structures
  and WAL replay churn on a 31.3 GiB host. Engine-only ceiling ≈ 18.97M
  documents; the practical backup ceiling is below 10M. The same binary is
  green at 1M (three restores verified). First structural fix: the
  raw-UTF8 body fast path in `StorageLogRecord` (removes the duplicate
  body, the dominant per-document term). The 2026-09-19 10M e2e receipts
  (net10) remain the 10M-scale evidence for the insert/scan/backup/restore
  chain.
- **100M rung — unqualified and not run.** 100M × 1,772 B ≈
  165 GiB of engine residency vs 31.3 GiB here (5.3× over). Even after the
  body fast path, the per-document dictionaries and sequence indexes scale
  linearly; 100M needs those restructured (or a ~200 GiB host). This rung
  is bounded analytically from the measured per-document cost rather than
  attempted.

## Findings that need product attention

1. **The DocumentStore single-document constraint path is quadratic in
   corpus size.** Writing 10,000 relationships one document at a time took
   2 s at a 20k corpus and was still running after 15 minutes at 100k
   (flat 730 MB RSS, 136% CPU — CPU-bound, not memory). The e2e now writes
   relationship documents as engine batches and derives edges with one
   `RebuildConstraintRecordsAsync` (10k relationships: ~1 s); the
   single-doc path itself remains the slow way and is what
   `relationship-improvement-20260919.md` should target next.
2. **Indexed-query tails are bimodal and scale badly.** p50 is a flat
   5–6 ms from 100k up, but a subset of queries stalls for seconds (p95
   5.6 s at 100k; p99 2.4 s at 1M in the earlier run). The stalls follow
   the feed pump's snapshot rebuilds on restored engines; the rebuild also
   materializes the corpus twice.
3. **Incremental artifacts cost ~2 KB per mutation** — ~18× the full
   backup's per-document bytes (before-images, per-publication authority
   chains, base64 framing). At small mutation-to-corpus ratios the
   incremental exceeds the full artifact (3.6× at 10k).
4. **Restored engines do not carry relationship (constraint) records**;
   the schema-driven rebuild is required post-restore. It is measured in
   the receipts (~1 s per 10k relationships) but it is a real restore
   contract to document.
5. **First-rung warmup dominates small-scale numbers** (10k rung: 140 s
   bulk write, 404 ms query p50 — cold JIT/fsync effects plus a smaller
   batch size; treat the 10k rung's absolute times as cold-start, its
   verification as the signal).

## Environment

.NET 11.0.100-rc1, net11.0 Release, NixOS 26.05, Xeon E5-2660 v4, 31 GiB,
server GC pinned to 8 processors, `nice -n 19`, sequential rungs, LOH
compaction between phases. Point-read and query throughput figures are
derived from summed per-operation time (harness verification excluded).

## Limits

Single process, single host, local-filesystem object store; one
deterministic corpus shape (~110 B bodies) plus the users/orders
relationship corpus; the 10M rung's failure is characterized but its
backup phases are unqualified under .NET 11 on this host.
