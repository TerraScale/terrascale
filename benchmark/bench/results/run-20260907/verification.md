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

## Frozen worker regression plan

| Change | Selector | Required new tests |
| --- | --- | --- |
| Query candidate retention | `query::execute::retention_tests::` | 4 |
| Lifecycle reader snapshot | `storage::sqlite::lifecycle_state::read_tests::` | 3 |
| Existing placement reader path | `storage::sqlite::write_usage::placement_read_tests::` | 2 |
| Borrowed batch ordering | `batch_efficiency_tests` | 2 |
| PUT causal snapshot | `api::tests::put_causal_snapshot::` | 2 |

Supporting selectors cover usage placement, query scan accounting/relevance/typed-SQL parity, paused lifecycle, bulk ingest, atomic/faulted batches, conditional create/replay, public billing allocation, and batch replay. Final selector names and source hashes are captured with the built artifact. Frontier regressions remain pending worker freeze.

## First consolidated debug cohort

`nice -n 19 cargo test -j 2 --lib -p terrabase -p terrabase-server --no-run --locked --message-format=json` exited **0**, 2 min 47 s. Full source manifests: `/tmp/terrabase-performance-verification-20260907/debug-before.json` and `debug-after.json`; compiler JSON/logs and exact exit are alongside them.

Engine source was unchanged during this build. The copied engine artifact SHA-256 is `cf1ce0f42f78bfc89775b642d44937cde3912ca71f43722295470b9ddbaac74a`. The copied server artifact SHA-256 is `68b2c965ab93eb3ccefe931ef1c972fa7086382a7cf22ba0e424ff5af6352bc5`. During compilation, bench sources and server `api/tests/batch_replay.rs`, `authorization_leases.rs`, `lib.rs`, `membership_revocations.rs`, and `runtime.rs` changed; server results qualify the copied artifact only. Artifacts and selector evidence are in `/tmp/terrabase-performance-verification-20260907/`.

| Selector group | Result |
| --- | --- |
| `query::execute::retention_tests::` | passed; 4 selected; exit 0 |
| `storage::sqlite::lifecycle_state::read_tests::` | passed; 3 selected; exit 0 |
| `storage::sqlite::write_usage::placement_read_tests::` | passed; 2 selected; exit 0 |
| `engine::usage_placement_tests::` | failed; 6 selected; exit 101 |
| `batch_efficiency_tests` | passed; 2 selected; exit 0 |
| `query::tests::result_statistics_report_the_scan_accounting` | passed; 1 selected; exit 0 |
| `query::tests::search_with_score_orders_by_relevance` | passed; 1 selected; exit 0 |
| `query::tests::query_input_normalizes_typed_and_sql_text_through_one_compile_seam` | passed; 1 selected; exit 0 |
| `storage::sqlite::tests::paused_lifecycle_rejects_writes_until_resume` | passed; 1 selected; exit 0 |
| `engine::ingest_tests::` | passed; 13 selected; exit 0 |
| `storage::sqlite::tests::group_commit_batches_are_atomic` | passed; 1 selected; exit 0 |
| `storage::sqlite::tests::faulted_batch_leaves_zero_divergence` | passed; 1 selected; exit 0 |
| `causal_frontier_tests` | passed; 7 selected; exit 0 |
| `origin_progress_survives_pruning_and_reopen_atomically` | passed; 1 selected; exit 0 |
| `pruning_preserves_a_gap` | passed; 1 selected; exit 0 |
| `origin_prefix_preserves_unsigned_hlc` | passed; 1 selected; exit 0 |
| `operation_kind_markers_tests` | passed; 5 selected; exit 0 |
| `engine::replication_outbox::tests` | passed; 1 selected; exit 0 |
| `engine::branch_restore_tests` | passed; 3 selected; exit 0 |
| `api::tests::put_causal_snapshot::` | failed; 2 selected; exit 101 |
| `api::tests::conditional_create::` | passed; 3 selected; exit 0 |
| `api::tests::conditional_replay::conditional_http_replay_preserves_original_result_after_intervening_changes` | passed; 1 selected; exit 0 |
| `api::tests::budget_http_tests::public_writes_consume_signed_included_and_paid_allocations_once` | passed; 1 selected; exit 0 |
| `api::tests::batch_replay::` | failed; 2 selected; exit 101 |

Each selector was first listed and required nonzero tests, then run against the copied artifact at nice 19 with two test threads and a 180-second timeout. Artifact hashes were unchanged after every selector.

Failures: stopped-operation replay expected `Committed` in `engine::usage_placement_tests` (5 passed, 1 failed); both new PUT and both batch replay tests received HTTP 401 rather than 201 while creating the test database. Owners are investigating. These failures block final qualification and optimized benchmark handoff. The compile also emitted warnings, so this is not clippy qualification.

Independent bench owner reports `nice -n 19 cargo test --manifest-path bench/Cargo.toml --bin terrabase-sync --bin terrabase-bench-lane` exit 0: sync 7 tests and main 42 tests (including four lane failure/healthy regressions); `/tmp/astra-sync-test.log`.

## Correction cohort

The corrected engine artifact SHA-256 `f815349233477d88fa365565bd29a74f6f6451b88ef70de152890b83412f15e5` passed all **35** selected tests (usage placement 6, lifecycle read 3, placement read 2, borrowed batch ordering 2, ingest 13, atomic group 1, billing retry 1, frontier 7). The earlier stopped-operation failure was a fixture that created an inconsistent standalone stop fence; the corrected test uses the authoritative deletion operation and retains operation-identity rejection coverage.

The combined correction build exited 101 because concurrent server OpenAPI changes exceeded the default macro recursion limit. The engine test artifact was produced successfully. Server retry after the documented recursion-limit correction exited 101 because concurrent engine shard split changes attempted to dereference `[u8; 16]` at `src/engine/shard_split.rs:824` and `:942`. Compilation and source manifests are recorded as `corrected-*` and `server-final-*` files in the evidence directory. These are build failures, not test passes. Final server tests and release artifact remain pending correction.
