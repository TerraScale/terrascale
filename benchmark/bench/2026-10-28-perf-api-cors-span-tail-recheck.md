# ClientCors span matcher: independent recheck of the small/c16 tail (2026-10-28)

Recheck of the concern retained in `2026-10-28-perf-api-cors-span.md` (`fix/api-cors-span-report` @ ec4a78534): in run b1,
all three small/c16 pairs were worse after for overhead p99 (+36.1, +49.3, +105.0 µs), client p99 (+26.4, +47.0, +127.2 µs),
client p50 (+2.9, +3.1, +5.0 µs) and ops/s (−334, −104, −901). This is a repeat-only run. It does no build, no tests, no
profiling and no runtime/GC change, and it reuses b1's frozen binaries.

**Verdict: UNRESOLVED under the predeclared rule.** The b1 tail pattern did not repeat. In six new pairs, overhead p99 and
client p99 were worse in only 2/6, with negative median deltas, and ops/s was worse in 3/6 with a positive median. The rule
still returns UNRESOLVED because small/c16 client p50 was worse in 3/6 pairs with a median of +0.2 µs. A small regression is
not ruled out, and no speedup is claimed. The allocation benefit repeated: B/op fell by 457–602 in all 21 unconfounded
paired cells. The three small/c1 pairs hit by the slow first-cell state (see the first limitation) were +4,901, −5,752 and −6,026.

## Predeclared assessment (committed at 7ec90f295 before any timing)

`benchmark/scripts/api-cors-span-repeat-summary.py`, small/c16. Metrics: overhead p99, client p99, client p50 (worse = after
higher) and ops/s (worse = after lower).

- REPRODUCED: at least two metrics are worse in 5 or 6 of the 6 pairs, with a worse median paired Δ.
- NOT REPRODUCED: every metric is worse in at most 3 of 6 pairs, and none has a worse median.
- UNRESOLVED: anything else.

Six alternating pairs: B A, A B, B A, A B, B A, A B. Each arm is a fresh process. The quiet gate is b1's, checked per arm:
external CPU mean ≤ 25 % of one core, p95 ≤ 100 %, and no external builder at ≥ 10 %. Any failing arm invalidates the run.
Nothing is dropped, retried or stopped early. The b1 pairs are shown for comparison only and are not pooled, because they
produced the hypothesis.

## Provenance

| Item | Value |
| --- | --- |
| Command | `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cors-span-repeat-20261028.sh r2 --repo terrascale --branch fix/api-cors-span-tail-recheck` |
| Script commit | 3772a194e (7ec90f295 plus the manifest fix below; the verdict rule is unchanged) |
| Frozen source (read-only) | `beta:~/remote-builds/terrascale/fix-api-cors-span-path-dd6840fd8f6c928f24b43da9d2af2868068850ab70fecedbaed280f5f4c6ceb8/benchmark/bench/cors-span-b1/{before,after}` (b1 checkout at f18d8f4b1) |
| before `terrabase.dll` | 4d83e14a7d674f3541520b31de8b7893e805edaff62f15bf2f157f64d2baa925 |
| after `terrabase.dll` | 0101b702b325af34f0cb84aa10032fd1ab815993175803a8f83b4a1862f3aa1d |
| Same in both arms | `TerraScale.DotNet.dll` a4f3be7f…41e19a, `TerraBase.E2E.dll` f871c1bd…ad33d, runtimeconfig 5628e9d5…0809a |
| Hash checks | The pinned manifests are byte-equal (`cmp`) to b1's. The originals pass before copying. Copies pass `diff -r` (excluding `TestResults/`), and before every arm they pass the 4 pinned hashes plus a hash of every file in the copy |
| Env per arm | Same as b1: `LANES=api WORKLOADS=read CONCURRENCY=1,16 SECONDS=5 REPEATS=1`, `taskset -c 2-11`, fixed 10 s warmup and 5 s window, workstation concurrent GC, TieredPGO, .NET 11.0.0, synthetic test-issuer key |
| Reservation | Slots 1–4: the job held one and reserved the other three; no other jobs were stopped |
| Raw (r2) | `beta:~/remote-builds/terrascale/fix-api-cors-span-tail-recheck-d9b0036102044a305bf79e0161eddb9a9e85d53d426d792f9e749585d3fa88da/benchmark/bench/cors-span-repeat-r2/` |
| r2 hashes | `summary.md` 2a15ed9e…ebdf7, `cpu-processes.jsonl` 25636f12…5262e, `vmstat.txt` f51bb181…832c3 |

**Aborted run r1 (kept, excluded).** The first launch (script 7ec90f295, label r1, same raw folder with `-r1`) stopped after
pair 1 (before-1, after-1). The extra all-files manifest included `TestResults/`, which TUnit rewrites in the arm folder on
every run, so the check failed before after-2. The four pinned hashes still matched. The fix excludes only `TestResults/`.
r2 is the single complete repeat, launched with the same plan before any r1 numbers were read. r1's small/c16 pair
(Δ: overhead p99 −9.9 µs, client p99 +26.1 µs, client p50 −5.0 µs, ops/s +382) is listed for disclosure only.

## Quiet gate (r2, run order)

Every arm passed: 69–70 samples, external mean 3.0–4.8 %, p95 8.9–14.9 %, builder 0.0 %. Errors were 0 in all arms. Load
average was 3.3–6.0 (the job's own arms) and CPU PSI some avg10 was 0.3–5.9 %.

## small/c16: paired Δ = after − before (r2 pairs in order)

| Metric | before range | after range | Δ p1 … p6 | worse | median Δ | b1 Δ (r1–r3), worse |
| --- | ---: | ---: | --- | ---: | ---: | --- |
| overhead p99 µs | 1,484.9–1,652.1 | 1,490.8–1,630.0 | −72.2, −120.2, +131.3, −16.7, +21.4, −57.2 | 2/6 | −36.9 | +36.1, +49.3, +105.0; 3/3 |
| client p99 µs | 1,967.6–2,124.3 | 1,967.3–2,109.9 | −38.3, −131.4, +112.0, −8.5, +33.5, −52.4 | 2/6 | −23.4 | +26.4, +47.0, +127.2; 3/3 |
| client p50 µs | 339.9–352.5 | 337.2–348.3 | −4.4, +1.3, −0.7, +7.7, +1.1, −7.7 | 3/6 | +0.2 | +2.9, +3.1, +5.0; 3/3 |
| ops/s | 34,504–35,950 | 34,907–36,312 | +621, +603, −544, −693, −174, +928 | 3/6 | +214 | −334, −104, −901; 3/3 |
| server p99 µs | 1,508.7–1,673.3 | 1,513.4–1,665.1 | −75.0, −110.9, +118.9, −17.4, +39.0, −61.2 | 2/6 | −39.3 | 3/3 |
| overhead p50 µs | 107.2–110.8 | 107.4–109.6 | +0.3, −0.6, 0.0, +1.3, +1.5, −3.4 | 3/6 | +0.1 | 2/3 |
| engine p50 / p99 µs | 5.0–5.2 / 10.7–11.4 | 5.1–5.3 / 11.1–11.9 | p99: +0.5, −0.3, 0.0, +0.2, +1.0, 0.0 | 4/6, 3/6 | +0.1 / +0.1 | 0/3, 0/3 |
| CPU µs/op | 193.9–200.2 | 193.0–198.8 | −0.8, −4.0, +1.3, +1.2, +2.5, −4.4 | 3/6 | +0.2 | 2/3 |
| B/op | 29,921–30,043 | 29,429–29,445 | −550, −484, −492, −602, −484, −488 | 0/6 | −490 | 0/3 |

Across b1 and r2 (descriptive, not the verdict), worse pairs out of 9: overhead p99 5, client p99 5, client p50 6, ops/s 6.

## Other cells (r2, worse pairs out of 6 and median Δ)

- medium/c16: overhead p99 3/6 (−4.4 µs), client p99 3/6 (+2.2), client p50 3/6 (+1.2), ops/s 2/6 (+262). Engine p50
  was lower after in 6/6 pairs (−0.2 µs) and engine p99 in 5/6 (−0.5 µs), matching b1's separated engine ranges.
- medium/c1: every latency metric was worse in at most 4/6 pairs, with medians within ±5.3 µs.
- small/c1 is confounded (see the first limitation). B/op fell in every pair of medium/c1 (−473 to −583) and medium/c16
  (−481 to −591).

The full table, including b1 recomputed, is `summary.md` in the raw folder.

## Assessment

- The b1 small/c16 signal, all three pairs worse on four tail and throughput metrics, did not reproduce in a quieter repeat.
  External CPU means were 3–5 % here against 7–8 % in b1. The p99 deltas switch sign within this run (−131 to +131 µs) with
  a pair-to-pair spread the size of b1's effect. So b1's direction is consistent with ordinary p99 variance, but this run
  cannot prove that.
- A small effect in small/c16 client p50 or overhead p50 (around +0.1 to +0.2 µs median, under 0.1 %) is neither shown nor
  excluded. Six pairs detect only large consistent shifts: a 6/6 sign test has two-sided p ≈ 0.03. The predeclared rule
  therefore keeps the result UNRESOLVED rather than calling it neutral.
- The <10 % server-over-engine target is still unmet, and this change does not move it. With the same denominator, overhead
  p50 is about 87–118 µs against engine p50 of about 3.9–6.8 µs across the four cells (roughly 1,700–2,200 %).

## Limits

- small/c1, the first cell in each process, ran in a slow state in 3 of 12 arms (after-1, before-3, before-4): about 5,060
  ops/s against about 6,000, +5.5 KB/op and +52 µs CPU/op. The state hit both arms and is not attributable to the change.
  It makes small/c1 pairs 1, 3 and 4 uninformative. The c16 cells, measured after it, were not checked for carry-over beyond
  their own spread.
- Overhead and server percentiles come from a 16,384-sample reservoir per cell (about 177 k requests at small/c16), so p99
  rests on about 164 samples. They are wall time including instrumentation, not CPU. B/op and CPU µs/op are process-wide,
  client and server in one process.
- One host (beta), the loopback single-node api read lane only: no TLS, no SDK lane, no live gru/sjc/fra, no tenants or
  credentials.
