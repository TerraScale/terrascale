# Token-hashing allocation experiment, 2026-10-28

## Status

DECISION: do not ship the token-hashing candidate. Reason: the three quiet comparisons show mixed latency, including two medium c=1 after arms slower than every before arm. Lower allocation and medium c=16 process CPU do not justify the extra production complexity with this latency risk. No production or <10% target claim is made.

The candidate preserves SHA-256/Base64 token cache keys, issuer-key identity, cache capacity and expiry checks. It replaces temporary UTF-8/digest arrays with bounded stack scratch or a cleared pooled buffer. Raw tokens are still excluded from cache keys. Security and revocation settings are unchanged. The accompanying real-HTTP expiry case adds an oversized token; both argument cases passed in the targeted auth validation below.

## Experiment A (rejected)

Delta, 12 vCPU, affinity 0–11, .NET 11.0.0, workstation concurrent GC, tiered PGO; run through `ts-heavy-any nice -n 19`. All other heavy slots were reserved without stopping other jobs. Six fresh-process arms alternate before/after, after/before, before/after. Each arm has ten-second warmup and five-second cells, keyed synthetic project token, 256/4096 B reads at c=1/16. All 24 cells completed with zero errors and server attribution present.

Baseline server `dc414b8a281aeb8d03a53f310e1a2a138350a6c7`; candidate `7806b36b985e3e3c8b0e8e4266c0c0468aeecd6a`. Harness and SDK are identical; SDK `36d114877a69d18fba33b27ad362bd3e68e9e7ff`. Builds succeeded with zero warnings/errors. Different server DLLs and identical SDK DLLs were verified. Whole-process CPU/allocations include client, background work and attribution; these are not server-only CPU costs. Server overhead is each sampled hosting activity duration minus its decorated engine calls, including instrumentation.

Predeclared gate: external CPU mean ≤25% and p95 ≤100% of one CPU, with no external build/test sample ≥10% during an arm. The monitor classifies reparented compiler workers as external. A compiler (`VBCSCompiler`) remained active during the arms; the gate is retained unchanged.

| Arm | CPU samples | External mean % | External p95 % | Build/test samples ≥10% | Quiet accepted |
| --- | ---: | ---: | ---: | ---: | --- |
| after-1 | 68 | 17.5 | 39.73 | 0 | True |
| after-2 | 68 | 18.83 | 52.6 | 2 | False |
| after-3 | 67 | 19.95 | 72.55 | 2 | False |
| before-1 | 68 | 55.83 | 137.21 | 24 | False |
| before-2 | 68 | 15.38 | 31.71 | 1 | False |
| before-3 | 68 | 11.86 | 20.83 | 1 | False |

Every repeat is retained, including outliers. No outlier is dropped.

| Arm | Pair | Size | c | p50 µs | p99 µs | CPU µs/op | Alloc B/op | Overhead p50 µs | Engine p50 µs |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| after | 1 | medium | 1 | 124 | 433.4 | 215.1 | 45562 | 80.2 | 4 |
| after | 1 | medium | 16 | 215.2 | 2484.1 | 159.5 | 44784 | 86.7 | 4.7 |
| after | 1 | small | 1 | 112.1 | 420.4 | 207.9 | 29839 | 71.8 | 3.2 |
| after | 1 | small | 16 | 177.9 | 2148.8 | 130.7 | 29318 | 74.7 | 3.7 |
| before | 1 | medium | 1 | 111.9 | 319.4 | 172.6 | 46278 | 71 | 3.5 |
| before | 1 | medium | 16 | 216.2 | 2580.8 | 160.5 | 45476 | 86.8 | 4.8 |
| before | 1 | small | 1 | 97.6 | 294.2 | 165.4 | 30401 | 60.2 | 2.7 |
| before | 1 | small | 16 | 176.7 | 2135.6 | 130.4 | 30023 | 74.8 | 3.5 |
| after | 2 | medium | 1 | 129.2 | 459.7 | 224.1 | 45601 | 83.8 | 4.1 |
| after | 2 | medium | 16 | 213.2 | 2570.2 | 160.1 | 44756 | 86.3 | 4.7 |
| after | 2 | small | 1 | 130.3 | 433.3 | 221.1 | 29855 | 79.6 | 3.6 |
| after | 2 | small | 16 | 181.7 | 2251.7 | 131.7 | 29301 | 74.7 | 3.7 |
| before | 2 | medium | 1 | 118.5 | 465.9 | 209.3 | 46440 | 76.4 | 3.6 |
| before | 2 | medium | 16 | 220.7 | 2632.2 | 162.3 | 45512 | 85.7 | 4.8 |
| before | 2 | small | 1 | 103.9 | 350.9 | 185.4 | 30595 | 64.3 | 2.7 |
| before | 2 | small | 16 | 174.6 | 2139.5 | 127.6 | 30082 | 73 | 3.3 |
| after | 3 | medium | 1 | 120.7 | 538.8 | 227.7 | 52362 | 76.2 | 3.8 |
| after | 3 | medium | 16 | 214.1 | 2474.9 | 158.7 | 44776 | 85.7 | 4.7 |
| after | 3 | small | 1 | 104.1 | 354.1 | 185.5 | 29756 | 64.1 | 2.9 |
| after | 3 | small | 16 | 171.6 | 2201.1 | 129 | 29300 | 73.4 | 3.5 |
| before | 3 | medium | 1 | 122.9 | 403.7 | 215 | 46251 | 79.9 | 3.9 |
| before | 3 | medium | 16 | 211.5 | 2595.8 | 161.6 | 45537 | 85.4 | 4.9 |
| before | 3 | small | 1 | 111 | 418.6 | 207.1 | 30646 | 70.7 | 3.2 |
| before | 3 | small | 16 | 175.3 | 2096.5 | 126.8 | 30077 | 72.8 | 3.5 |

Artifacts retained on delta under the managed `fix-api-token-hash-allocation-8a84f3a93a1361c3b8556947f5518c229b5c130324ae4290b5f9df98a1c8eeb9` checkout, `benchmark/bench/token-hash-qualification-a/`: exact run script, raw cell Markdown/JSON, logs, host timestamps, CPU samples, vmstat, topology, source identities, hashes and analysis. The initial script is the committed `run-server-overhead.sh` with `ValidatedTokens.cs` as its sole changed production input.

| Artifact | SHA-256 |
| --- | --- |
| before/terrabase.dll | `050bf91659e79dbd2d2efc90bbf987ae2255a0752bfe51c5986866b5f3901eb1` |
| after/terrabase.dll | `c2e13f736c9898030b29a53c12c83519de17ac8d53bc70ea4f94de4e4e973620` |

No live tenant or production credential was used. The normal key-watch rollout, retained tenant reconciliation and live gru/sjc/fra measurements remain outstanding.

## Frozen comparisons B and C

B reused the exact verified A binaries without building; its first pair still failed the unchanged compiler-activity gate. The compiler process later exited (its PID was absent). B pairs 2/3 and C pair 4 passed every noise criterion, yielding three quiet before/after comparisons on the same host: after/before, before/after, after/before. The extra pair was declared before it ran, based on CPU evidence. Eligibility uses the predeclared noise gate, never latency or allocation results. All rejected cells remain below. C ran with the same frozen binaries and no builds.

| Experiment / arm | CPU samples | External mean % | External p95 % | Build/test samples ≥10% | Quiet accepted |
| --- | ---: | ---: | ---: | ---: | --- |
| B / after-1 | 68 | 12.46 | 24.8 | 2 | False |
| B / after-2 | 67 | 13.55 | 22.83 | 0 | True |
| B / after-3 | 68 | 12.6 | 24.82 | 0 | True |
| B / before-1 | 66 | 14.1 | 26.89 | 1 | False |
| B / before-2 | 69 | 14.34 | 24.79 | 0 | True |
| B / before-3 | 67 | 12.31 | 25.79 | 0 | True |
| C / after-4 | 66 | 11.54 | 19.85 | 0 | True |
| C / before-4 | 69 | 13.46 | 40.71 | 0 | True |

Quiet-pair medians (min–max); CPU and allocations remain whole-process values.

| Size | c | Arm | p50 µs | p99 µs | CPU µs/op | Alloc B/op |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| small | 1 | before | 105.7 (98.1–140.9) | 330.1 (319.9–440) | 187.9 (176.2–227.3) | 30537 (30489–30659) |
| small | 1 | after | 102.9 (100–113.3) | 325.3 (318.3–391.4) | 184.2 (175.1–205.3) | 29760 (29666–29799) |
| small | 16 | before | 177 (172–182.5) | 2207.2 (2100.5–2243.9) | 130.8 (127.5–132.8) | 30102 (30075–30105) |
| small | 16 | after | 173.8 (169.6–178) | 2107.7 (2018.4–2259.1) | 126.4 (124.7–131.2) | 29319 (29310–29361) |
| medium | 1 | before | 120 (113.1–127.2) | 410.2 (385–411.4) | 208 (190.6–219.8) | 46288 (46198–46334) |
| medium | 1 | after | 175.2 (122.2–185.3) | 449.5 (389.7–484.1) | 241.2 (210.4–249.1) | 45677 (45551–45792) |
| medium | 16 | before | 214.6 (213.5–225) | 2612 (2597.2–2652.6) | 162.4 (160.2–165.6) | 45547 (45511–45554) |
| medium | 16 | after | 215.9 (212.7–218.1) | 2541.5 (2481.2–2569.2) | 159 (158.2–159.6) | 44765 (44758–44816) |

Allocation ranges are lower in all four quiet cells. Medium c=16 CPU is 162.4 → 159.0 µs/op (−2.1%, nonoverlapping ranges); its p99 ranges also do not overlap. The other latency comparisons overlap, and medium c=1 after p50 is 175.2 µs versus 120.0 µs before. This does not establish an across-workload improvement or an acceptable c=1 outcome. Every measured server-overhead/engine p50 ratio remains far above 10%.

All B/C cells (zero operation errors, attribution present):

| Experiment / arm | Pair | Size | c | p50 µs | p99 µs | CPU µs/op | Alloc B/op | Overhead p50 µs | Engine p50 µs |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| B / after | 1 | medium | 1 | 118.8 | 454.5 | 210.3 | 45480 | 77.1 | 3.6 |
| B / after | 1 | medium | 16 | 212.1 | 2591.1 | 158.7 | 44773 | 85.4 | 4.6 |
| B / after | 1 | small | 1 | 107.9 | 375.5 | 196.4 | 29828 | 67.8 | 3 |
| B / after | 1 | small | 16 | 174.4 | 2088.4 | 127.9 | 29304 | 72.8 | 3.5 |
| B / before | 1 | medium | 1 | 146.1 | 439.7 | 237.9 | 46505 | 91.4 | 4.5 |
| B / before | 1 | medium | 16 | 221.8 | 2596.5 | 162.9 | 45528 | 86.7 | 5 |
| B / before | 1 | small | 1 | 101.2 | 360 | 180.4 | 30561 | 62.5 | 2.9 |
| B / before | 1 | small | 16 | 178.9 | 2169.1 | 131 | 30083 | 74.6 | 3.7 |
| B / after | 2 | medium | 1 | 175.2 | 484.1 | 241.2 | 45677 | 104.8 | 4.6 |
| B / after | 2 | medium | 16 | 212.7 | 2569.2 | 158.2 | 44758 | 84.6 | 4.5 |
| B / after | 2 | small | 1 | 100 | 318.3 | 175.1 | 29666 | 61 | 2.6 |
| B / after | 2 | small | 16 | 169.6 | 2018.4 | 124.7 | 29310 | 71.7 | 3.3 |
| B / before | 2 | medium | 1 | 113.1 | 385 | 190.6 | 46198 | 71.9 | 3.6 |
| B / before | 2 | medium | 16 | 214.6 | 2597.2 | 160.2 | 45547 | 85.7 | 4.9 |
| B / before | 2 | small | 1 | 98.1 | 330.1 | 176.2 | 30489 | 60.4 | 2.7 |
| B / before | 2 | small | 16 | 172 | 2100.5 | 127.5 | 30105 | 73.2 | 3.5 |
| B / after | 3 | medium | 1 | 185.3 | 449.5 | 249.1 | 45792 | 110.5 | 5 |
| B / after | 3 | medium | 16 | 218.1 | 2481.2 | 159 | 44816 | 85.5 | 4.6 |
| B / after | 3 | small | 1 | 102.9 | 325.3 | 184.2 | 29760 | 63.3 | 2.9 |
| B / after | 3 | small | 16 | 173.8 | 2107.7 | 126.4 | 29361 | 72.9 | 3.5 |
| B / before | 3 | medium | 1 | 127.2 | 411.4 | 219.8 | 46334 | 82.3 | 3.9 |
| B / before | 3 | medium | 16 | 213.5 | 2612 | 162.4 | 45554 | 87 | 4.9 |
| B / before | 3 | small | 1 | 105.7 | 319.9 | 187.9 | 30537 | 66.5 | 2.9 |
| B / before | 3 | small | 16 | 182.5 | 2243.9 | 132.8 | 30102 | 75.6 | 3.6 |
| C / after | 4 | medium | 1 | 122.2 | 389.7 | 210.4 | 45551 | 78.6 | 3.9 |
| C / after | 4 | medium | 16 | 215.9 | 2541.5 | 159.6 | 44765 | 85.7 | 4.7 |
| C / after | 4 | small | 1 | 113.3 | 391.4 | 205.3 | 29799 | 72 | 3.3 |
| C / after | 4 | small | 16 | 178 | 2259.1 | 131.2 | 29319 | 74.6 | 3.6 |
| C / before | 4 | medium | 1 | 120 | 410.2 | 208 | 46288 | 77.6 | 3.9 |
| C / before | 4 | medium | 16 | 225 | 2652.6 | 165.6 | 45511 | 86.6 | 5 |
| C / before | 4 | small | 1 | 140.9 | 440 | 227.3 | 30659 | 83 | 3.9 |
| C / before | 4 | small | 16 | 177 | 2207.2 | 130.8 | 30075 | 74.1 | 3.6 |

B/C artifacts share the A managed-checkout prefix, under `benchmark/bench/token-hash-qualification-{b,c}/`: run/analysis scripts, raw cells, output logs, hashes, CPU samples, host timestamps and vmstat. `c/accepted.json` records the 24 eligible cells. All 56 A/B/C cells are retained across the tables.

## Auth validation

On beta through `ts-heavy-any nice -n 19`, source `0d651b2f1`, SDK pinned as above, `make terrabase-test-fast CONFIGURATION=Release TEST_ARGS='--treenode-filter /*/*/UserAuthTests/* --minimum-expected-tests 2'` passed: 44 driver tests and 18 auth Fast cases, zero failures/skips; builds reported zero warnings/errors. Expiry coverage includes both normal and oversized tokens, cached and uncached, exactly at expiry while the issuer is unavailable. This targeted run is not the full required Fast suite and supplies no revocation or production proof.

The first validation attempt failed during locked restore because staging the SDK inside the monorepo inherited central package management. No test ran. A fresh pinned SDK checkout outside the monorepo fixed staging; no lock, package or guard was changed. Artifacts remain on beta under managed `fix-api-token-hash-validation-948f6646eca360ca1a380e8356334229366e623530ab46dc2d7f3c6d87ba6c44`, `benchmark/bench/token-hash-validation-{a,b}/`; successful B includes exact script, source identity, logs and empty source/SDK diffs.

The production candidate and new expiry argument are retained as [an unapplied investigation patch](2026-10-28-perf-api-token-hash-candidate.patch), based on `dc414b8a2`. It reproduces the measured production change and validated expiry coverage; it is not compiled or proposed for production. Only evidence is published.
