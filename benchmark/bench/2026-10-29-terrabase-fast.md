# TerraBase and libs Fast: five-minute budget

Measurement host: beta, 12 logical CPUs. Commands include locked restore and build:

```sh
TS_HEAVY_HOST=beta ts-heavy-any bash -lc \
  'time make libs-test TEST_ARGS="--report-trx --report-trx-filename baseline-libs.trx"' \
  --repo terrascale --branch <revision>
TS_HEAVY_HOST=beta ts-heavy-any bash -lc \
  'time make terrabase-test-fast TEST_ARGS="--report-trx --report-trx-filename baseline-terrabase.trx"' \
  --repo terrascale --branch <revision>
```

Native assets are fetched and SHA-256 verified automatically by the changed build targets.
The first baseline downloaded assets before the timed commands.

## Before

Revision: `a283e5f25788ad6abbb9d1e451a5219173c2982b`.

| Command | Wall time including build | Tests | Result |
| --- | ---: | ---: | --- |
| `make libs-test` | 17.099 s | 135 (89 endpoints + 46 logging) | Passed |
| `make terrabase-test-fast` | 470.766 s | 2,155 (2,109 TerraBase + 46 driver) | 182 existing TerraBase failures |

CI source: [main run 40948941246](https://github.com/TerraScale/terrascale/actions/runs/40948941246).
These are failing baseline measurements; the separate Fast-green worker owns those failures.

| CI job | Job wall time | TerraBase Fast tests | Failed |
| --- | ---: | ---: | ---: |
| terrabase-build | 466 s | — | 0 |
| terrabase (1) | 315 s | 722 | 156 |
| terrabase (2) | 249 s | 514 | 3 |
| terrabase (3) | 96 s | 588 | 11 |
| terrabase (4) | 52 s | 285 | 11 |
| Total Fast | — | 2,109 | 181 |

Each class duration below sums its TRX per-test durations; concurrent work overlaps,
so these values must not be added to obtain suite wall time.

| Class | Summed test time |
| --- | ---: |
| ExplainSqlBoundaryTests | 839.87 s |
| DatabaseRecoveryTests | 272.79 s |
| HistoryErasureBackupTests | 198.63 s |
| BackupBacklogRecoveryTests | 173.83 s |
| CustomerBackupNetworkTests | 147.16 s |
| StorageBeginCancellationTests | 120.75 s |
| BackupApiTests | 114.73 s |
| UsageApiTests | 96.06 s |

Per-test baseline durations: [CSV](2026-10-29-terrabase-fast-before.csv).
The old Makefile did not forward TRX arguments to the driver; its baseline count comes from the runner log.

## Coverage changes

Fast now covers the main features only, following the user's 2026-10-29 order. Edge,
limits, abuse, recovery-fault, stress and scheduler timing scenarios live in Slow
or were deleted where redundant. Assertions for retained cases remain strict.

- Keep document create/read/update/delete, conditional writes, merge/operation patch,
  transactions, pagination, collection access, queries, indexes and vector search.
- Keep management, authentication/revocation, tenant isolation, backup/restore, import,
  change feeds, shape clients, storage and platform workflows.
- Move whole exhaustive classes and split mixed API methods; helpers are source-linked
  from TestHarness so Slow compiles and runs independently.
- Remove the repeated `DIAG_memory_exhaustion` diagnostic and redundant parameter rows.
- Share the default document host across the assembly; each test has its own organization,
  project and database. Customized and host-global tests retain dedicated hosts. A dedicated
  regression proves cross-tenant denial and independent client disposal.
- Replace revocation polling with fake time. Await vector backfill directly and assert
  its public active state. Move backup scheduler polling to Slow.
- Keep library routing/binding/OpenAPI and structured logging main workflows. Logging
  contexts use AsyncLocal and run in parallel.
- Keep driver connection, command, transaction, encryption, persistence and native-loading
  contracts; remove finalizer stress and corruption/race/buffer matrices.
- Fetch missing native assets automatically through the existing SHA-256 verifier and
  downloader. No verification bypass was added.
- Use host CPU capacity for Fast parallelism. CI builds only the graphs consumed by Fast
  shards and archives only their executable outputs; formatting, analyzers and both
  single-file packaging proofs stay enforced.

## Final measurements

Rebased onto `ac82173cb`. The measured tree is `94a5a7585`; `b81cf85a9` adds only this baseline report/data. Beta has 12 logical CPUs. All repeated commands include restore and build, use host parallelism, and request TRX output through `TEST_ARGS`. No tests were skipped.

| Run | `make libs-test` | `make terrabase-test-fast` | Result |
| --- | ---: | ---: | --- |
| 1 | 8.753 s | 49.283 s | Passed |
| 2 | 7.668 s | 41.533 s | Passed |
| 3 | 7.824 s | 36.375 s | Passed |
| 4 | — | 35.461 s | Passed |
| 5 | — | 37.040 s | Passed |

| Suite | Before tests | After tests |
| --- | ---: | ---: |
| TerraBase Fast | 2,109 | 406 |
| Turso driver | 46 | 24 |
| TerraBase target total | 2,155 | 430 |
| Endpoints | 89 | 50 |
| Logging | 46 | 42 |
| Libs target total | 135 | 92 |

Per-test after durations for every repeat: [CSV](2026-10-29-terrabase-fast-after.csv).

`ts-ship` also passed this candidate on mariogk-dev: libs **17.192 s**, auth **90.789 s**, TerraBase **64.200 s**. Its benchmark check failed `TestParityRunnerMeasuresEveryRegionThenCleansUp` and reached the 299-second deadline, so it correctly did not push. The benchmark owner has been notified through the parent.

Live main CI proof is pending shipping. A separate four-CPU build rehearsal is in progress.
