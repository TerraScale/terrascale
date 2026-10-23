# Backup key-cache Slow evidence — 2026-10-22

Issues [#147](https://github.com/TerraScale/terrascale/issues/147), [#153](https://github.com/TerraScale/terrascale/issues/153), and [#156](https://github.com/TerraScale/terrascale/issues/156) do not reproduce on the tested main baseline. Each assigned test passed **20 fresh repetitions beyond its green baseline: 21/21 each, 63/63 total**, with zero failures or skips. No backup/key-cache source or test change was needed.

## Tested environment

- TerraScale SHA: `e7fa366ddb289f5612c93d316b316fb92c568151` (main at investigation start; pushed evidence branch `fix/slow-keys-evidence`).
- Host: `beta`, NixOS 26.05, x64, 12 logical CPUs; .NET SDK `11.0.100-rc.1.26425.128`, runtime `11.0.0-rc.1.26425.128`, TUnit `1.73.19`, Microsoft Testing Platform `2.5.1`.
- Sibling ts-dotnet SHA used by the baseline build: `e7d9cf9a8c310f7c54940318b76253dd8dda7a81`. Repetitions reused the built output without rebuilding.
- The tests start real TerraBase Kestrel listeners with isolated directories, ports, and synthetic data. FakeS3 supplies object storage and wrapped keys; the test clock controls expiry. No live vendor credentials were required.
- TUnit's default parallel execution remained intact. The filtered run selects exactly the three assigned tests; every TRX was checked for three passes on `beta` and no skipped executions.

## Assigned tests

- #147: `BackupKeyCacheTests.An_idle_cold_node_reloads_a_retained_key_once_after_both_key_caches_expire`.
- #153: `BackupKeySaturationTests.A_full_65536_entry_cache_reuses_a_cold_key_for_concurrent_empty_wal_segment_restores`.
- #156: `BackupKeyCacheTests.A_cold_node_reuses_more_than_1024_historical_keys_across_repeated_restores`.

## Classification and existing fixes

All three reports are **stale for the tested current-main workload**. The historical reports contain timeouts, shutdown cancellations, and startup 503s, rather than a reproduced current key-cache assertion failure.

The old fix relevant to the startup 503 signature is [`0df6897bbe6cbbbe490ae779265514eba51cd300`](https://github.com/TerraScale/terrascale/commit/0df6897bbe6cbbbe490ae779265514eba51cd300), “Fix catalog startup scans and frozen-clock backup fixtures” (#255), committed 2026-10-14 03:18:31 UTC. It replaces 4,096 empty-bucket startup queries with one indexed page. The last recorded #147/#153 failures were reported around 03:07 UTC that day; #156's latest reports were earlier. This fix is already an ancestor of the tested SHA. Its relevance to startup readiness is supported by the changed code; the exact commit responsible for every historical timeout/cancellation was not isolated.

The saturation fixture also already includes [`6b3dbc3a577b44ffb9ff38f6c77c664b77a79c37`](https://github.com/TerraScale/terrascale/commit/6b3dbc3a577b44ffb9ff38f6c77c664b77a79c37) (accept empty segments sharing a start) and [`3290a26bc8722e46a61eb65d06f081b14e356a44`](https://github.com/TerraScale/terrascale/commit/3290a26bc8722e46a61eb65d06f081b14e356a44) (close fixture histories with snapshots). Both predate some later timeout reports, so neither is claimed as the timeout fix.

The full Slow suite was intentionally not run. These repetitions establish the current targeted behavior; they do not rule out failures under a different full-suite contention profile. No assertions, deadlines, cache sizes, or concurrency settings were weakened.

## Commands and results

Baseline (Release build succeeded with zero warnings/errors, 50.90 seconds; runner passed 3/3 in 76.233 seconds):

```bash
TS_HEAVY_HOST=beta ~/.local/bin/ts-heavy-any bash -c '
  set -euo pipefail
  evidence=/tmp/slow-keys-e7fa366d
  nice -n 19 make terrabase-test-slow CONFIGURATION=Release \
    TEST_ARGS="--treenode-filter /*/*/BackupKey*/* --minimum-expected-tests 3 --output detailed --no-ansi --report-trx --results-directory $evidence/baseline" \
    2>&1 | tee "$evidence/baseline.log"
' --repo terrascale --branch fix/slow-keys-evidence
```

Twenty fresh repetitions ran through the same wrapper **from an SSH session on beta**, using `env TS_HEAVY_SLOTS=3 TS_HEAVY_HOST=beta ~/.local/bin/ts-heavy-any bash -c '<loop>' --repo terrascale --branch fix/slow-keys-evidence`. The invocation-only third slot was explicitly authorized after the catalog batch released it; no host defaults or scripts changed. The previously queued two-slot wrapper was cancelled before it ran any repetition.

The loop checked the expected repository and SDK SHAs before and after all repetitions. Its test command for each `run` in `seq 1 20` was:

```bash
results=/tmp/slow-keys-e7fa366d/fresh-$run
nice -n 19 make -o terrabase-build terrabase-test-slow CONFIGURATION=Release \
  TEST_ARGS="-- --treenode-filter /*/*/BackupKey*/* --minimum-expected-tests 3 --output detailed --no-ansi --report-trx --results-directory $results" \
  >"/tmp/slow-keys-e7fa366d/fresh-$run.log" 2>&1
```

The loop stopped on failure and validated every TRX before advancing. All 20 fresh iterations completed. Durations below are seconds from TRX, rounded to three decimals; raw TRX and `fresh-durations.tsv` retain full precision.

| Run | #147 expiry | #153 saturation | #156 historical keys |
|---|---:|---:|---:|
| Baseline | 9.962 | 47.187 | 75.906 |
| 1 | 14.172 | 83.517 | 107.666 |
| 2 | 26.713 | 155.128 | 174.979 |
| 3 | 11.534 | 117.384 | 142.758 |
| 4 | 13.272 | 60.222 | 74.758 |
| 5 | 9.734 | 46.477 | 64.820 |
| 6 | 9.216 | 45.669 | 61.596 |
| 7 | 11.618 | 59.967 | 70.316 |
| 8 | 10.077 | 51.924 | 63.226 |
| 9 | 8.835 | 49.267 | 60.068 |
| 10 | 10.334 | 54.412 | 73.831 |
| 11 | 10.929 | 48.537 | 63.205 |
| 12 | 10.561 | 49.691 | 64.675 |
| 13 | 10.697 | 48.351 | 77.238 |
| 14 | 13.634 | 60.583 | 80.442 |
| 15 | 10.291 | 47.772 | 66.689 |
| 16 | 10.811 | 48.992 | 70.020 |
| 17 | 14.174 | 51.960 | 70.390 |
| 18 | 9.621 | 47.395 | 63.111 |
| 19 | 10.564 | 44.890 | 59.460 |
| 20 | 10.310 | 44.978 | 62.903 |

Fresh-only duration ranges: #147 8.835–26.713s; #153 44.890–155.128s; #156 59.460–174.979s.

## Evidence and handoff

Remote artifacts: `beta:/tmp/slow-keys-e7fa366d/`. Retained local copy: `/home/mariogk/.local/state/terrascale/slow-keys/2026-10-22-e7fa366d-beta/`. These include the baseline build/test log, 20 fresh run logs, 21 TRX files, HTML reports, host/SHA metadata, a 60-row fresh-duration ledger, and `SHA256SUMS`. All 43 checksums (21 logs, 21 TRX files, and the duration ledger) were independently verified against the local copy. Manifest SHA256: `b53a73eb71662d03a5f2d8fcbebf2e26f2894405cc9d9923108d8497c0607e3b`.

Issue bodies, all comments, and issue events were saved under `/tmp/backup-keys-evidence/issue-{147,153,156}.json` and `events-{147,153,156}.json` before classification. They include the historical failure SHAs and CI run URLs.

Backup/key-cache source and tests remain unchanged. The evidence supports stale closure for the targeted workload; full-suite contention remains unverified.
