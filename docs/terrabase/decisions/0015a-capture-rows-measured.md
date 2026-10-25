# 0015a: Fewer captured rows per write, measured (Q-PARITY)

- Status: proposed (RT2d, 2026-10-25; awaiting human review). **Needs format bump** for option 2 of
  [0015](0015-fewer-captured-rows-per-write.md); the other cuts below need none.
- Measures the options of [0015](0015-fewer-captured-rows-per-write.md) on `main` 7755bc02 (after #612, #638, #644,
  #645 and #647) and corrects its premise.
- Goal (user decision Q-PARITY, 2026-10-24): a write at concurrency 1 within 10 % of in-process Turso.

## Where a write stands

Measurement setup:
- delta (16 vCPU, NVMe), idle, running `benchmark/dotnet/VsTurso` with writes at concurrency 1.
- 5 alternating repeats, reported as median (min–max).
- The engine row is the "after" arm of the #647 A/B, whose tree is identical to `main` 7755bc02.

| Size | Lane | p50 µs | CPU µs/write | Allocated B/write | Gap (p50) |
| --- | --- | ---: | ---: | ---: | ---: |
| small (256 B) | raw Turso | 953 (617–1,044) | 527 | 4,990 | |
| small (256 B) | engine | 1,068 (1,043–1,113) | 1,030 | 70,650 | **+12 %** |
| medium (4 KiB) | raw Turso | 960 (911–1,008) | 565 | 20,032 | |
| medium (4 KiB) | engine | 1,154 (1,108–1,226) | 1,177 | 175,716 | **+20 %** |

To reach 10 %, a small write must lose about 20 µs and a medium write about 100 µs.

## 0015's premise does not hold

0015 assumed the commit was the gap, because the engine captures six or seven bookkeeping rows against Turso's one.
Per-statement timings show otherwise. They come from a temporary driver hook (never merged), measured on delta in one
run per lane.

| Lane | `COMMIT` µs/write | Other statements | Other µs/write |
| --- | ---: | ---: | ---: |
| raw Turso, small | 695 | 2 (`BEGIN IMMEDIATE`, upsert) | 55 |
| engine, small | 795 | 18 | 153 |
| raw Turso, medium | 958 | 2 | 150 |
| engine, medium | 838 | 18 | 156 |

- The engine's `COMMIT` is sometimes cheaper and sometimes dearer than Turso's. Across this run and the earlier #638
  run (engine 813 µs against Turso's 942 µs), commit time follows the host's sync timing, not the number of captured
  rows. It is not a measurable share of the gap.
- 0015 option 1 (Merkle buckets computed on demand) was prototyped and A/B'd against `main` at #638. It showed no gain
  within noise, while removing one statement and one captured row.
- What remains:
  - about 100 µs of small bookkeeping statements (none over 20 µs);
  - managed work that grows with document size: the engine's CPU rises 147 µs from small to medium, against Turso's 38 µs.

The statements a small write runs besides `COMMIT` and the document `UPDATE` (12 µs), in µs per write:

| Statement | µs | Kind |
| --- | ---: | --- |
| `_tb_merkle` bucket upsert | 19.9 | captured row |
| `_tb_feed` insert | 19.1 | captured row |
| `_tb_state` update | 14.6 | captured row |
| `commit_offset` update | 13.9 | captured row |
| `BEGIN IMMEDIATE` | 13.4 | (Turso runs it too) |
| `PRAGMA schema_version` | 9.8 | read |
| `_tb_feed` previous prefix | 9.1 | read |
| `_tb_feed_sequence` update | 6.2 | captured row |
| old document size (three reads) | 16.2 | read |
| `_tb_unique_conflicts` delete | 5.7 | write, usually no row |
| `_tb_gc_floor`, `_unique`, `_tb_feed_floor`, `_tb_feed_sequence`, `commit_offset` reads | 12.8 | read |

## Options and expected gains

1. **0015 option 2, the feed sequence in the `commit_offset` row (Needs format bump).**
   - It drops the `_tb_feed_sequence` update and read (8 µs) and one captured row.
   - Expected gain: about 8 µs per write, under 1 % of p50. The captured row adds nothing measurable, as option 1
     showed.
   - Worth batching with the next format bump only for the simpler schema; on its own it does not reach 10 %.
2. **Reads a single writer can keep (no format change).**
   - The single-row reads (`commit_offset`, `_tb_feed_sequence`, `_tb_gc_floor`, `_tb_feed_floor`, `_unique`) can be
     kept in the store across transactions. The store already does this for its collection rows (#644).
   - Expected gain: about 13 µs.
3. **One old-size read instead of three, and no `_tb_unique_conflicts` delete for a collection without unique
   constraints (no format change).**
   - Expected gain: about 12–17 µs.
4. **The per-write `PRAGMA schema_version` and previous-prefix reads (no format change).**
   - Both are answerable from state the writer already holds.
   - Expected gain: up to 19 µs; how much of it is reachable needs a prototype.
5. **Size-dependent managed work (no format change).**
   - This is the medium gap: canonical materialization, digests, the schema check and the per-write JSON and UTF-16
     copies.
   - Needs a CPU profile of medium writes before an estimate.

Options 2–4 add up to about 45 µs, which brings a small write to roughly +7 % if the gains are additive. A medium
write stays at about +15 % until option 5 is profiled and cut. Capture stays on for every row: continuous backup
restores only from captured changes.

## Recommendation

- Do options 2–4 next as ordinary PRs, each with a 5-repeat A/B.
- Profile medium writes for option 5.
- Schedule option 1 (format bump) with the deploy lane's next batched bump. It is tidy-up, not the route to parity.
- Leave 0015 option 3 (feed rows per commit) out of scope for concurrency 1.

## Not changed by this record

- Every write still commits with FULL before it answers (0010), and capture stays on for every client and system row.
- Replication, acks, the change log and the storage format are unchanged until the batched bump.
