# Stable-80303 proof interrupted by another fabric restart

Acceptance remains incomplete. FRA repeat 1 passed 500 writes locally with attributed pushed notices at 145.500 ms commit→visible p99. Repeat 2 stopped at write 412 after another pushed ts-orch release restarted embedded NATS. No SJC repeats ran.

| Region | Repeat | Completed writes | Local | Pushed pages | Commit→visible p50 ms | p99 ms | Result |
|---|---:|---:|---:|---:|---:|---:|---|
| fra | 1 | 500 | 500 | 500 | 113.468 | 145.500 | PASS |
| fra | 2 (partial) | 411 | 411 | 411 | 113.967 | 260.820 | FAIL / incomplete |

All six nodes passed authenticated internal health/readiness checks on `80303f9d36c24cdea1976225bd234ecc4c40127a`, format `23-41a88901325a`, before the proof. Raw JSONL, trace, health, deployment and before/after service metadata are retained alongside this report. The partial percentile is diagnostic only.

Overall write 912 (repeat 2 write 412) was sent at 17:03:45.946088 UTC, acknowledged at 17:03:46.345673 and visible locally at 17:03:53.576565: **7,230.892 ms after acknowledgement**. It received no attributed revision notice within two seconds after visibility. The orchestrator journal shows pushed-release exits at SJC 17:03:45 and GRU/FRA 17:03:46, followed by NATS readiness at 17:03:48/48/49. The coordinator identified the concurrent ts-orch push as `dad736726`, despite the intended fabric freeze.

Finding and required durable replay: [TerraScale/terrascale#1104](https://github.com/TerraScale/terrascale/issues/1104). The issue includes this incident and the earlier 16:52 restart with a 7,393.317 ms visibility delay.

The synthetic account `lag-stable-1105` was deleted successfully by the coordinator, and both remote credential files were removed. No client conflict retry was added.
