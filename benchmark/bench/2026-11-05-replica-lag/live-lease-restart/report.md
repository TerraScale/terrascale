# Lease-fix proof interrupted by a fabric restart

Acceptance remains incomplete. The first FRA repeat passed 500 writes locally with attributed pushed notices at 140.576 ms commit→visible p99. Repeat 2 stopped at write 372 because its revision notice was missing. Repeat 3 and SJC did not run.

| Region | Repeat | Completed writes | Local | Pushed pages | Commit→visible p50 ms | p99 ms | Result |
|---|---:|---:|---:|---:|---:|---:|---|
| fra | 1 | 500 | 500 | 500 | 113.038 | 140.576 | PASS |
| fra | 2 (partial) | 371 | 371 | 371 | 113.033 | 291.792 | FAIL / incomplete |

The partial percentile is diagnostic only. All six nodes were authenticated through their internal health/readiness endpoints before the run, with format `23-41a88901325a` and revisions containing the initial-lease and WAN lock fixes. GRU ran `ca3063480`; SJC and FRA ran its descendant `20609ac`. Benchmark and deployment evidence are retained alongside this report.

Overall write 872 (repeat 2 write 372) was sent at **16:52:15.770332 UTC**, acknowledged at **16:52:16.119644**, and visible locally at **16:52:23.512961**: **7,393.317 ms after acknowledgement**. The next two seconds produced no attributed revision notice, and the strict benchmark failed. Earlier successful samples are retained in `fra.jsonl.gz`; the failed write's read and timing records are in `fra-trace.jsonl.gz`.

The orchestrator journals establish a concurrent pushed-release restart: SJC exited for the release at 16:52:14, FRA at 16:52:15 and GRU at 16:52:16. Their embedded NATS servers became ready at 16:52:17, 16:52:19 and 16:52:18 respectively (`orch-restart.txt`). Before/after service start timestamps and PIDs independently confirm all three restarts. The missing notice and 7.4-second row delay occurred across this fabric restart. The terrascale push hold did not include ts-orchestrator at that time; the follow-up hold includes both repositories.

Finding: revision notifications must survive fabric restarts through durable replay. A missing notification after an acknowledged write must be replayed when the subscriber reconnects, without waiting for the recovery sweep. The row must remain locally readable and revision attribution must remain per write. The user requested a GitHub issue with this evidence after the next live proof; its link will be recorded here.

Both remote credential files were removed. Account deletion was accepted but remained pending at the first two explicit cleanup retries; this report does not claim it deleted yet.
