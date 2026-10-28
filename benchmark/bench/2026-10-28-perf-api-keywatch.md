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
Both normal-api E2E Release builds also passed with zero warnings/errors; all six normal arms passed.

## Diagnostic exception observation

Harness deddf91d4829e7b1ceb6ea8b0a5453f232b8e206 attached a first-chance exception handler process-wide.
Counts include background work/repeated throws. This observer perturbs latency, so these cells are mechanism
evidence, not a production latency speedup claim. In current harness 89ad448bb the handler is restricted to the
explicit api-exceptions lane; normal api reports null for unmeasured exceptions.

All six diagnostic arms passed (one test each), zero skipped tests, 48 cells and zero operation errors.
Keyed API cells observed 1,432,978 cancellation exception events for 1,432,943 hosting requests before,
and zero events for 1,470,731 hosting requests after. Normal-api latency results follow separately.

Triplets below are repeats 1, 2, 3 in that order; every measured diagnostic cell is retained.

| Arm | Lane | Size | c | ops/s | Client p50/p99 µs | CPU µs/op | B/op | OCE | Engine p50 µs | Overhead p50/p99 µs |
| --- | --- | --- | ---: | --- | --- | --- | --- | --- | --- | --- |
| before | engine | small | 1 | 1078553, 1074135, 1080170 | 0.7/1.5, 0.7/1.6, 0.7/1.5 | 0.9, 0.9, 0.9 | 968, 969, 968 | 0, 0, 0 | — | — |
| before | engine | small | 16 | 1877609, 2037301, 2057878 | 1.5/9.4, 1.4/8, 1.4/7.8 | 2.4, 2.2, 2.2 | 972, 970, 968 | 0, 0, 0 | — | — |
| before | engine | medium | 1 | 473781, 543993, 547734 | 1.7/4.8, 1.6/2.4, 1.6/2.5 | 2.1, 1.8, 1.8 | 4812, 4808, 4808 | 0, 0, 0 | — | — |
| before | engine | medium | 16 | 435519, 428397, 433567 | 4.7/1175.2, 4.8/1173.4, 4.7/940.1 | 7.5, 7.5, 7.5 | 4815, 4814, 4812 | 0, 0, 0 | — | — |
| before | api | small | 1 | 7737, 7427, 7282 | 109.3/324.7, 108.5/346.1, 111.1/332.5 | 203.9, 214.1, 217.1 | 30702, 30687, 30737 | 38689, 37138, 36411 | 2.9, 3.2, 3.1 | 68.7/210.7, 68.7/215.3, 70/211.3 |
| before | api | small | 16 | 35150, 48394, 49623 | 321.2/2797.5, 222.5/2334.3, 217.1/2358.8 | 150, 144.5, 141.9 | 30364, 30418, 30434 | 175765, 241981, 248129 | 3.9, 3.7, 3.6 | 79.4/1697.4, 77.5/1890.3, 76.3/1644.3 |
| before | api | medium | 1 | 6579, 7078, 6182 | 125.7/375.9, 118.7/359.9, 132.2/383.7 | 227.6, 218.4, 248.2 | 46580, 46529, 46662 | 32895, 35391, 30913 | 3.8, 3.8, 4.1 | 81.1/240.5, 76.2/231.8, 85.1/240.5 |
| before | api | medium | 16 | 36426, 37968, 36730 | 276.8/2607.5, 270.7/2469.9, 275.9/2581.5 | 175.9, 173.8, 176.7 | 45971, 45973, 45984 | 182142, 189856, 183668 | 4.4, 4.5, 4.5 | 90.5/2132.5, 90/1966.4, 89.8/2046.4 |
| after | engine | small | 1 | 988869, 1072166, 1073383 | 0.8/1.8, 0.7/1.6, 0.7/1.6 | 1, 0.9, 0.9 | 971, 969, 969 | 0, 0, 0 | — | — |
| after | engine | small | 16 | 1642482, 1833991, 1994370 | 1.8/11.1, 1.6/9.4, 1.4/8.4 | 2.7, 2.5, 2.3 | 972, 973, 970 | 0, 0, 0 | — | — |
| after | engine | medium | 1 | 518372, 298655, 528003 | 1.6/2.9, 2.2/7.9, 1.6/2.6 | 1.9, 3, 1.9 | 4810, 4806, 4809 | 0, 0, 0 | — | — |
| after | engine | medium | 16 | 425309, 405079, 387470 | 4.8/1216.3, 5.1/1223.3, 5.1/1247.9 | 7.5, 8, 8.3 | 4811, 4802, 4801 | 0, 0, 0 | — | — |
| after | api | small | 1 | 6732, 7505, 7634 | 121.8/376.5, 106.3/334.3, 107.9/342.5 | 208.6, 188.9, 184.9 | 30268, 30185, 30272 | 0, 0, 0 | 3.6, 3.1, 3 | 75.5/233.1, 66.5/207.7, 66.9/209.6 |
| after | api | small | 16 | 50962, 32445, 49118 | 203.6/2449.3, 217.1/3563.7, 205.4/2356.3 | 133.2, 129.7, 138.1 | 29885, 29874, 30802 | 0, 0, 0 | 3.8, 3.6, 3.6 | 74.5/1883.5, 72.6/1722.5, 73.7/1777.2 |
| after | api | medium | 1 | 7509, 8433, 6744 | 112.3/328.3, 105/291, 122.5/411.1 | 181, 161.2, 201.6 | 46004, 45933, 46101 | 0, 0, 0 | 3.6, 3.5, 3.8 | 71.2/207.5, 66.1/162.5, 78.1/229.6 |
| after | api | medium | 16 | 38519, 38499, 40035 | 248.4/2666.2, 253.3/2605.3, 239.3/2592.5 | 163.2, 162.5, 158.8 | 45436, 45439, 45537 | 0, 0, 0 | 4.8, 4.7, 4.5 | 86.4/2089.8, 86.6/2045.9, 84.9/2062.7 |

## Diagnostic host and artifact evidence

Shared-host snapshots record CPU pressure and do not establish an isolated quiet host. No arm was discarded.

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

## Normal API comparison (no exception handler)

Harness `89ad448bb4052cea62d5280634ecc9cd98f59cee`, same baseline production source and SDK as above.
Only the api lane runs here; its per-request engine attribution supplies the denominator. All 24 cells
returned nonempty attribution, zero operation errors and null (unmeasured) exception counts. No tests were skipped.
These shared-host measurements are diagnostic, not quiet-host qualification or live production evidence.

| Size | c | Arm | Client p50 µs median (min–max) | p99 µs median | CPU µs/op median (min–max) | B/op median | Overhead/engine p50 % median |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| small | 1 | before | 109.4 (98.3–109.7) | 366.8 | 207.9 (177.6–216.4) | 31,080 | 2,277 |
| small | 1 | after | 104.8 (90.8–109.2) | 339.9 | 187.5 (144.6–197.8) | 30,453 | 2,238 |
| small | 16 | before | 194.4 (194.2–218.0) | 2,154.8 | 141.9 (132.9–142.0) | 30,531 | 2,134 |
| small | 16 | after | 174.5 (173.0–196.4) | 2,120.3 | 128.5 (121.8–129.1) | 30,017 | 2,114 |
| medium | 1 | before | 127.1 (107.8–144.1) | 390.6 | 226.8 (177.9–270.4) | 46,808 | 2,108 |
| medium | 1 | after | 118.4 (100.4–196.1) | 416.9 | 205.0 (149.8–253.9) | 46,281 | 2,074 |
| medium | 16 | before | 282.1 (266.0–285.7) | 2,637.5 | 180.5 (160.1–197.0) | 45,929 | 1,880 |
| medium | 16 | after | 232.8 (212.5–242.6) | 2,537.9 | 158.7 (147.1–160.0) | 45,457 | 1,785 |

Raw normal-api triplets likewise follow repeats 1, 2, 3.

| Arm | Size | c | ops/s | Client p50/p99 µs | CPU µs/op | B/op | Server p50 µs | Engine p50 µs | Overhead p50/p99 µs |
| --- | --- | ---: | --- | --- | --- | --- | --- | --- | --- |
| before | small | 1 | 9054, 7699, 7324 | 98.3/265.5, 109.4/366.8, 109.7/368.7 | 177.6, 207.9, 216.4 | 30925, 31080, 31107 | 63.9, 71.4, 71.9 | 2.7, 3, 3 | 61.3/157.7, 68.3/215.1, 68.7/208.2 |
| before | small | 16 | 50925, 53378, 53348 | 218/2032.2, 194.4/2154.8, 194.2/2162.3 | 132.9, 141.9, 142 | 30435, 30531, 30598 | 78.5, 81.9, 81.3 | 3.5, 3.6, 3.7 | 74.7/1681.9, 78/1758.7, 77.4/1781.7 |
| before | medium | 1 | 8625, 6658, 5582 | 107.8/250.7, 127.1/390.6, 144.1/474 | 177.9, 226.8, 270.4 | 46593, 46808, 46847 | 72.4, 86.4, 97.9 | 3.3, 3.9, 4.4 | 69.1/135.7, 82.2/233.9, 93.3/289.2 |
| before | medium | 16 | 37138, 36804, 34154 | 285.7/2308.9, 266/2637.5, 282.1/2909.1 | 160.1, 180.5, 197 | 45856, 45929, 45995 | 91.3, 97.6, 102 | 4.6, 4.9, 5.4 | 86.5/1750.5, 92.5/2065.1, 96.2/2365.5 |
| after | small | 1 | 9930, 7687, 7232 | 90.8/221.6, 104.8/339.9, 109.2/407.7 | 144.6, 187.5, 197.8 | 30325, 30453, 30554 | 58.9, 68.1, 71 | 2.4, 2.9, 3.3 | 56.5/131.3, 64.9/202.2, 67.4/230.5 |
| after | small | 16 | 54918, 57979, 57741 | 196.4/1981, 174.5/2120.3, 173/2202.1 | 121.8, 129.1, 128.5 | 29926, 30017, 30048 | 75, 77.7, 76.1 | 3.3, 3.5, 3.6 | 71.5/1600.1, 74/1755, 72.3/1846.6 |
| after | medium | 1 | 9097, 6617, 5375 | 100.4/240.1, 118.4/416.9, 196.1/480.2 | 149.8, 205, 253.9 | 46016, 46281, 46504 | 67.4, 80.9, 122.3 | 3.1, 3.7, 5.7 | 64.3/131, 76.8/231.1, 114.6/279.5 |
| after | medium | 16 | 41966, 42281, 39860 | 242.6/2160.7, 212.5/2537.9, 232.8/2609.7 | 147.1, 158.7, 160 | 45376, 45457, 45478 | 87.6, 90.8, 90 | 4.6, 4.8, 5.1 | 82.8/1711, 85.7/2088.2, 84.7/2077.4 |

| Arm | Pair | UTC start–end | load1 start–end | CPU some avg10 % start–end |
| --- | ---: | --- | --- | --- |
| before | 1 | 2026-10-28T01:12:11Z–2026-10-28T01:13:22Z | 9.96–7.39 | 15.49–6.53 |
| before | 2 | 2026-10-28T01:15:38Z–2026-10-28T01:16:47Z | 4.27–5.29 | 5.10–5.76 |
| before | 3 | 2026-10-28T01:16:47Z–2026-10-28T01:17:55Z | 5.29–4.15 | 5.76–5.51 |
| after | 1 | 2026-10-28T01:13:22Z–2026-10-28T01:14:30Z | 7.39–7.36 | 6.53–6.24 |
| after | 2 | 2026-10-28T01:14:30Z–2026-10-28T01:15:38Z | 7.36–4.27 | 6.24–5.10 |
| after | 3 | 2026-10-28T01:17:55Z–2026-10-28T01:19:04Z | 4.15–5.03 | 5.51–5.31 |

Normal server DLL SHA-256: before `64f5463d40fbb1184d1050884abebf38c68c33966006ab2cdc1d886d2b029b9b`, after `ec66f5d759c31331babb3d41d72854ccac65bf2f76f585751954a53c5855d6d5`.
Raw normal artifacts on delta: `/home/mariogk/remote-builds/terrascale/fix-api-keywatch-timer-3abe240428921dedcdd8d9fd645d2b861e9c2c0be603805186d65566b2ad022e/benchmark/bench/keywatch-pairs-delta-noexceptions` (six `.md/.log/.host`, `vmstat.txt`, source/SDK ids, build logs).
Reproduce on harness 89ad448bb with `TERRABASE_VS_TURSO_LANES=api`, invoking `run-server-overhead.sh <SDK checkout> 4d2f9c029 <output directory> src/TerraBase/Features/UserAuth/KeyAuthority.cs` through `ts-heavy-any nice -n 19 taskset -c 0-11`.

## Attempted quiet window: rejected by the external CPU control

Same verified normal-api binaries, no rebuild, identical three alternating pairs. All six arms passed, zero errors.
A 1 Hz /proc process-CPU monitor excluded this run's process tree. External CPU mean/max was 31.82/519.62% of one CPU.
This failed the predeclared mean<=25%, max<=100% and no external build/test>=10% criteria: VBCSCompiler/MSBuild
and Go compile/vet/link work overlapped the arms. All cells remain below; no quiet-host speedup follows.
| Arm | Pair | Size | c | ops/s | Client p50/p99 µs | CPU µs/op | B/op | Engine p50 µs | Overhead p50/p99 µs |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| before | 1 | small | 1 | 7722 | 107.1/330.4 | 206.9 | 31036 | 2.9 | 66.5/205.3 |
| before | 1 | small | 16 | 53352 | 190.7/2167.6 | 140.8 | 30586 | 3.6 | 76.8/1711.1 |
| before | 1 | medium | 1 | 6108 | 129.8/509.2 | 242.6 | 46885 | 4 | 82.9/272.4 |
| before | 1 | medium | 16 | 39773 | 241.2/2516.6 | 174.5 | 45998 | 4.8 | 89.8/2028.6 |
| before | 2 | small | 1 | 8555 | 103.6/295.5 | 187.7 | 30987 | 3 | 63.4/156.8 |
| before | 2 | small | 16 | 48614 | 222.3/2138.9 | 137.6 | 30512 | 3.7 | 75.2/1662.2 |
| before | 2 | medium | 1 | 7135 | 119.1/371.2 | 214.3 | 46715 | 3.7 | 75.5/220.6 |
| before | 2 | medium | 16 | 39659 | 239.7/2533.1 | 173.8 | 46000 | 4.8 | 89.7/2062.5 |
| before | 3 | small | 1 | 8434 | 100.4/316.6 | 191.3 | 30983 | 2.6 | 62.6/197.8 |
| before | 3 | small | 16 | 54751 | 191.3/2055.7 | 138.1 | 30527 | 3.4 | 76.8/1680.3 |
| before | 3 | medium | 1 | 6575 | 126.2/441.5 | 232.4 | 46889 | 3.7 | 81.2/250.3 |
| before | 3 | medium | 16 | 38128 | 252.2/2628.8 | 176.7 | 45954 | 4.6 | 91/2115.5 |
| after | 1 | small | 1 | 8321 | 100.5/340.3 | 169.1 | 30455 | 2.9 | 59.5/177 |
| after | 1 | small | 16 | 58116 | 173/2156.5 | 128.4 | 30027 | 3.6 | 73.2/1802.4 |
| after | 1 | medium | 1 | 6593 | 120.9/420 | 205.4 | 46277 | 3.8 | 77.5/238.7 |
| after | 1 | medium | 16 | 38795 | 241.1/2732 | 165.2 | 45456 | 5.2 | 86.9/2149.5 |
| after | 2 | small | 1 | 7634 | 103.3/383.3 | 188.3 | 30627 | 2.8 | 64/202.1 |
| after | 2 | small | 16 | 59022 | 172.1/2074.2 | 127.2 | 30026 | 3.4 | 72.7/1702.1 |
| after | 2 | medium | 1 | 6533 | 120.9/391.5 | 210.7 | 46305 | 3.8 | 78.4/229.1 |
| after | 2 | medium | 16 | 42196 | 210.8/2548.3 | 158.2 | 45476 | 4.7 | 85.2/2121.9 |
| after | 3 | small | 1 | 6993 | 111.6/419.2 | 205.1 | 30542 | 3 | 70.5/221 |
| after | 3 | small | 16 | 57600 | 175.4/2164.1 | 128.7 | 30026 | 3.4 | 74.1/1831.5 |
| after | 3 | medium | 1 | 7010 | 115.9/366.5 | 194.7 | 46269 | 3.5 | 73.6/223.4 |
| after | 3 | medium | 16 | 41525 | 214.4/2603.3 | 160.8 | 45482 | 4.8 | 86.5/2187.6 |
Raw arms, CPU samples/monitor source and host metadata on delta: `/home/mariogk/remote-builds/terrascale/fix-api-keywatch-timer-3abe240428921dedcdd8d9fd645d2b861e9c2c0be603805186d65566b2ad022e/benchmark/bench/keywatch-pairs-delta-lowload`.

## Reserved quiet window

Held the remaining three heavy-admission slots during all six arms; no other job was stopped. The same verified
normal-api binaries were reused without a build. All six arms passed, 24 cells, zero errors/skipped tests.
Predeclared quiet control: external CPU mean<=25%, p95<=100% of one CPU, no observed external build/test>=10%.
Observed 406 one-second samples: mean/p95/max 17.94/58.57/116.70%; the max was a T3 UI burst. No build/test samples exceeded 10%.
The slots released at completion. Small c=16 client p50 was 190.0→175.5 µs median (7.6% lower, nonoverlapping
repeat ranges); c=16 CPU medians were 7.8%/7.9% lower for small/medium. Other latency ranges overlap; p99 shows
no general win. This is an isolated-patch local result at the recorded source, not production or <10% target proof.
| Arm | Pair | Size | c | ops/s | Client p50/p99 µs | CPU µs/op | B/op | Engine p50 µs | Overhead p50/p99 µs |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| before | 1 | small | 1 | 6021 | 137.3/474.1 | 271.8 | 34481 | 3.9 | 78/233 |
| before | 1 | small | 16 | 53679 | 190/2250.3 | 142.3 | 30576 | 3.8 | 77.6/1906.2 |
| before | 1 | medium | 1 | 5240 | 151.4/616.4 | 294.2 | 55524 | 4.6 | 89/266.4 |
| before | 1 | medium | 16 | 39337 | 240.5/2556.1 | 173.7 | 45980 | 4.9 | 89.5/2090.4 |
| before | 2 | small | 1 | 6563 | 120.8/396.9 | 245.4 | 31139 | 3.3 | 77.2/235.2 |
| before | 2 | small | 16 | 53695 | 191.3/2193.2 | 140 | 30595 | 3.6 | 76.6/1795.8 |
| before | 2 | medium | 1 | 6134 | 130/407.1 | 249.1 | 46911 | 4 | 85.2/240.5 |
| before | 2 | medium | 16 | 37909 | 253.8/2614.9 | 178.8 | 45994 | 4.8 | 91.1/2175.6 |
| before | 3 | small | 1 | 7965 | 107.6/328.8 | 201.6 | 31010 | 3 | 66.5/196 |
| before | 3 | small | 16 | 54629 | 189.3/2115.7 | 140.3 | 30588 | 3.7 | 76.8/1735.1 |
| before | 3 | medium | 1 | 6380 | 123.2/381.8 | 239.3 | 46821 | 3.8 | 79.4/242.5 |
| before | 3 | medium | 16 | 38573 | 249.2/2560.5 | 175.7 | 45994 | 4.8 | 89.9/2072 |
| after | 1 | small | 1 | 7029 | 112/382.6 | 205.1 | 30530 | 3.2 | 70/209.5 |
| after | 1 | small | 16 | 58382 | 175.5/2064.5 | 128.6 | 30029 | 3.6 | 72.9/1722.2 |
| after | 1 | medium | 1 | 6055 | 128.3/466.1 | 224.5 | 46270 | 4 | 82.1/246.1 |
| after | 1 | medium | 16 | 38466 | 241.7/2720 | 165.8 | 45470 | 5.1 | 87.4/2237.8 |
| after | 2 | small | 1 | 6467 | 131/395 | 221.5 | 30582 | 3.6 | 80.1/224.1 |
| after | 2 | small | 16 | 57547 | 174.6/2177.1 | 129.4 | 30052 | 3.5 | 73.6/1842.4 |
| after | 2 | medium | 1 | 7219 | 112.3/369.8 | 188.3 | 46174 | 3.6 | 70.6/218.7 |
| after | 2 | medium | 16 | 42113 | 210.1/2566.8 | 159 | 45508 | 4.8 | 85/2151.2 |
| after | 3 | small | 1 | 7031 | 111.1/413.3 | 203.4 | 30626 | 3 | 70.2/215.2 |
| after | 3 | small | 16 | 57293 | 175.8/2185.9 | 129.8 | 30053 | 3.4 | 74.4/1847.8 |
| after | 3 | medium | 1 | 5580 | 162.5/483 | 242.1 | 46320 | 4.7 | 101.5/259.7 |
| after | 3 | medium | 16 | 41196 | 218.5/2583.3 | 161.9 | 45500 | 4.9 | 86.6/2144.9 |
Raw arms, reservation receipt, CPU samples/monitor source and host metadata: `/home/mariogk/remote-builds/terrascale/fix-api-keywatch-timer-3abe240428921dedcdd8d9fd645d2b861e9c2c0be603805186d65566b2ad022e/benchmark/bench/keywatch-pairs-delta-reserved`.
