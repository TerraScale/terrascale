# FRA read forwarded during the 55fd rollout

Acceptance is incomplete. FRA repeat 1 passed 500 local writes/notices at 166.975 ms commit→visible p99. Repeat 2 stopped at write 94 after a successful read was served by GRU. No SJC repeats ran.

| Region | Repeat | Completed writes | Local | Pushed pages | Commit→visible p50 ms | p99 ms | Result |
|---|---:|---:|---:|---:|---:|---:|---|
| fra | 1 | 500 | 500 | 500 | 113.364 | 166.975 | PASS |
| fra | 2 (partial) | 93 | 93 | 93 | 113.438 | 297.854 | FAIL / incomplete |

Overall write 594's first read began at **17:12:43.955761 UTC**, took 1,169.444 ms and returned the row with `X-TerraBase-Machine: ts-gru-1`; the strict benchmark rejected it as nonlocal. Its revision notice arrived at 17:12:43.959486. The partial percentile is diagnostic only.

Before the run, GRU/FRA served `80303f9d3` and SJC served `55fd5cb60`. The production TerraBase source diff between those revisions is empty, but the rollout was still active. All six nodes reported `55fd5cb60` afterward. The orchestrator PIDs/start timestamps stayed unchanged throughout this run, distinguishing this event from the two NATS restart incidents tracked in [#1104](https://github.com/TerraScale/terrascale/issues/1104).

The coordinator attributes the timing to the 55fd rollout reaching FRA/GRU. The next proof requires all six nodes on 55fd and no rollout running. A forward or missed notice in that stable window must be treated as a separate server bug rather than retried away. No client conflict retry was added.

The remote credentials were removed by the coordinator. Account deletion completed successfully on the fourth explicit cleanup retry at 17:20 UTC.
