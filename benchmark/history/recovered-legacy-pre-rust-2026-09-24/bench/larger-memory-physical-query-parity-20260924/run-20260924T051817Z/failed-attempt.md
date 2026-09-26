# Failed continuous-ingestion physical-query attempt

This attempt used the frozen byte-cap/gate build, 25-document frames, a 256 MiB zero-swap cgroup, and a 128 MiB GC heap cap. It is separate from the earlier continuous failures and from the periodic close/reopen diagnostic. Exact source and binary hashes are in `run-start.json`; the runner source copy is `Program-attempt.cs.txt` (SHA-256 `16d20e93daefc68b7d7287b2a2950cea10bb8b02c22b6e240589e2d2d4cbb089`).

| Phase | Committed base | Maintenance inserts | Maintenance updates | Watermark | File length | Outcome |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| TerraBase `status` continuous ingestion | 3000 | 100 | 100 | 128 | 337,095,760 B | In-process commits completed; durable reopen threw `OutOfMemoryException` before queries or full post-reopen verification |

The exception stack in `failure.txt` enters `VariableSizeDiskSegment.ReadValue` from `DurableIndexStore.ReadRetainedDocuments` via `HasStaleEntryGeneration`. `ReadRetainedDocuments` builds a list of every decoded body; this workload has about 406 MiB of encoded payload before other document allocations. This is a reproducible larger-than-memory reopen failure, not evidence that durable recovery or query correctness passed. The `profile.segment` TerraBase lane was never reached.

The last progress sample records cgroup `memory.peak=268,435,456` bytes and `memory.events` as `low 0; high 0; max 16362; oom 0; oom_kill 0; oom_group_kill 0; sock_throttled 0`. The process exited 134. The collected scope disappeared after exit, so final cgroup counters are unavailable and the sampled counters are not claimed as final. The unfinished physical data remains under `data/` for reproduction.

`maximumObservedMutableWalBytes` in `progress.json` actually sums all segment WAL files, including read-only WALs. The on-disk `0.json` read-only count does not reliably reflect the live in-memory backlog. Neither value proves the mutable WAL or backlog bound for this attempt.
