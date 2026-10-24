# 0015: Fewer captured bookkeeping rows per write (Q-PARITY)

- Status: proposed (RT2d, 2026-10-24; awaiting human review). **Needs format bump** for options 2 and 3; option 1 does
  not.
- Scope: `src/TerraBase/Engine/Turso` (`TursoStorageTransaction.Merkle.cs`, `.Feed.cs`, `.Repair.cs`, commit path).
- Goal (user decision Q-PARITY, 2026-10-24): a write at concurrency 1 within 10 % of in-process Turso (the vendored driver,
  one upsert per transaction, `synchronous = FULL`).

## Where a write stands

Measured on delta (16 vCPU, NVMe) with `benchmark/dotnet/VsTurso`, engine lane, small (256 B) documents, concurrency 1,
3 alternating repeats. Statements were counted and timed per write with a temporary driver hook (never merged).

| Build | p50 µs | CPU µs/write | transactions/write |
| --- | ---: | ---: | ---: |
| `main` before Q-PARITY | 2,100 | 2,331 | 3 (data, intent record, intent retire) |
| + #612 and 0013f (lingering intent) and the one-write savepoint | 1,153–1,193 | 1,118–1,136 | 1 |
| raw Turso | 862–948 (639–1,052 across runs) | 434–507 | 1 |

After those changes a small write runs one transaction. Its `COMMIT` takes about 780 µs and the other ~22 statements
about 250 µs. The engine's CPU per write is still about 2.2× Turso's. Most of the difference is in the commit: Turso
commits one upserted row, while a TerraBase write changes and captures (in `turso_cdc`, mode `after`) six or seven rows
of five or six tables:

| Row | Why it exists | Read by |
| --- | --- | --- |
| the document row (`"<collection>"`) | the document | clients |
| `_tb_state` | the lattice state, its digest (`h1`, `h2`) and bucket | writes, replication |
| `_tb_merkle` bucket (upsert) | XOR of the bucket's state digests (R9) | anti-entropy only (`TursoStorageTransaction.Repair.cs`) |
| `_tb_feed` | the change feed entry | clients (SSE), shapes, retention |
| `_tb_feed_sequence` | the feed's next number | feed, shapes, retention |
| `commit_offset` | the commit's offset (the commit marker) | replication, acks, shapes |

Capture cannot simply skip these rows: continuous backup restores from captured changes only, so an uncaptured row is
missing from every point-in-time restore (`SuspendCaptureAsync`).

## Options

1. **Merkle buckets computed on demand, no per-write upsert.** Every `_tb_state` row already stores its bucket and
   digest; anti-entropy can fold them when it asks for a bucket's digest (`SELECT bucket, h1, h2 FROM _tb_state` once
   per round, XOR in memory), and `_tb_merkle` becomes a cache refreshed by that pass. Saves one statement (16 µs) and
   one captured row per write. Cost: anti-entropy reads every state row each round (60 s) instead of 4096 buckets; a
   database of N documents pays O(N) per round. No format change if `_tb_merkle` stays as an unused-at-write cache.
2. **Feed sequence in the `commit_offset` row.** One row holds the commit offset and the feed's next number, updated
   by one statement per commit. Saves one UPDATE (7 µs) and one captured row per write. **Format change:** the
   `commit_offset` schema gains a column and `_tb_feed_sequence` goes; every reader of the sequence moves.
3. **Feed rows per commit, not per document.** A commit's feed entry names its documents. This saves a captured row
   per document beyond the first, so it mostly helps batches, not concurrency-1 writes. **Format change**, and the SSE
   feed and shapes read a different shape.

## Recommendation

- Prototype options 1 and 2 on a throwaway branch and A/B them against `main` (3 repeats, same host), measuring the
  commit's share (`COMMIT` µs per write) and the whole write's p50 and CPU.
- Adopt only what moves the write toward the 10 % mark.
- Option 1 can ship without a format bump if anti-entropy's O(N) pass is acceptable for the largest databases; measure
  that pass on a 1M-document database first.
- Option 2 goes to the deploy lane's next batched format bump.
- Option 3 is out of scope for concurrency 1.

## Not changed by this record

- Every write transaction still commits with FULL before it answers (0010); capture stays on for every client and
  system row.
- Replication, acks and the change log are unchanged.
- The lingering intent (0013f) and the local-durable default (0013e) are separate decisions.
