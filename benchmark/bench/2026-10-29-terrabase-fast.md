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

The independently source-linked Slow graph also builds: `dotnet build tests/TerraBase.Slow/TerraBase.Slow.csproj --no-restore --nologo -p:UseSharedCompilation=false` completed in **52.84 s**, with zero warnings or errors. This verifies moved scenarios still compile independently.

A four-CPU-affinity CI rehearsal on mariogk-dev completed locked restore, the full checked Fast/E2E/driver build, both single-file/native packaging proofs, SHA verification and the shard archive in **254 s**. Disabling ReadyToRun only for the CI packaging proof reduced that rehearsal from **282 s**; production image builds retain ReadyToRun. The isolated single-file driver proof passed all **24** tests, with no skips. This rehearsal excludes GitHub setup/upload overhead and is advisory; the actual main CI run remains required.

The benchmark owner's fix landed as `ab95ae37f`. Rebased onto that fix and shipped through `ts-ship` as `ca5093d3a565fab15b7dc97cdeffb6877d80de49`. Every affected area's exact-commit status passed below 300 seconds on mariogk-dev, with isolated concurrent builds:

| Gate | Restore/build/test wall time |
| --- | ---: |
| libs | 42.161 s |
| auth | 259.438 s |
| TerraBase | 215.134 s |
| benchmark | 32.037 s |

The packaged shard outputs also ran successfully with four-CPU affinity, without rebuilding. Each archive was extracted into a fresh source tree. The complete 406 TerraBase tests were selected exactly once:

| Shard | TerraBase tests | Assigned extra proofs | Wall time |
| --- | ---: | --- | ---: |
| 1 | 172 | — | 58 s |
| 2 | 59 | 24 driver tests | 92 s |
| 3 | 109 | — | 48 s |
| 4 | 66 | 3 local E2E tests | 51 s |

All passed, with zero skips.

## Main CI proof

The TerraBase jobs on main push `ca5093d3a565fab15b7dc97cdeffb6877d80de49` passed on four-vCPU GitHub runners: [run 40968149450](https://github.com/TerraScale/terrascale/actions/runs/40968149450). These are full job wall times, including checkout/setup and artifact transfer:

| CI job | Before wall time | After wall time | After tests | Result |
| --- | ---: | ---: | ---: | --- |
| terrabase-build | 466 s | 212 s | 24 single-file driver checks | Passed |
| terrabase (1) | 315 s | 45 s | 172 | Passed |
| terrabase (2) | 249 s | 47 s | 59 + 24 driver | Passed |
| terrabase (3) | 96 s | 36 s | 109 | Passed |
| terrabase (4) | 52 s | 48 s | 66 + 3 local E2E | Passed |

All 406 TerraBase tests passed exactly once; the 24 driver tests and three assigned local E2E tests passed, with no skips. The packaging proof separately runs the same 24 driver behaviors from an isolated single-file bundle and checks the extracted native SHA.

That run exposed one unused `BindingTests` import in the library style gate. It was removed without changing assertions or the gate, and the complete `make libs-check` passed on beta. The correction shipped as `bfa4234e113e876ca82fb8baa5140f217ce64a4f` after `ts-ship/libs` passed in **18.064 s**. Replacement main CI: [40968917311](https://github.com/TerraScale/terrascale/actions/runs/40968917311). Its complete libraries job passed all **92** tests with zero skips in **100 s**, including restore, formatting/style checks, build and test execution.


The replacement main push also passed every TerraBase job with the same test counts and zero skips:

| Job | Complete job wall time |
| --- | ---: |
| terrabase-build | 202 s |
| terrabase (1) | 50 s |
| terrabase (2) | 40 s |
| terrabase (3) | 32 s |
| terrabase (4) | 41 s |

Both required local commands and all five TerraBase CI jobs are below the five-minute budget. Native fetching, assertions, formatting, analyzers, packaging and SHA checks remain enforced.
