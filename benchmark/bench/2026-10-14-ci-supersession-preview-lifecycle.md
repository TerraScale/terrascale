# Main supersession and preview lifecycle — 2026-10-14

PR [#275](https://github.com/TerraScale/terrascale/pull/275) merged `67364c42` at 05:24:43Z. These measurements qualify workflow cancellation and a real ready-then-close preview. The completed after measurement of merge → both products deployed in all three regions remains outstanding.

| Measurement | Observed result |
|---|---|
| Superseded main cancellation after next push | 1 s: push 05:26:29Z, old run cancelled 05:26:30Z |
| Exact-tree Fast gate | Unchanged; nine freshness/error/exact-tree controls passed in required `test` |
| Preview shape | One shared CPU, 1,024 MB RAM, 1 GB encrypted volume |
| Preview readiness | Both auth and TerraBase `/readyz` HTTP 200 at PR merge-tree revision `41477205` |
| Auth secret mappings | Master key, audit signing key and peer secret mapped to auth container |
| Close → Tigris bucket deleted | 76.82 s |
| Close → app deleted | 79.77 s; subsequent org app list confirms absence |
| Cleanup mechanism | Automatic `pull_request: closed` workflow; no manual Fly deletion |

Cancellation proof: [superseded run](https://github.com/TerraScale/terrascale/actions/runs/38516490807) and [new main run](https://github.com/TerraScale/terrascale/actions/runs/38516648367). Cleanup proof: [run 38516493385](https://github.com/TerraScale/terrascale/actions/runs/38516493385).

## PR job times

These are actual job wall-clock durations from [run 38513716906](https://github.com/TerraScale/terrascale/actions/runs/38513716906), excluding queue time.

| Job | Result | Start UTC | End UTC | Seconds |
|---|---|---|---|---:|
| changes | success | 2026-10-14T04:54:27Z | 2026-10-14T04:54:35Z | 8 |
| deploy | skipped | 2026-10-14T04:53:14Z | 2026-10-14T04:53:14Z | 0 |
| e2e | skipped | 2026-10-14T04:53:14Z | 2026-10-14T04:53:14Z | 0 |
| libs | success | 2026-10-14T04:56:59Z | 2026-10-14T04:59:28Z | 149 |
| terrabase | success | 2026-10-14T04:58:03Z | 2026-10-14T05:07:36Z | 573 |
| auth | success | 2026-10-14T04:58:41Z | 2026-10-14T05:07:11Z | 510 |
| slow | skipped | 2026-10-14T04:54:36Z | 2026-10-14T04:54:35Z | — |
| test | success | 2026-10-14T05:08:36Z | 2026-10-14T05:08:54Z | 18 |
| preview | success | 2026-10-14T05:16:01Z | 2026-10-14T05:22:58Z | 417 |

`test` passed at 05:08:54Z, 940 seconds after the PR run was created at 04:53:14Z. Slow and live E2E were skipped on this PR, so this run provides no Slow/live-production success claim. Preview finished at 05:22:58Z.

A newer push may interrupt an active rollout. Already-issued Fly operations can finish during cancellation; the replacement main requires Fast for its exact tree and retains all regional storage/live-health checks. The separate close/build race correction is still pending.

Metadata and sampled readiness responses are in the adjacent JSON. No secret values are included.
