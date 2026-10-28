# .NET parity timing controls, 2026-10-28

Source `740a10792`, stacked on observation controls `08d829fd9`, with
ts-dotnet v0.0.18 at `36d114877a69d18fba33b27ad362bd3e68e9e7ff`.
These are offline controls; no live request, credential or tenant was used.

| Check on beta | Result |
|---|---|
| SDK preparation fixtures | 14 passed |
| Locked restore and Release build | Passed |
| Existing format checks, three projects | Passed |
| Existing plus observation, discovery and window tests | 138 passed, 0 failed, 0 skipped |
| SDK checkout and committed dependency locks after checks | Clean, unchanged |
| Read starts inside 5 s window, finishes at 6 s | Full 6 s latency; throughput denominator 6 s |
| Warmup read finishes inside window | Excluded from measured latency; included in logical starts |
| Drain timeout and interruption | Remaining read canceled and joined; missing latency/throughput null |
| One failed operation with 16 workers | All workers joined; cell rejected |
| 20,000 completed reads | All counted; reservoir limited to 16,384 samples |

Command: `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash
/tmp/perf-api-dotnet-window-qualification-a.sh --repo terrascale --branch
fix/api-dotnet-parity-window`. The retained script runs the existing `make check`
and verifies SDK cleanliness and unchanged locks. Artifacts on beta:
`~/remote-builds/terrascale/fix-api-dotnet-parity-window-883e2719159aab3f5a88eec6e1e71603f002169885ef3ced5dcb95cc9a8c5e84/benchmark/bench/api-dotnet-window-qualification-a/`.
`check.log` SHA-256:
`8a01c036016b2c74d9b8477e7ba8a01128952e5581ff5cbfc2bdde910ae31141`.

The controls use a manually advanced monotonic clock and synchronization
signals, with no wall-clock sleeps. The window requires operations to honor
cancellation. This is not a complete authenticated runner or a performance
comparison; live setup, recovery and full-response validation remain necessary.
