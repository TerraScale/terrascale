# API key-watch timer comparison, 2026-10-28

The candidate retains a separate in-flight watch for every request/session and uses PeriodicTimer disposal for
normal completion. Pending waits return false instead of throwing on Task.Delay cancellation. Admission checks,
250 ms polling, send-time two-second positive-cache freshness, Until deadlines and fail-closed refusal remain.
No shared watch/cache, new option, token-retention change, storage change or billing change is introduced.

The original server-over-engine <10% target remains unmet. Hosting attribution excludes socket/header parsing;
it reports per-request engine and overhead wall times, not CPU, and cannot establish transport-inclusive overhead.
CPU and allocation below cover the measured process window (client/server/background), including end-boundary work.

## Method and validation

All arms ran through ts-heavy-any nice -n 19 on delta, affinity CPUs 0–11, .NET 11 Release, workstation concurrent
GC and tiered PGO. Each cell has 10 s warmup and 5 s measurement; seeded 256 B/4 KiB documents, reads at c=1/16.
The same keyed synthetic token path, attribution listener, harness and SDK are used in both arms. The test-only
issuer exercises key authority without production round-trip/network latency. No live tenant was created here.
Three process pairs alternate before/after, after/before, before/after; no arm is discarded.
The runner verifies the complete baseline src tree, distinct server DLLs and byte-identical SDK DLLs.
Baseline production source is 4d2f9c0294bf1f792e78d3a3e29d90e236618052; the only changed production file is
src/TerraBase/Features/UserAuth/KeyAuthority.cs. SDK is d62203970942942811e83851ca68e80a82e80abe.

Targeted Fast KeyRevocationTests passed 9/9 (3.041 s), including established HTTP-feed revocation and status-outage
cases driven by FakeTimeProvider; the Turso driver suite passed 44/44 (14.352 s). No test failed or was skipped.
Both diagnostic E2E Release builds passed with zero warnings/errors. CI still gates the full Fast suite.
Normal-api observer-free harness changes require their own successful builds before reporting their results.

## Diagnostic exception observation

Harness deddf91d4829e7b1ceb6ea8b0a5453f232b8e206 attached a first-chance exception handler process-wide.
Counts include background work/repeated throws. This observer perturbs latency, so these cells are mechanism
evidence, not a production latency speedup claim. In current harness 89ad448bb the handler is restricted to the
explicit api-exceptions lane; normal api reports null for unmeasured exceptions.

All six diagnostic arms passed (one test each), zero skipped tests, 48 cells and zero operation errors.
Keyed API cells observed 1,432,978 cancellation exception events for 1,432,943 hosting requests before,
and zero events for 1,470,731 hosting requests after. Normal-api latency results follow separately.

| Arm | Pair | Lane | Size | c | ops/s | Client p50/p99 µs | CPU µs/op | B/op | OCE | Engine p50 µs | Overhead p50/p99 µs |
| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| before | 1 | engine | small | 1 | 1078553 | 0.7/1.5 | 0.9 | 968 | 0 | — | — |
| before | 1 | api | small | 1 | 7737 | 109.3/324.7 | 203.9 | 30702 | 38689 | 2.9 | 68.7/210.7 |
| before | 1 | engine | small | 16 | 1877609 | 1.5/9.4 | 2.4 | 972 | 0 | — | — |
| before | 1 | api | small | 16 | 35150 | 321.2/2797.5 | 150 | 30364 | 175765 | 3.9 | 79.4/1697.4 |
| before | 1 | engine | medium | 1 | 473781 | 1.7/4.8 | 2.1 | 4812 | 0 | — | — |
| before | 1 | api | medium | 1 | 6579 | 125.7/375.9 | 227.6 | 46580 | 32895 | 3.8 | 81.1/240.5 |
| before | 1 | engine | medium | 16 | 435519 | 4.7/1175.2 | 7.5 | 4815 | 0 | — | — |
| before | 1 | api | medium | 16 | 36426 | 276.8/2607.5 | 175.9 | 45971 | 182142 | 4.4 | 90.5/2132.5 |
| before | 2 | engine | small | 1 | 1074135 | 0.7/1.6 | 0.9 | 969 | 0 | — | — |
| before | 2 | api | small | 1 | 7427 | 108.5/346.1 | 214.1 | 30687 | 37138 | 3.2 | 68.7/215.3 |
| before | 2 | engine | small | 16 | 2037301 | 1.4/8 | 2.2 | 970 | 0 | — | — |
| before | 2 | api | small | 16 | 48394 | 222.5/2334.3 | 144.5 | 30418 | 241981 | 3.7 | 77.5/1890.3 |
| before | 2 | engine | medium | 1 | 543993 | 1.6/2.4 | 1.8 | 4808 | 0 | — | — |
| before | 2 | api | medium | 1 | 7078 | 118.7/359.9 | 218.4 | 46529 | 35391 | 3.8 | 76.2/231.8 |
| before | 2 | engine | medium | 16 | 428397 | 4.8/1173.4 | 7.5 | 4814 | 0 | — | — |
| before | 2 | api | medium | 16 | 37968 | 270.7/2469.9 | 173.8 | 45973 | 189856 | 4.5 | 90/1966.4 |
| before | 3 | engine | small | 1 | 1080170 | 0.7/1.5 | 0.9 | 968 | 0 | — | — |
| before | 3 | api | small | 1 | 7282 | 111.1/332.5 | 217.1 | 30737 | 36411 | 3.1 | 70/211.3 |
| before | 3 | engine | small | 16 | 2057878 | 1.4/7.8 | 2.2 | 968 | 0 | — | — |
| before | 3 | api | small | 16 | 49623 | 217.1/2358.8 | 141.9 | 30434 | 248129 | 3.6 | 76.3/1644.3 |
| before | 3 | engine | medium | 1 | 547734 | 1.6/2.5 | 1.8 | 4808 | 0 | — | — |
| before | 3 | api | medium | 1 | 6182 | 132.2/383.7 | 248.2 | 46662 | 30913 | 4.1 | 85.1/240.5 |
| before | 3 | engine | medium | 16 | 433567 | 4.7/940.1 | 7.5 | 4812 | 0 | — | — |
| before | 3 | api | medium | 16 | 36730 | 275.9/2581.5 | 176.7 | 45984 | 183668 | 4.5 | 89.8/2046.4 |
| after | 1 | engine | small | 1 | 988869 | 0.8/1.8 | 1 | 971 | 0 | — | — |
| after | 1 | api | small | 1 | 6732 | 121.8/376.5 | 208.6 | 30268 | 0 | 3.6 | 75.5/233.1 |
| after | 1 | engine | small | 16 | 1642482 | 1.8/11.1 | 2.7 | 972 | 0 | — | — |
| after | 1 | api | small | 16 | 50962 | 203.6/2449.3 | 133.2 | 29885 | 0 | 3.8 | 74.5/1883.5 |
| after | 1 | engine | medium | 1 | 518372 | 1.6/2.9 | 1.9 | 4810 | 0 | — | — |
| after | 1 | api | medium | 1 | 7509 | 112.3/328.3 | 181 | 46004 | 0 | 3.6 | 71.2/207.5 |
| after | 1 | engine | medium | 16 | 425309 | 4.8/1216.3 | 7.5 | 4811 | 0 | — | — |
| after | 1 | api | medium | 16 | 38519 | 248.4/2666.2 | 163.2 | 45436 | 0 | 4.8 | 86.4/2089.8 |
| after | 2 | engine | small | 1 | 1072166 | 0.7/1.6 | 0.9 | 969 | 0 | — | — |
| after | 2 | api | small | 1 | 7505 | 106.3/334.3 | 188.9 | 30185 | 0 | 3.1 | 66.5/207.7 |
| after | 2 | engine | small | 16 | 1833991 | 1.6/9.4 | 2.5 | 973 | 0 | — | — |
| after | 2 | api | small | 16 | 32445 | 217.1/3563.7 | 129.7 | 29874 | 0 | 3.6 | 72.6/1722.5 |
| after | 2 | engine | medium | 1 | 298655 | 2.2/7.9 | 3 | 4806 | 0 | — | — |
| after | 2 | api | medium | 1 | 8433 | 105/291 | 161.2 | 45933 | 0 | 3.5 | 66.1/162.5 |
| after | 2 | engine | medium | 16 | 405079 | 5.1/1223.3 | 8 | 4802 | 0 | — | — |
| after | 2 | api | medium | 16 | 38499 | 253.3/2605.3 | 162.5 | 45439 | 0 | 4.7 | 86.6/2045.9 |
| after | 3 | engine | small | 1 | 1073383 | 0.7/1.6 | 0.9 | 969 | 0 | — | — |
| after | 3 | api | small | 1 | 7634 | 107.9/342.5 | 184.9 | 30272 | 0 | 3 | 66.9/209.6 |
| after | 3 | engine | small | 16 | 1994370 | 1.4/8.4 | 2.3 | 970 | 0 | — | — |
| after | 3 | api | small | 16 | 49118 | 205.4/2356.3 | 138.1 | 30802 | 0 | 3.6 | 73.7/1777.2 |
| after | 3 | engine | medium | 1 | 528003 | 1.6/2.6 | 1.9 | 4809 | 0 | — | — |
| after | 3 | api | medium | 1 | 6744 | 122.5/411.1 | 201.6 | 46101 | 0 | 3.8 | 78.1/229.6 |
| after | 3 | engine | medium | 16 | 387470 | 5.1/1247.9 | 8.3 | 4801 | 0 | — | — |
| after | 3 | api | medium | 16 | 40035 | 239.3/2592.5 | 158.8 | 45537 | 0 | 4.5 | 84.9/2062.7 |

## Diagnostic host and artifact evidence

Host pressure and concurrent compiler activity disqualify quiet-host certification. No arm was discarded.

| Arm | Pair | UTC start–end | load1 start–end | CPU some avg10 % start–end |
| --- | ---: | --- | --- | --- |
| before | 1 | 2026-10-28T00:57:01Z–2026-10-28T00:59:14Z | 8.24–5.14 | 10.23–6.42 |
| before | 2 | 2026-10-28T01:03:41Z–2026-10-28T01:05:54Z | 8.92–6.62 | 6.51–6.32 |
| before | 3 | 2026-10-28T01:05:55Z–2026-10-28T01:08:08Z | 6.62–5.28 | 6.32–6.56 |
| after | 1 | 2026-10-28T00:59:14Z–2026-10-28T01:01:28Z | 5.14–3.33 | 6.42–6.30 |
| after | 2 | 2026-10-28T01:01:28Z–2026-10-28T01:03:41Z | 3.33–8.92 | 6.30–6.51 |
| after | 3 | 2026-10-28T01:08:08Z–2026-10-28T01:10:21Z | 5.28–3.46 | 6.56–6.14 |

Diagnostic server DLL SHA-256: before `06be276443fe7c1ed7b291c2d2a1fa69d3abb71fdc7ad4cccc225dfce868b6ac`,
after `923fddf28dc4b9a6554fbc9f8c7278ad3385c64d8aca13411db49568ac79c611`.
Raw diagnostic artifacts on delta: `/home/mariogk/remote-builds/terrascale/fix-api-keywatch-timer-3abe240428921dedcdd8d9fd645d2b861e9c2c0be603805186d65566b2ad022e/benchmark/bench/keywatch-pairs-delta` (six `.md/.log/.host`, `vmstat.txt`, source/SDK ids, test/build logs).
