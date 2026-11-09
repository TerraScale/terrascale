# One process, many databases

One TerraBase process per node serves every database on it. Handles, engine time and I/O workers are bounded per
node and shared fairly between organizations.

## Open handles

- Each client database keeps at most one writer connection open. The engine bounds open writers at
  `max(64, 8 × storage threads)`. Using a writer moves its database to the back of a least recently used list; past
  the bound the front database's writer is checkpointed (the same checkpoint shutdown runs) and closed, together with
  its idle readers. A writer that is busy, being rewritten or has open readers stays open until its next use.
- The next write reopens the writer and reattaches the revision ledger, as after a restart. No data or acknowledged
  write is lost: eviction only closes connections.
- Idle reader connections stay bounded as before (`4 × CPU` engine-wide, `CPU` per database).
- Metrics: `terrabase.storage.open_writers` (gauge), `terrabase.storage.writers_evicted` (counter).

## Per-organization governance

| Control | Hobby | Starter | Pro / Team | Enterprise |
|---|---|---|---|---|
| Statement timeout | 1 s | 2 s | 5 s | 5 s (request bound) |
| Engine time replenished per second | 0.5 s | 1 s | 2 s | unlimited |
| Engine time burst | 3 s | 6 s | 15 s | unlimited |

- Every SQL statement runs with a native timeout of min(the plan's statement timeout, the request's remaining execution,
  the transaction's remaining execution) and answers `query_canceled` (57014) when it passes it: `the request exceeded its
  5 s time limit; split the batch or narrow the query` when the request deadline tripped, `query too expensive: it
  exceeds the work budget` when the engine's work or scan budget did. The 5 s request and transaction budgets count only
  statement execution: admission, session-gate and storage-queue waits pause them, and each such wait keeps its own
  bounded refusal (`429 tenant_busy`, `503`/busy unavailable, or the storage queue's `StorageBusyException`). An open
  transaction that runs no statement for 5 s is still rolled back by the idle timer.
- Each statement's execution time is charged to its organization. An organization whose engine time is spent gets
  `429 tenant_busy` with `Retry-After` (PostgreSQL wire: `53300`) until it replenishes.
- One organization runs on at most `max(1, min(threads / 2, threads − 2))` storage I/O workers at once, so a runaway
  tenant always leaves workers to the others. Writes run on each database's own write lane.
- Rate, concurrency, memory and result caps are unchanged ([plan resource policy](qos.md)): results hold at most
  10,000 rows and 8 MiB ([native SQL](sql/native-sql.md)).

Proof: `tests/TerraBase.Slow/Tenancy/SharedProcessTests.cs` (1,000 databases), `tests/TerraBase.Fast/Tenancy/SharedProcessTests.cs` (noisy neighbour),
benchmark results in `benchmark/bench/2026-11-04-shared-process-tenancy.md`.
