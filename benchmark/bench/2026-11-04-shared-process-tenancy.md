# Shared-process tenancy (E6, E11)

Commit `403b561d0`, host `mariogk-dev`, 46 CPUs, 3 measured repeats. Produced by `benchmark/scripts/shared-process.sh` (`benchmark/dotnet/SharedProcessBenchmark.cs`), one in-process TerraBase per scenario.

## 1,000 databases in one process

Each database is created, gets a table and a row, is written again and then read back (row count checked). RSS is the whole process after a full GC.

| Run | Writer bound | Open writers | RSS before (MiB) | RSS after (MiB) | Create + first write (s) | Second write p50 (ms) | p99 (ms) |
|---|---|---|---|---|---|---|---|
| 1 bounded | 64 | 64 | 188 | 858 | 59.3 | 41.26 | 299.03 |
| 1 unbounded | 2000 | 1000 | 483 | 1472 | 51.7 | 13.77 | 50.08 |
| 2 bounded | 64 | 64 | 187 | 901 | 55.6 | 40.54 | 319.91 |
| 2 unbounded | 2000 | 1000 | 594 | 1905 | 103.6 | 11.01 | 99.75 |
| 3 bounded | 64 | 64 | 186 | 875 | 48.6 | 33.66 | 71.55 |
| 3 unbounded | 2000 | 1000 | 507 | 3715 | 65.7 | 11.35 | 28.43 |

## Runaway neighbour

Tenant B (Hobby) runs point reads paced at 40 per second (under its 50/s plan rate); tenant A (Hobby) keeps 4 endless recursive queries in flight (its plan concurrency) on a 4-thread storage pool.

| Run | B alone p50 (ms) | B alone p99 (ms) | B with A p50 (ms) | B with A p99 (ms) | A requests | A throttled (429) |
|---|---|---|---|---|---|---|
| 1 | 2.66 | 10.78 | 1.99 | 5.08 | 42073 | 42065 |
| 2 | 2.95 | 5.99 | 2.03 | 7.99 | 41985 | 41977 |
| 3 | 1.61 | 4.95 | 1.86 | 5.43 | 38994 | 38986 |

## Reading

- The unbounded run starts in the same process after the bounded one, so its "before" RSS is higher; compare the
  growth. Bounded: +670 to +714 MiB for 1,000 databases. Unbounded: +989 to +3,208 MiB, and at its shutdown 1,002
  connections were still open after the 10 s grace; the bounded server closed cleanly.
- The bound costs reopens. This workload writes all 1,000 databases round robin, so every second write reopens an
  evicted writer: p50 34–41 ms against 11–14 ms with every writer open. A working set under 64 databases per node
  (`max(64, 8 × storage threads)`) never reopens.
- Runaway neighbour: B's p99 stays at 5–8 ms while A runs (5–11 ms alone). A's endless queries are interrupted at
  Hobby's 1 s timeout and, once its 3 s engine burst is spent, almost every further request is refused with `429`.
  Without the per-tenant worker cap the Fast noisy-neighbour test fails 3 of 3 runs (B's p99 over 400 ms).
