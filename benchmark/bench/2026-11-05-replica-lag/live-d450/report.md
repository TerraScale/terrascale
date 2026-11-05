# Live replica lag on d450cbe3b, 2026-11-05

Overall acceptance failed. SJC completed all three repeats locally below 200 ms p99. FRA stopped at repeat 1 write 312 without its attributed revision notice; repeats 2–3 did not run.

Deployment and benchmark hashes are in `deployment.json`. Writes committed at GRU; the benchmark ran on the reading region over the production public API and observed the local NATS subscription. Strict mode rejects forwarded reads and missing per-write notices. Every completed sample below was local and had pushed page bytes. Percentiles use nearest rank.

| Read region | Repeat | Completed writes | Local | Pushed pages | Commit→visible p50 ms | p99 ms | Max ms | Result |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| sjc | 1 | 500 | 500 | 500 | 107.379 | 141.263 | 344.345 | PASS |
| sjc | 2 | 500 | 500 | 500 | 107.400 | 143.620 | 345.411 | PASS |
| sjc | 3 | 500 | 500 | 500 | 107.348 | 153.987 | 361.330 | PASS |
| fra | 1 (partial) | 311 | 311 | 311 | 114.595 | 293.012 | 538.317 | FAIL / incomplete |

The FRA partial percentile is diagnostic only and is not a 500-write acceptance cell. At write 312 (sent 13:17:07.113034 UTC), repeated reads returned errors before a local successful read roughly 8 s later. No notice for that write arrived during the subsequent 2 s attribution deadline. Both FRA and GRU logged NATS connection timeouts at 13:17:09.246124 and 13:17:10.521163 UTC respectively (`runtime-window.jsonl`). This coincidence points to a fabric interruption; it does not establish the cause of earlier slow samples. Runtime PIDs and deployed revision remained unchanged after the failure.

The account `lag-maintenance-1105` was deleted successfully after one pending-cleanup retry. Both remote credential files were removed and their absence checked. New live runs are held for the announced format-23 reset; reset owner HANDOFF is required before resuming.

Raw JSONL is gzip compressed. Trace records contain timing, revision and read-route evidence, without credentials.
