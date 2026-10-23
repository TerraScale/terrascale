# Slow count/cache evidence — 2026-10-22

Final tested SHA: `a4113a2fe1b9732f1007a86d15fc7e13b128486a`, branch `fix/slow-query-evidence`.
Baseline SHA: `e7fa366ddb289f5612c93d316b316fb92c568151`, current main when reproduction began.
Pristine baseline ran on `mariogk-dev`; final repeats ran on authorized dev host `delta`.
Final environment: NixOS linux-x64, SDK `11.0.100-rc.1.26425.128`, TUnit `1.73.19.0`.
Default parallel execution and isolated real listeners were retained; no assertions were weakened.

| Case | Pristine baseline | Final passes | Final duration range |
| --- | --- | --- | --- |
| #155 bounded query count | Timeout, 601.006 s | 20/20 | 5.087–18.637 s |
| #157 bounded assert_count | Pass, 82.758 s | 20/20 | 19.652–50.242 s |
| #161 cache entry bound | Pass, 19.654 s | 20/20 | 6.792–21.570 s |
| Public overflow count, true | Pass, 11.935 s | 20/20 | 2.827–10.036 s |
| Public overflow count, false | Pass, 11.935 s | 20/20 | 2.827–10.041 s |

Twenty fresh runner processes each produced exactly these five passed TRX results: **100 passed, 0 failed, 0 skipped**.
Each round verified the exact SHA and clean checkout. Final TRX, logs and `verified.json` agree; `summary.json` records 20 per case.
Classify #157/#161 as stale against this filtered reproduction; historical whole-Slow load and #161's 4092-entry failure remain unproven.
#155's fixture now seeds 100,000 healthy current-format rows using the existing snapshot helper and canonical state/digests.
All five HTTP count requests, the overflow HTTP PUT and assertions remain byte-for-byte unchanged.
Production `src/TerraBase` tree is identical: `d1f2f2931dd72b7489e0a85329121c2e6af1ffc7`; no API/storage-format change.

Separate, temporary phase instrumentation on dev's immutable baseline checkout is **not** part of pristine repeat evidence.
Instrumented HTTP baseline passed in 493.632 s: setup 486.866113 s; five count queries totaled 3.789942 s.
The pristine baseline timeout stack identifies only the timeout handler; its exact phase cannot be recovered.
Instrumented superseded `38a96951` batch setup timed out at 600.044 s: 97,000 rows at 597.188951 s, zero query starts.
Its pristine run had two complete green five-case rounds; round 3 was intentionally stopped and does not count toward the final 20.
These observations establish setup's dominant cost and a setup timeout in the batch candidate, not the original HTTP timeout's phase.
No separate snapshot phase timing was taken; final durations include setup, queries and teardown, on a different host.

Final invocation used SSH delta with invocation-only `TS_HEAVY_SLOTS=4 TS_HEAVY_HOST=delta`, then `ts-heavy-any bash -c <retained-script> --repo terrascale --branch fix/slow-query-evidence`.
The script built once and repeated the following command 20 times, retaining a separate results directory each time:
```bash
nice -n 19 make terrabase-build CONFIGURATION=Release
nice -n 19 make -o terrabase-build terrabase-test-slow CONFIGURATION=Release TEST_ARGS="-- --treenode-filter /*/*/*BoundTests/* --minimum-expected-tests 5 --output detailed --no-ansi --report-trx --results-directory $result/results"
```
Baseline used the same Make target with `/*/*/CountBoundTests/*` (minimum 4), then `/*/*/CacheEntryBoundTests/*` (minimum 1).
Raw evidence: `delta:/tmp/wrk-count-cache-a4113a2f-repeat20/`; local copies and original dev evidence: `/home/mariogk/.local/state/terrascale/slow-query-count-cache/2026-10-22/`.
The local root retains the exact repeat script, baseline/repeat TRX and logs, diagnostic patches/source hashes and `SHA256SUMS`.
Local manifest SHA-256: `47197b3fd692695f7a019a970673e3a9f6ddf38f5cf4962b8e50f2a3ffb8be9a`; all copied final evidence hashes matched delta.
HTTP diagnostic patch SHA-256: `4a84fc09cbe0542fd60bb8bc16b320026d721216b52bce077d7e15a79286737c`.
Batch diagnostic patch SHA-256: `5f5cbac974b0f6562559b5a3de67030e58e48e2774329de64811f10c2e6b50a4`; temporary source was restored.
Full Slow, Fast and CI were not run here; HTTP bulk-write performance and original whole-suite failures remain unverified. Parent owns Git/PR/issue actions.
