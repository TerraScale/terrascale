# Performance verification — 2026-09-07

Evidence inventory captured 2026-09-08T00:14:37.667793+00:00. Repository HEAD at inventory: `14f166282af80bc44cb004fd957947278a2eef85`. Concurrent edits mean HEAD alone does not identify tested source.

| Existing evidence | Result | Scope / limitation |
| --- | --- | --- |
| `/tmp/terrabase-engine-bench.log` | 3 passed, 0 failed; 109.21 s | Prior release engine commissioning artifact; not current-source qualification |
| `/tmp/terrabase-materializer-bench.log` | 1 passed, 0 failed; 81.83 s | Prior materializer benchmark |
| `/tmp/terrabase-ack-bench.log` | ACK timing regression: 1 passed, 0 failed; 2.31 s | Other filtered integration binaries ran zero tests and provide no coverage |
| `/tmp/terrabase-fts-bench.log` | 1 passed, 0 failed; 66.96 s; FTS top-10 27.956705 ms | Prior release artifact `target/release/deps/terrabase-94c9bf760c69a9e0` |
| `/tmp/terrabase-lane-tests.log` | 36 passed, 0 failed; 0.01 s | Predates subsequent lane changes |
| `/tmp/terrabase-lane-clippy.log` | Finished dev profile in 20.80 s | Prior lane source; original shell exit code not retained here |
| `/tmp/terrabase-sequence-check` | Standalone regression reported passed by parent | Copies exact schema/source regression; not workspace qualification |

The original shell exit codes and build-time source manifests for the preceding logs are not available in this inventory; test counts above were independently read from logs. No new live benchmark was run by verification.

Initial Rust/manifest source hash inventory: `/tmp/terrabase-performance-verification-20260907/initial-source-sha256.json` (SHA-256 `e6b7821b913014c7a338ab85d763aacc271972d0637dc77817b51498dd50a89c`). Initial process snapshot: `/tmp/terrabase-performance-verification-20260907/initial-processes.txt`. Existing external cargo jobs and benchmark processes are left untouched.

## Planned qualification

1. Obtain each worker's changed-file list, discriminating regression selectors, and readiness; root must agree the optimized server source freeze before its build.
2. Capture a source manifest before and after each build; reuse one matching test executable per crate. Record executable SHA-256 and command exit code.
3. List selected tests first, require a nonzero count, then execute bounded selectors at nice 19. A zero-test result never qualifies a regression.
4. Schedule broader checks only after targeted qualification and coordination with the external shared-target workload. Rust builds and Makefile commands run at nice 19.
5. Live benchmark load belongs to `benchmark_completion`; timing under unrelated load is not silently treated as isolated performance evidence.

Current-source qualification is pending worker readiness and coordinated builds.
