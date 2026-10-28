# ClientCors.IsDataPath span matcher: paired allocation measurement (2026-10-28)

Candidate: `src/TerraBase/Hosting/ClientCors.cs`. `IsDataPath` used `string.Split('/')`, so every client-listener request
allocated an array and one string per segment. It now splits into a stackalloc `Span<Range>` (8 ranges; the last one keeps
any remainder) and compares the segments as spans. The predicate is unchanged: at least 7 segments, the first empty, segments
3 and 5 nonempty, segments 1/2/4 equal to `v1`/`projects`/`databases`, and segment 6 one of `collections`/`transactions`/`changes`,
all `OrdinalIgnoreCase`. Extra and empty segments behave as before. This addresses the common-path profile
(`origin/fix/api-common-overhead-profile` @ c400ebc63, `2026-10-28-perf-api-common-profile.md`), which attributed
448–498 B/request to `ClientCors.IsDataPath`/`String.Split` in its four sampled cells.

## Pins

| Input | Value |
| --- | --- |
| Baseline (before) | main a5999f5eaddc608e908a92f3b35da3857dec0f1e, `ClientCors.cs` sha256 ce2a1844…0c0211 |
| Candidate source (after) | f18d8f4b1 (`fix/api-cors-span-path`), `ClientCors.cs` sha256 f0915828…6635dd |
| Production diff | `git diff --name-only a5999f5 HEAD -- src Directory.Build.* Directory.Packages.props global.json` = `ClientCors.cs` only (the script checks this) |
| SDK | ts-dotnet 36d114877a69d18fba33b27ad362bd3e68e9e7ff (fresh public GitHub clone, `TsDotnetRoot`) |
| Host | beta, 12 CPUs, arms pinned with `taskset -c 2-11` (10 CPUs), .NET 11.0.0, workstation concurrent GC, TieredPGO (established runtimeconfig edit, same in both arms) |
| Build | Release E2E harness for both arms, 0 warnings and 0 errors each. Fast ran in Debug (Makefile default) |
| before `terrabase.dll` | 4d83e14a7d674f3541520b31de8b7893e805edaff62f15bf2f157f64d2baa925 |
| after `terrabase.dll` | 0101b702b325af34f0cb84aa10032fd1ab815993175803a8f83b4a1862f3aa1d |
| Identical in both arms | `TerraScale.DotNet.dll` a4f3be7f…41e19a, `TerraBase.E2E.dll` f871c1bd…ad33d, runtimeconfig 5628e9d5…0809a (checked with `cmp`, and against `sha256sum --check` before every arm) |

## Commands

```
TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cors-span-20261028.sh b1 --repo terrascale --branch fix/api-cors-span-path
```

The script runs these steps in order:

1. `make fmt-check`.
2. `make terrabase-test-fast` (whole Fast suite) on the candidate.
3. `make terrabase-test-fast TEST_ARGS="--treenode-filter /*/*/ClientCorsTests/*"` with main's `ClientCors.cs` restored.
4. The seeded equivalence check (`benchmark/scripts/api-cors-path-equivalence.cs`).
5. Release builds of before and after, then `dotnet build-server shutdown`.
6. Reserves the other three ts-heavy slots (it would exit 73 without starting an arm if any slot were taken).
7. Starts the external CPU observer (`api-cpu-observer.py`, the established one) and `vmstat`.
8. Runs 3 alternating pairs (before/after, after/before, before/after). Each arm is a fresh process with
   `TERRABASE_VS_TURSO_LANES=api WORKLOADS=read CONCURRENCY=1,16 SECONDS=5 REPEATS=1`: the keyed synthetic test-issuer
   token, the fixed 10 s warmup and 5 s window, and the engine timing denominator and attribution unchanged from main.
9. Runs `benchmark/scripts/api-cors-span-summary.py`.

## Correctness evidence

| Check | Result |
| --- | --- |
| Fast, candidate, whole suite (Debug) | TerraBase.Fast 3181/3181 passed, 0 skipped; Turso.Data.Sqlite.Tests 44/44; build 0 warnings |
| Fast, main's matcher, `ClientCorsTests` (Debug) | 34/34 passed (14 existing + 20 new path cases), so the new cases describe main's contract |
| Equivalence, main's Split predicate vs span matcher | 5,000,000 seeded random paths (237,071 matching) plus 6 fixed edge cases (null, empty, separator-only), 0 disagreements |
| `make fmt-check` | passed |

The new Fast cases are a real-HTTP preflight per path, each on its own server. They check: every data prefix, mixed case,
a trailing separator, extra segments, empty trailing segments, a missing or empty project/database, a doubled separator,
`v2`, `v1x`, and `project`/`projectsx`/`database`/`change`/`changesx` lookalikes. CI has not run on this branch (no PR).

## Results (run b1, all arms on beta; raw artifacts kept on beta)

Raw: `beta:~/remote-builds/terrascale/fix-api-cors-span-path-dd6840fd…/benchmark/bench/cors-span-b1/` (job log, per-arm
`.md`/`.log`/`.host`/`.window`, `cpu-processes.jsonl`, `vmstat.txt`, build and test logs, hashes).

The quiet gate was declared in the summary script before the run: in each arm window, the external mean must be at most 25 %
of one core, the external p95 at most 100 %, and no external build or compile process may reach 10 %. All six arms passed and
no arm was dropped or retried. Load average was about 11 at the first arm because the job's own builds and tests had just
finished (owned, not external). CPU PSI some avg10 was about 8 %.

B/op and CPU µs/op are process-wide, client and server in one process (`GC.GetTotalAllocatedBytes` / `TotalProcessorTime`).
Server, engine and overhead are hosting-activity wall time, including instrumentation, not CPU.

### Quiet gate

| Arm | samples | external mean % | external p95 % | max external builder % | verdict |
| --- | ---: | ---: | ---: | ---: | --- |
| before-1 | 69 | 7.9 | 18.8 | 0.0 | pass |
| after-1 | 69 | 7.7 | 20.8 | 0.0 | pass |
| after-2 | 70 | 8.1 | 19.8 | 0.0 | pass |
| before-2 | 69 | 7.4 | 16.8 | 0.0 | pass |
| before-3 | 69 | 7.5 | 16.8 | 0.0 | pass |
| after-3 | 70 | 7.9 | 21.8 | 0.0 | pass |

Run gate: PASS

### Cells (api, read; ranges are min–max of 3 repeats; paired Δ = after − before within a repeat)

| Size | Conc | Metric | before range | after range | paired Δ (r1, r2, r3) | median paired Δ |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| small | 1 | B/op | 30,606–30,667 | 30,114–30,119 | -549, -492, -512 | -512 |
| small | 1 | CPU µs/op | 228.3–229.8 | 226.5–231.4 | -3.3, 3.1, 0.7 | 0.7 |
| small | 1 | ops/s | 5,973–5,978 | 5,928–6,019 | 40, -45, 19 | 19 |
| small | 1 | client p50 µs | 156.0–156.6 | 155.2–157.6 | -1.4, 1.6, 0.0 | 0.0 |
| small | 1 | client p99 µs | 352.8–356.7 | 347.1–379.6 | -9.6, 9.5, 26.8 | 9.5 |
| small | 1 | overhead p50 µs | 87.8–88.1 | 87.7–88.8 | -0.1, 0.7, -0.1 | -0.1 |
| small | 1 | overhead p99 µs | 163.7–169.1 | 160.5–166.0 | 1.5, -3.1, -6.3 | -3.1 |
| small | 1 | engine p50 µs | 4.1–4.1 | 4.0–4.1 | -0.1, 0.0, -0.1 | -0.1 |
| small | 1 | engine p99 µs | 148.6–157.6 | 146.6–171.4 | -4.5, 21.5, 13.8 | 13.8 |
| small | 1 | server p50 µs | 92.1–92.3 | 91.7–93.0 | -0.4, 0.7, -0.1 | -0.1 |
| small | 16 | B/op | 29,935–29,997 | 29,432–29,497 | -549, -498, -500 | -500 |
| small | 16 | CPU µs/op | 196.3–197.7 | 196.3–201.2 | 0.5, 4.9, -1.4 | 0.5 |
| small | 16 | ops/s | 34,678–35,738 | 34,575–35,154 | -334, -104, -901 | -334 |
| small | 16 | client p50 µs | 343.4–348.8 | 348.0–351.9 | 2.9, 3.1, 5.0 | 3.1 |
| small | 16 | client p99 µs | 1,991.5–2,063.9 | 2,046.7–2,118.7 | 26.4, 47.0, 127.2 | 47.0 |
| small | 16 | overhead p50 µs | 108.6–110.2 | 108.2–111.4 | 1.3, 2.4, -2.0 | 1.3 |
| small | 16 | overhead p99 µs | 1,521.9–1,580.5 | 1,564.9–1,629.8 | 36.1, 49.3, 105.0 | 49.3 |
| small | 16 | engine p50 µs | 5.3–5.3 | 5.1–5.3 | -0.2, 0.0, -0.2 | -0.2 |
| small | 16 | engine p99 µs | 11.5–12.1 | 11.2–11.4 | -0.3, -0.9, -0.2 | -0.3 |
| small | 16 | server p50 µs | 114.2–115.8 | 113.5–117.1 | 1.1, 2.4, -2.3 | 1.1 |
| medium | 1 | B/op | 46,431–46,499 | 45,989–46,006 | -502, -425, -510 | -502 |
| medium | 1 | CPU µs/op | 230.5–231.0 | 228.8–233.9 | -0.3, 3.4, -1.8 | -0.3 |
| medium | 1 | ops/s | 5,651–5,698 | 5,600–5,715 | -38, -71, 64 | -38 |
| medium | 1 | client p50 µs | 164.1–165.1 | 162.9–167.1 | 0.8, 2.6, -2.2 | 0.8 |
| medium | 1 | client p99 µs | 402.3–414.6 | 388.8–406.5 | -8.1, -4.5, -13.5 | -8.1 |
| medium | 1 | overhead p50 µs | 93.0–94.0 | 92.7–95.4 | 1.2, 1.4, -1.2 | 1.2 |
| medium | 1 | overhead p99 µs | 174.4–179.2 | 177.3–180.4 | 6.0, -1.4, -1.3 | -1.3 |
| medium | 1 | engine p50 µs | 4.9–5.1 | 4.8–5.0 | -0.1, -0.1, -0.1 | -0.1 |
| medium | 1 | engine p99 µs | 154.0–167.6 | 155.0–167.7 | 0.1, 4.5, 1.0 | 1.0 |
| medium | 1 | server p50 µs | 98.0–99.1 | 97.5–100.4 | 1.1, 1.3, -1.4 | 1.1 |
| medium | 16 | B/op | 45,389–45,444 | 44,886–44,953 | -550, -502, -491 | -502 |
| medium | 16 | CPU µs/op | 220.5–224.2 | 221.6–226.3 | 0.6, 5.8, -2.6 | 0.6 |
| medium | 16 | ops/s | 28,669–29,156 | 28,688–29,417 | 603, -468, 299 | 299 |
| medium | 16 | client p50 µs | 392.8–396.3 | 388.4–398.4 | -4.5, 5.6, -1.1 | -1.1 |
| medium | 16 | client p99 µs | 2,088.3–2,182.9 | 2,134.7–2,204.5 | -45.4, 116.2, -12.1 | -12.1 |
| medium | 16 | overhead p50 µs | 115.9–117.8 | 116.0–119.5 | 2.3, 2.3, -1.8 | 2.3 |
| medium | 16 | overhead p99 µs | 1,593.8–1,651.1 | 1,611.4–1,633.9 | -39.7, 38.9, 5.8 | 5.8 |
| medium | 16 | engine p50 µs | 6.8–6.9 | 6.3–6.6 | -0.5, -0.3, -0.4 | -0.4 |
| medium | 16 | engine p99 µs | 15.1–15.2 | 13.7–14.7 | -1.4, -0.5, -0.6 | -0.6 |
| medium | 16 | server p50 µs | 122.9–124.8 | 122.6–126.3 | 1.8, 2.0, -2.2 | 1.8 |

Errors across all arms: 0. Overall: VALID

## Reading

- **Allocation: measured benefit.** Process B/op fell in all 12 paired cells, by 425–550 B/op; the median paired Δ is
  −500 to −512 B/op per cell. The before and after ranges do not overlap in any cell. This matches the profile's
  448–498 B/request attribution. Relative to the totals it is about 1.1–1.7 % of the 30–46 KB/op the process allocates.
- **CPU and latency: no claim.** CPU µs/op, client p50/p99, server, overhead and engine before/after ranges overlap in
  every cell, and the paired deltas change sign. This shows no speedup and does not move the <10 % overhead target;
  overhead stays about 88–118 µs p50 against engine 4–7 µs.
- **Watch item, not a finding:** in small/c16, all three paired deltas were positive for overhead p99 (+36 to +105 µs) and
  client p99, and negative for ops/s. The ranges overlap and the medium/c16 deltas change sign, so 3 pairs cannot tell this
  apart from noise. There is no plausible mechanism in a matcher that only removes allocations.

## Limits

- One host and one run (3 pairs). This is the loopback single-node api lane only: no live gru/sjc/fra, no TLS, no SDK lane.
- Synthetic test-issuer token in the test binary only. No production requests, tenants or credentials.
- The equivalence check copies both predicates on strings, so it shows the matchers agree, not that Kestrel delivers the
  same `PathString`. The Fast preflight cases cover the real path on both builds.
