# Cross-machine feed cursor validation: one million events

Measured on beta, 2026-10-13, .NET 11.0.0-rc.1.26425.128 Release, vendored Turso driver/core 0.8.2,
Linux 7.2.10, AMD Ryzen 5 PRO 6650H. Each measured process had `MemoryMax=512M`, `MemorySwapMax=0`,
`CPUQuota=200%` and `taskset -c 0,1`, at nice 19. The executable recorded actual `memory.max=536870912`,
`memory.swap.max=0`, `cpu.max=200000 100000` and `Cpus_allowed_list: 0-1` before measuring. These are local resource limits, not cloud measurements.

These timings precede B1's persisted sequence and commit-offset columns. The current fixture includes those columns
and advances their counters; performance of the combined storage tree has not been remeasured.

| implementation | proof build ms | cross-machine validation median ms | validation range ms | peak process RSS MiB | allocated MiB per validation | systemd memory peak (displayed) | fixture file MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| retained-prefix scan, `b93fc33a` | 1735.802 | 1543.213 | 1520.420–1562.402 | 116.574 | 495.940 | 88.7M | 72.773 |
| persisted running hashes | 11.329 | 0.290 | 0.265–0.554 | 86.086 | 0.023 | 36.3M | 105.215 |

| iteration | scan ms | persisted ms |
| ---: | ---: | ---: |
| 0 | 1520.420 | 0.554 |
| 1 | 1529.028 | 0.304 |
| 2 | 1559.765 | 0.290 |
| 3 | 1543.213 | 0.265 |
| 4 | 1562.402 | 0.286 |

`FeedCursorBench` calls the real engine's `ReadFeedAsync` on a reopened, persisted tenant database. The fixture has
one origin and 1,000,000 retained events, distinct stamps and synthetic 26-character document IDs. Generation uses
bounded inserts into the real feed schema outside the measured process. Document/state bodies are absent because
the measured cursor has already consumed every event: this isolates validation and the empty next page. Startup,
HTTP, network transit, replication transport and fixture generation are excluded from the timers. Proof creation
runs first; validation then repeats five times on the same transaction and a different issuing replica ID. Every
result must be unexpired with no events. This is a warm local read measurement, not a production latency guarantee.

The scan streams event identities with bounded live memory but allocates approximately 496 MiB cumulatively per
resume. Persisting each row's 32-byte running hash and the last deleted hash at the origin's retention floor makes
validation and head proof creation indexed reads. No retained prefix is materialized. The fixture grows about
32.4 MiB, including storage overhead; writes now calculate and persist the hash. An out-of-order join rehashes
successors in bounded pages inside its write transaction. Write throughput and the cost of a million-row late-join
suffix were not measured here.

RSS includes mapped/shared pages, whereas systemd reports cgroup memory accounting; the two peak numbers have
different accounting scopes. The five timing samples are descriptive, not confidence intervals.

Correctness validation: targeted Fast `DatabaseChangesTests`, **26/26 passed**, zero skipped; driver **6/6 passed**.
Both original repair variants and all six matching-history region pairs remain covered. New variants check an
out-of-order join and a cursor created after retention trimming. Persisted chains retain deleted-history differences,
so conservative 410 responses can continue until the affected origin is forgotten. This storage format change needs
a reviewed greenfield cutover; no migration or deployment was performed.

Reproduce the persisted implementation with the commands in
[`FeedCursorBench/README.md`](../dotnet/FeedCursorBench/README.md). The scan row was measured before the persisted
columns existed, on `b93fc33a`, using the same fixture/timers and a seed insert without the prefix column.
