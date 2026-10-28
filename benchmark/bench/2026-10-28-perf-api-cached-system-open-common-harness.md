# Cached system-store open: common-harness paired measurement, run common-r2 (2026-10-28)

Candidate `fix/terrabase-cached-system-open` @ 8aaef93e8 (production delta: `TursoStorageEngine.OpenSystemDatabaseAsync`
fast path and `StampingStorageEngine`'s cached system wrapper), against main a42f65d55. ts-dotnet 36d114877a69. These are
frozen inputs, not current main.

**Result: allocation REPRODUCED under the predeclared rule (B/op lower in 6/6 pairs in every cell, median −320 to −363
B/op). The predeclared worsening rule also fired: CONCERN on small/c16 CPU µs/op (5/6 worse, median +1.0 µs), small/c16
server p99 (5/6 worse, median +38.4 µs) and medium/c16 client p50 (5/6 worse, median +3.3 µs).** Quiet gate passed in all 12
arms, 0 errors, no small/c1 arm flagged. This is one run on beta (loopback, single node, api read lane), not production.

## Why a new protocol (r1 is unchanged)

Run r1 (`2026-10-28-perf-api-cached-system-open.md`, commit 6c3bd20d8) stopped before timing. Its predeclared check required
the separately built `TerraBase.E2E.dll` files to be byte-identical, and they differed in 72 bytes. No timed arm, reservation or
observer started in r1. The differing bytes were not parsed, so this report does not claim they are only identity fields. r1's
outcome and artifacts stand as they were.

The root authorised a new protocol and one new run. The script at 7b3980c65 (`fix/terrabase-cached-system-open-ab`, 8aaef93e8 +
`benchmark/scripts/` only) does no build. It copies r1's frozen arm directories into a new run directory and uses r1's
**candidate** `TerraBase.E2E.dll`/`.pdb` in both arms. Each arm keeps its own frozen `terrabase.dll`/`.pdb` pair. Before any
timing, it checks:

- the four known r1 hashes (both servers, both original E2E DLLs) and both r1 all-files manifests;
- after the copy, that `TerraScale.DotNet.dll`, `TerraBase.E2E.dll` and the runtimeconfig are identical, the file sets are
  identical, and the only differing files are `terrabase.dll` and `terrabase.pdb`;
- the existing real-HTTP local scenario `RepairAndContractE2ETests/Every_region_publishes_the_replication_lag_series` (3 cases,
  `Cluster.StartLocalAsync(All)`, documents seeded over HTTP, `/metrics` read over HTTP) against each arm. This is a
  compatibility check outside timing, with no retries.

The timing and the verdict rules are unchanged from r1's predeclared `api-cached-system-open-summary.py`.

Binding assumption, reviewed before launch: the E2E sources are identical at both commits, and the server delta adds or
changes only internal/private members (`Store.Inner`, `SystemStore`, `_system`, `OpenSystemDatabaseSlowAsync`) that the E2E code
does not use. That the candidate-compiled harness binds to the baseline server is supported by evidence, not proven: the smoke
passed against both arms, and all 12 timed arms ran with 0 errors.

## Provenance

| Item | Value |
| --- | --- |
| Command (run once, no retry) | `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cached-system-open-20261028.sh common-r2 --repo terrascale --branch fix/terrabase-cached-system-open-ab` |
| Harness head (`harness-sha.txt`) | 7b3980c656ed4fdd683296409d7f71b733b70a6c |
| Baseline / candidate / SDK | a42f65d558384ed2728f2799c70fe98addf6924f / 8aaef93e8e65d1b17e7d754f6911bc8e4630bf80 / 36d114877a69d18fba33b27ad362bd3e68e9e7ff |
| Frozen source | `beta:~/remote-builds/terrascale/fix-terrabase-cached-system-open-ab-425dad0ed56916cf56224d19eb2e76fc3465d797f40393a72b60c1d7f7ed1e48/benchmark/bench/cached-system-open-r1/` (manifests re-verified intact after the run) |
| before `terrabase.dll` / `.pdb` | 54e1578ce84105e9f54b836d30470fed6eed58375fb9a5188f1e6b6609cd74af / 4351197c0092329bec45a97ff4929d84690e9b6661f3b21430e7a0189c30a587 |
| after `terrabase.dll` / `.pdb` | 6868fb0b561c7a24fcbecaed5c5a3a708a5c3aab20a066dc8ab7c96459476207 / 177882ff692728db26ea01052f2b6b2d999705b430e54fea5d9d37e3ca2ffa8f |
| Common `TerraBase.E2E.dll` / `.pdb` (r1 after) | 2eadb593cea6a3056e081a06bcbc3476fbe19a40f40a53db1eeb289c4331651e / 547938ef1a7278ed0ae96966202e37fd91f1971ee1806c7e2a0e1981252eb282 |
| r1 before E2E (not used) | a28fac0aa00b7747328fe4322f731717dab1e0a18b0938ea16a0f07adbaf5e2d |
| `TerraScale.DotNet.dll` / runtimeconfig (both arms) | a4f3be7fccbf55ff5b5516cb5d0063982a67771fd47c2d45fe7cb982e641e19a / 5628e9d54e2eae59b08c162e41eedb7bffd95ab6b2646c23a8d49473f590809a |
| Runtime | .NET 11.0.0-rc.1.26425.128, workstation concurrent GC, TieredPGO (runtimeconfig), `taskset -c 2-11` (10 CPUs) |
| `binary-diff.txt` | only `./terrabase.dll` and `./terrabase.pdb` differ (abcc2311…a3f79) |
| Compatibility smoke | before: 3 total, 3 succeeded, 0 failed, 0 skipped (6.69 s); after: 3 total, 3 succeeded, 0 failed, 0 skipped (6.69 s) |
| Reservation | own slot plus 3 additional heavy slots reserved; no other job stopped. No live job shared this branch's ts-heavy key at launch. |
| Timed window | 2026-10-28T12:06:38Z – 12:20:37Z, order B A, A B, B A, A B, B A, A B (`order.txt`) |
| Raw | `beta:~/remote-builds/terrascale/fix-terrabase-cached-system-open-ab-425dad0ed56916cf56224d19eb2e76fc3465d797f40393a72b60c1d7f7ed1e48/benchmark/bench/cached-system-open-common-r2/` |
| Raw hashes | `job.log` f2cfc70a…04f1, `summary.md` b447243a…0cc3, `cpu-processes.jsonl` 71e0db2b…b940, `before.all.sha256` 08eda91c…a371fd, `after.all.sha256` 657fadf6…d72d0d (identical to r1's after manifest), `frozen-key.sha256` 69496c3b…720d, `smoke-before.log` 788e27e4…39c539, `smoke-after.log` 0a99786c…1ee1a; every per-arm `.md`/`.log` is in `artifacts.sha256` (verified OK after the run) |

## Quiet gate (arms in run order)

| Arm | samples | external mean % | external p95 % | max external builder % | verdict |
| --- | ---: | ---: | ---: | ---: | --- |
| before-1 | 69 | 3.3 | 11.9 | 6.0 | pass |
| after-1 | 69 | 3.2 | 13.9 | 5.0 | pass |
| after-2 | 69 | 3.0 | 8.9 | 1.0 | pass |
| before-2 | 70 | 4.5 | 13.9 | 2.0 | pass |
| before-3 | 69 | 3.2 | 9.9 | 0.0 | pass |
| after-3 | 69 | 3.1 | 10.9 | 3.0 | pass |
| after-4 | 69 | 3.1 | 10.9 | 0.0 | pass |
| before-4 | 69 | 4.3 | 14.8 | 0.0 | pass |
| before-5 | 70 | 3.0 | 12.9 | 0.0 | pass |
| after-5 | 69 | 3.1 | 9.9 | 0.0 | pass |
| after-6 | 70 | 3.0 | 10.9 | 0.0 | pass |
| before-6 | 69 | 4.5 | 14.9 | 0.0 | pass |

Run gate: PASS (thresholds: mean ≤ 25 % of one core, p95 ≤ 100 %, no external builder ≥ 10 % in any sample).

## Paired Δ = after − before within a pair (worse = higher, except ops/s)

All cells are kept, including each arm's first cell (small/c1). Server, engine and overhead come from the harness's
`ServerAttribution` (16,384 sampled requests per cell). Its interval boundaries are the existing, process-wide ones, unchanged
here.

| Size | Conc | Metric | before range | after range | before median | after median | paired Δ p1 … p6 | worse pairs | median Δ |
| --- | ---: | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: |
| small | 1 | B/op | 30,548–30,617 | 30,195–30,256 | 30,572 | 30,228 | -358, -359, -360, -348, -375, -329 | 0/6 | -358 |
| small | 1 | CPU µs/op | 221.7–229.2 | 223.0–228.0 | 225.9 | 226.3 | 3.2, -2.1, 1.1, 2.4, 0.7, -3.3 | 4/6 | 0.9 |
| small | 1 | ops/s | 5,885–6,171 | 5,984–6,106 | 6,042 | 6,052 | -79, 151, -21, -43, 9, 52 | 3/6 | -6 |
| small | 1 | client p50 µs | 151.4–158.6 | 151.9–156.3 | 153.8 | 154.1 | 1.9, -3.9, 0.4, 2.3, -1.0, -1.8 | 3/6 | -0.3 |
| small | 1 | client p99 µs | 336.8–367.0 | 338.5–373.4 | 346.4 | 350.6 | 14.9, 6.4, -10.5, 15.7, 3.3, -7.9 | 4/6 | 4.8 |
| small | 1 | server p50 µs | 88.8–92.3 | 89.6–92.0 | 90.5 | 90.7 | 0.8, -1.3, -0.1, 1.7, -0.9, -0.8 | 2/6 | -0.4 |
| small | 1 | server p99 µs | 262.3–299.1 | 266.9–296.0 | 270.5 | 276.9 | 7.5, -3.1, -7.4, 16.2, 12.0, -6.0 | 3/6 | 2.2 |
| small | 1 | engine p50 µs | 3.8–4.1 | 3.9–4.2 | 4.0 | 4.0 | -0.2, 0.0, 0.1, 0.3, -0.1, 0.2 | 3/6 | 0.1 |
| small | 1 | engine p99 µs | 140.3–167.4 | 143.9–155.5 | 145.8 | 150.8 | 9.7, -12.0, -2.6, 11.2, 10.3, -1.6 | 3/6 | 4.0 |
| small | 1 | overhead p50 µs | 84.6–88.2 | 85.5–87.7 | 86.6 | 86.7 | 0.9, -1.3, -0.1, 1.4, -0.8, -0.9 | 2/6 | -0.4 |
| small | 1 | overhead p99 µs | 162.1–167.7 | 161.9–166.1 | 164.2 | 163.6 | -0.2, -3.5, 0.9, -0.6, -1.6, 0.4 | 2/6 | -0.4 |
| small | 16 | B/op | 29,913–29,993 | 29,557–29,626 | 29,930 | 29,590 | -426, -292, -367, -362, -364, -319 | 0/6 | -363 |
| small | 16 | CPU µs/op | 192.1–196.6 | 193.7–197.9 | 196.0 | 195.4 | 1.6, -2.2, 0.7, 0.6, 1.3, 1.3 | 5/6 | 1.0 |
| small | 16 | ops/s | 34,514–35,934 | 34,907–35,724 | 35,296 | 35,335 | -211, 521, -518, 860, -205, -535 | 4/6 | -208 |
| small | 16 | client p50 µs | 339.6–351.4 | 339.8–347.4 | 347.9 | 345.1 | -0.2, -7.3, 3.5, -4.6, -4.6, 2.4 | 2/6 | -2.4 |
| small | 16 | client p99 µs | 1,976.4–2,142.4 | 1,998.7–2,143.6 | 2,015.4 | 2,050.3 | 36.8, -4.8, 72.0, -143.7, 144.2, 54.1 | 4/6 | 45.5 |
| small | 16 | server p50 µs | 111.4–114.4 | 111.3–115.0 | 113.6 | 113.0 | -0.1, -1.6, -0.1, 1.7, -0.2, -0.1 | 1/6 | -0.1 |
| small | 16 | server p99 µs | 1,550.1–1,635.3 | 1,573.9–1,681.8 | 1,566.1 | 1,606.0 | 27.6, 1.2, 49.2, -61.4, 126.4, 81.0 | 5/6 | 38.4 |
| small | 16 | engine p50 µs | 4.9–5.2 | 5.0–5.2 | 5.1 | 5.0 | -0.1, 0.0, 0.1, 0.0, -0.1, 0.1 | 2/6 | 0.0 |
| small | 16 | engine p99 µs | 10.8–11.4 | 10.7–11.8 | 11.1 | 11.0 | -0.4, 0.3, 0.1, -0.2, -0.5, 0.4 | 3/6 | -0.0 |
| small | 16 | overhead p50 µs | 106.0–109.0 | 106.0–109.5 | 108.2 | 107.7 | 0.0, -1.5, -0.1, 1.7, 0.1, -0.2 | 2/6 | -0.0 |
| small | 16 | overhead p99 µs | 1,520.3–1,602.2 | 1,550.2–1,653.5 | 1,547.9 | 1,578.7 | 19.5, -4.4, 43.6, -49.7, 112.2, 93.2 | 4/6 | 31.5 |
| medium | 1 | B/op | 46,400–46,438 | 45,961–46,157 | 46,414 | 46,083 | -465, -321, -281, -403, -306, -320 | 0/6 | -320 |
| medium | 1 | CPU µs/op | 225.9–231.1 | 226.2–229.1 | 228.1 | 228.9 | -0.7, -1.6, 0.9, 3.1, -2.0, 0.1 | 3/6 | -0.3 |
| medium | 1 | ops/s | 5,673–5,778 | 5,692–5,799 | 5,722 | 5,724 | 22, 58, -45, -48, 17, 45 | 2/6 | 19 |
| medium | 1 | client p50 µs | 161.9–165.2 | 161.9–163.6 | 163.3 | 162.8 | 0.7, -3.0, 0.1, 1.6, -2.3, -1.6 | 3/6 | -0.7 |
| medium | 1 | client p99 µs | 362.3–412.9 | 384.6–417.0 | 384.1 | 389.6 | -9.8, 3.5, 15.6, 30.3, 32.3, -28.3 | 4/6 | 9.6 |
| medium | 1 | server p50 µs | 96.8–99.2 | 96.6–98.1 | 97.6 | 97.5 | -0.2, -1.6, 0.7, 1.1, -1.1, -0.6 | 2/6 | -0.4 |
| medium | 1 | server p99 µs | 276.4–300.4 | 285.8–317.2 | 286.5 | 294.1 | -14.6, 9.9, 11.5, 17.6, 31.6, -3.9 | 4/6 | 10.7 |
| medium | 1 | engine p50 µs | 4.8–5.0 | 4.8–5.0 | 4.8 | 4.8 | -0.1, 0.0, 0.0, 0.2, -0.1, 0.0 | 1/6 | 0.0 |
| medium | 1 | engine p99 µs | 143.8–152.8 | 144.5–170.2 | 147.4 | 152.6 | -8.3, 3.1, 11.0, 14.3, 21.2, -4.4 | 4/6 | 7.0 |
| medium | 1 | overhead p50 µs | 91.8–94.2 | 91.6–93.3 | 92.8 | 92.6 | -0.2, -1.5, 0.7, 0.9, -0.9, -0.8 | 2/6 | -0.5 |
| medium | 1 | overhead p99 µs | 172.7–177.8 | 170.8–178.8 | 176.1 | 175.4 | -1.9, 1.5, 1.0, -1.9, 1.9, -0.9 | 3/6 | 0.0 |
| medium | 16 | B/op | 45,370–45,446 | 45,012–45,073 | 45,381 | 45,044 | -426, -297, -374, -363, -363, -315 | 0/6 | -363 |
| medium | 16 | CPU µs/op | 213.4–222.0 | 217.9–223.3 | 220.2 | 220.9 | 4.5, -2.4, 5.4, 3.6, -0.3, -0.6 | 3/6 | 1.7 |
| medium | 16 | ops/s | 28,869–30,343 | 29,118–29,613 | 29,209 | 29,485 | -730, 681, 77, 181, -26, 249 | 2/6 | 129 |
| medium | 16 | client p50 µs | 380.6–395.9 | 387.0–398.6 | 390.6 | 390.9 | 6.4, -8.7, 2.9, 3.8, 2.5, 6.9 | 5/6 | 3.3 |
| medium | 16 | client p99 µs | 1,959.4–2,185.6 | 2,087.5–2,166.7 | 2,111.1 | 2,112.4 | 150.1, -5.5, 16.5, -22.7, 64.3, -83.5 | 3/6 | 5.5 |
| medium | 16 | server p50 µs | 119.4–124.1 | 120.5–124.5 | 122.7 | 122.7 | 1.1, -1.8, 1.5, 2.1, -0.7, -0.5 | 3/6 | 0.3 |
| medium | 16 | server p99 µs | 1,503.2–1,670.6 | 1,587.7–1,642.5 | 1,609.2 | 1,621.2 | 113.0, 17.7, 5.9, -17.9, 37.5, -82.9 | 4/6 | 11.8 |
| medium | 16 | engine p50 µs | 6.5–6.8 | 6.2–6.5 | 6.6 | 6.3 | -0.4, -0.4, -0.2, -0.3, -0.3, -0.3 | 0/6 | -0.3 |
| medium | 16 | engine p99 µs | 14.6–14.9 | 13.3–14.7 | 14.9 | 14.1 | -0.5, -1.6, -0.8, -0.6, -0.2, -1.3 | 0/6 | -0.7 |
| medium | 16 | overhead p50 µs | 112.5–117.3 | 114.1–117.8 | 115.8 | 116.2 | 1.6, -1.3, 1.7, 2.4, -0.5, -0.2 | 3/6 | 0.7 |
| medium | 16 | overhead p99 µs | 1,482.0–1,638.9 | 1,569.1–1,606.1 | 1,584.7 | 1,600.7 | 117.9, 23.3, 7.8, -4.9, 19.5, -69.8 | 4/6 | 13.7 |

Errors across all arms: 0.

small/c1 slow-state flag: medians over all 12 small/c1 arms are B/op 30,402 and CPU/op 225.9 µs. Flagged: none.

## Predeclared verdicts

- B/op small/c1: lower in 6/6 pairs, median Δ −358 (profile prediction about −356).
- B/op small/c16: lower in 6/6 pairs, median Δ −363.
- B/op medium/c1: lower in 6/6 pairs, median Δ −320.
- B/op medium/c16: lower in 6/6 pairs, median Δ −363.
- **Allocation verdict (c16 cells): REPRODUCED.**
- **Worsening flag: CONCERN.** small/c16 CPU µs/op 5/6 worse, median +1.0 µs (about +0.5 %); small/c16 server p99 5/6
  worse, median +38.4 µs; medium/c16 client p50 5/6 worse, median +3.3 µs.

Reading, not a rule change: the CONCERN metrics are small next to their own between-arm ranges, and the other cells' paired
deltas for the same metrics change sign. One run cannot tell whether they are real or noise. The rule flags them, and they
stay flagged. Their absence in the other cells is not parity: this run has no non-inferiority tolerance and makes no latency
or throughput claim. The −0.3 µs engine p50 and −0.7 µs engine p99 at medium/c16 (0/6 worse) are not a predeclared question.

## Server over engine

Overhead/engine p50 stays far above the < 10 % target in both arms (before-1 per-arm table: small/c1 2,063 %, small/c16
2,078 %, medium/c1 1,836 %, medium/c16 1,679 %). This candidate saves about 360 B/op of ~30–46 KB/op. It does not establish progress toward the
server-over-engine target, which stays unmet.

## Limits

- One run, beta only, loopback, a single local node, keyed api read lane, small and medium at c1 and c16. No write lanes, no
  `-3` cluster lanes, no profiler.
- The harness was compiled against the candidate's server. That it binds to the baseline server rests on the reviewed source
  delta, the 3-case smoke and 12 error-free arms, not on metadata inspection.
- r1's 72-byte E2E difference is still unparsed and unexplained.
- No live authenticated gru/sjc/fra evidence. No production request, credential or tenant was used: the benchmark's token is a
  synthetic test-issuer key.
- The owned api-perf-01m610ttq4n8ew5y4q25aj5sc2 remains `reconcile_required`, untouched.

## Root verification and disposition

Root rechecked `artifacts.sha256` and both final binary manifests on beta after completion. All 44 paired-metric rows and all
12 quiet-gate rows in this report match the raw `summary.md` exactly. The raw summary SHA-256 is
`b447243a9f6248ddcf6282ae565d68265891c51e3802795376a14ce73fd90cc3`; `job.log` is
`f2cfc70afc1dc08fd448ff3a8db5d96e829f8091cf04318406889da2ca6c04f1`; `artifacts.sha256` is
`4373d7f40faaa478c5deffaea7d0326b5fab46ffcb603120afde78bd846e5e00`.

The production candidate is held because the predeclared concern rule fired. Allocation reduction is reproduced; latency
neutrality and a speedup are not established. This evidence branch contains only the two reports, based on current main;
it includes neither the production candidate nor a new measurement. The frozen protocol remains available at commit
`7b3980c656ed4fdd683296409d7f71b733b70a6c`, under `benchmark/scripts/api-cached-system-open-20261028.sh`.
