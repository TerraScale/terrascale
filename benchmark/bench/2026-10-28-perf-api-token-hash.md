# Token-hashing allocation experiment, 2026-10-28

## Status

Experiment A is rejected by the predeclared quiet-host gate. It cannot establish a latency or CPU improvement. A second comparison is running with these same frozen binaries and no builds. No production or <10% target claim is made.

The candidate preserves SHA-256/Base64 token cache keys, issuer-key identity, cache capacity and expiry checks. It replaces temporary UTF-8/digest arrays with bounded stack scratch or a cleared pooled buffer. Raw tokens are still excluded from cache keys. Security and revocation settings are unchanged. The accompanying real-HTTP expiry case adds an oversized token; it has not yet run.

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

No live tenant or production credential was used. The normal key-watch rollout, retained tenant reconciliation, real expiry validation and live gru/sjc/fra measurements remain outstanding.
