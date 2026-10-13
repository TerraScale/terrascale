# SQL and document value regressions — 2026-10-13

Source revision: `e30ffdc287530551b161b0130220be53d3c0475e` (both local `origin/main` and the remote main ref when inspected).
Test artifact: `tests/TerraBase.Slow/Management/QueryValueRegressionTests.cs`, in the existing `TerraBase.Slow` integration executable.

Validated local test/source head: `091f1fe7885bf68aee790bc845ca988d9e32c868`. Focused compilation **passed**, exit **0**, **0 warnings / 0 errors**, **38.35 seconds**. The seven focused integration tests **passed: 7 total / 7 succeeded / 0 failed / 0 skipped**, exit **0**, **8.141 seconds**. Session `18384` acquired the existing lock and completed; no local waiter remains. Exact commands, source/binary hashes and logs are recorded below. The original failed test and intermediate failed compilation remain visible.

PR172 remains open/draft at remote head `6b7805355a2fb5caf918001ac1e272ebe50d0d89`; the local corrections are not pushed. Its required Fast CI is failed, independently of this successful focused local result. Publication remains an open dependency; no merge is authorized.

## Scope and contracts

Seven independent tests start real local Kestrel listeners with the existing `ManagementApi` / `TerraBaseTestServer` harness. Each owns its server, temporary storage and clients; TUnit retains its default parallel execution. Writes use the real document PUT route. No production source, existing tests, shared fixture, project, build or workflow files change.

The inspected contracts are:

- `src/TerraBase/Features/Management/Queries/SqlSubset.cs` and `QueriesExtensions.cs`: read-only SELECT subset; signed 64-bit integer literals, finite double literals, string and boolean literals; placeholders are rejected. `SqlRequest` in `IQueryService.cs` has only `Sql`, with no parameter-binding input.
- `src/TerraBase/Features/Management/Queries/QueryService.cs`: whole-document and dotted-field projections, null for missing projected members, result limits and truncation.
- `src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Queries.cs`: actual parameter binding, `json_extract` comparisons, case-sensitive prefix ranges and deterministic id ordering.
- `src/TerraBase/Primitives/DocumentJson.cs`, `ExactNumber.cs`, and the serialization methods in `Engine/Lattices/DocumentState.cs`: JSON objects, preserved JSON value types and numeric tokens, at most 128 significant digits, and magnitudes from `1e-307` to below `1e308` (or zero).

Coverage groups:

1. Stored JSON and SQL projections: null, empty string/array/object, booleans, signed 64-bit bounds, 128-digit numbers, fractional precision, exponent bounds, negative zero, Unicode, embedded NUL and escaped characters.
2. SQL predicates: null versus missing, empty and ordinary strings, boolean versus string literals, adjacent integers around `2^53` and both signed 64-bit endpoints, exact composed/decomposed Unicode, quotes and Unicode prefix ranges.
3. Result/error semantics: empty collection and empty filter results, exact and exceeded LIMIT, honest named/positional placeholder rejection, integer/decimal overflow, unsupported UNION/subquery shapes and malformed SQL.
4. Cancellation: cancel after the first actual engine row, assert the next read throws, dispose the canceled transaction, then query again through a fresh read transaction and the HTTP SQL route. This covers cancellation at a deterministic consumer boundary; it does not claim interruption inside a native call.

## Collision inspection and gaps

There is no checkout `.agents` directory or additional nested test instruction file. The referenced sibling `terrabase-rust` contract checkout is unavailable; assertions use the implemented C# contracts above.

Open PR file lists were inspected for #125, #152, #165–170, #116–117, #120–121. Relevant diffs of #116, #121, #168 and #169 were read: they own storage/catalog or replication/harness changes, including `SqliteStorageTransaction.Queries.cs`, `DocumentFiles.cs` and `FakeChangeGroupSource.cs`. None owns the new test file. Those files remain untouched. These tests are based on main, not the held replication candidates.

No vector, FTS or view-query route is implemented in the inspected TerraBase query surface. No coverage is claimed for those features, HTTP request validation, cursors, replication, backup/restore, auth, imports or health.

SQL uses SQLite scalar comparisons; document queries also apply type checks. This artifact does not define a new cross-type coercion or arbitrary-precision SQL comparison contract. Its exact-number assertions cover storage/projection preservation and signed integer equality.

## Focused reproduction

SDK: `11.0.100-rc.1.26425.128`, as pinned in `global.json`.
The existing Slow project references ts-auth, which references the sibling SDK. This machine's SDK checkout is `/home/mariogk/Projects/TerraScale/ts-dotnet`, revision `df852e3b4d3a4df324452d46792b20566eea5136` when inspected.

The Makefile's Slow target builds the entire TerraBase solution filter. To respect the requested focused local validation, use the project directly with the existing shared build lock:

```sh
flock -w 1800 /tmp/ts-heavy.lock timeout 240 nice -n 19 \
  env TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet \
  dotnet build tests/TerraBase.Slow/TerraBase.Slow.csproj \
  --configuration Debug -p:RestoreLockedMode=true --nologo -v minimal

flock -w 1800 /tmp/ts-heavy.lock timeout 180 nice -n 19 \
  env TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet \
  dotnet run --project tests/TerraBase.Slow --configuration Debug --no-build -- \
  --treenode-filter '/*/*/QueryValueRegressionTests/*' --minimum-expected-tests 7
```

## Recorded outcome

The focused Debug compilation **passed** at test/source head `6b7805355a2fb5caf918001ac1e272ebe50d0d89`, with **0 warnings, 0 errors**, elapsed **31.11 seconds**, exit **0**. Its log is `/tmp/query-value-focused-build-queued2.log` (9,065 bytes when inspected). The first focused test run at that head **failed: 7 total, 6 succeeded, 1 failed, 0 skipped**, duration **4.408 seconds**, exit **2**. Log: `/tmp/query-value-focused-tests-head6b780535.log`; preserved HTML: `/tmp/query-value-test-head6b780535-report.html`. The failure is retained in [issue #177](https://github.com/TerraScale/terrascale/issues/177). Three earlier bounded build attempts failed to acquire `/tmp/ts-heavy.lock`:

| Attempt | Lock wait | Exit | Evidence |
|---|---:|---:|---|
| Initial baseline build | 60 seconds | 1 | `/tmp/query-value-baseline-build.log`: 0 bytes; dotnet never started |
| Focused build with new tests | 600 seconds | 1 | `/tmp/query-value-focused-build.log`: 0 bytes; dotnet never started |
| First queued focused build | 1,800 seconds | 1 | `/tmp/query-value-focused-build-queued.log`: 0 bytes; dotnet never started |

At the initial inspection, one process held the lock and seven processes were waiting. The holder subsequently changed, but this build never acquired it. This is contention evidence, not a source regression or a passing build. Those admission failures did not execute Slow; the later actual Slow fixture failure is separately recorded in issue #177.

The composite JSON fixture and all 13 other raw JSON literals were parsed locally with Python; the composite contains the intended 128-digit integer, signed integer bounds and embedded NUL. Both new files pass `git diff --cached --check`. These are fixture/whitespace checks, not C# compilation or integration verification.

Full repository checks and the complete Fast/Slow suites have not run locally. Shared release CI is contended and no duplicate full build is requested.

The third focused admission expired before dotnet started. Its single replacement (session `17037`, waiter PID `2163185`, enqueued 2026-10-13 02:47 UTC) acquired the same lock and completed the successful build above. The subsequent single filtered test request (session `46259`, waiter PID `2237111`) also acquired the same lock and produced the failed 6/1/0 result. Both have exited. Sanitized snapshots established continuing release progress through holder turnover (`1977256` → `2026998` → `2036181`) and fresh compiler/test descendants. No environment values or command-line arguments were read for this inspection.

Focused local validation is complete at `091f1fe7885bf68aee790bc845ca988d9e32c868`: successful compilation and all seven tests passed without skips. Original-head validation remains a failed 6/1/0 run, and the intermediate lifecycle correction remains a failed compilation. No shared fixture or project edit was needed. The residual dependency is publication of the local corrections to the existing draft: required Fast checks at the remote head remain failed, and the root `AGENTS.md` requires checks to pass before pushing. No full-repository local build, duplicate CI rerun or merge was requested; existing release and failure owners retain their work. No waiter remains and no further run is needed for the unchanged test artifact.

## Exact-head Fast CI classification

Test/source head: `6b7805355a2fb5caf918001ac1e272ebe50d0d89`. Source base: `e30ffdc287530551b161b0130220be53d3c0475e`. This section records the original remote head. A later local fixture correction is recorded separately below; no production input changes.

The full `terrabase` job checkout log (`/tmp/query-value-pr172-ci-full-job.log`) establishes actual CI checkout `b1989bbf98a940c0fb15cb379beca0f98ee2dbf3`, GitHub's merge of that PR head into the exact source base. GitHub's commit API and local `git rev-parse '6b7805355a2fb5caf918001ac1e272ebe50d0d89^{tree}'` both give tree `e231092c7d6667053d02fad926d341e0748085c2`; the CI checkout and PR head therefore have identical file contents. The CI SDK checkout is `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`; the local build used the separately recorded sibling SDK revision.

- [Current-head run 38336376782](https://github.com/TerraScale/terrascale/actions/runs/38336376782): Fast **1,433 total, 1,406 succeeded, 27 failed, 0 skipped**, duration **18m47.320s**. `changes` succeeded; `terrabase` and aggregate `test` failed; `libs`, `auth`, `preview`, `deploy`, `e2e` and `slow` were skipped. The seven new Slow tests were not executed by this run.
- [Existing source-base run 38317822337](https://github.com/TerraScale/terrascale/actions/runs/38317822337): Fast **1,433 total, 1,414 succeeded, 19 failed, 0 skipped**, duration **5m59.104s**. No baseline rerun was requested.
- Both logs record `dotnet run --project tests/TerraBase.Fast --configuration Release --no-build -- --maximum-parallel-tests 16`. Local logs: `/tmp/query-value-pr172-ci-run38336376782.log` and `/tmp/query-value-baseline-ci-run38317822337.log`; sanitized structured associations: `/tmp/query-value-pr172-ci-failures.json` and `/tmp/query-value-baseline-ci-failures.json`. Table line numbers refer to these raw downloaded job logs.

### Causation evidence and its limit

`git diff --name-status e30ffdc287530551b161b0130220be53d3c0475e HEAD` reports only the two reserved additions. `git diff --exit-code` reports no changes to production, Fast, harness, shared build inputs, Dockerfile or workflows. At base and head these trees are identical:

| Tree | Git tree SHA at both revisions |
|---|---|
| `src` | `0c10eefa87f878cc02a82ef82e836ac4b30d0523` |
| `tests/TerraBase.Fast` | `8898db45a2bfab8cbb09766f899e7bc716d518cb` |
| `tests/TerraBase.TestHarness` | `435da9eddb565024eb38c35a0334f2c4e7852811` |

The actual Fast project has its own default C# compile items. `Directory.Build.targets:51` references production TerraBase, and `:58` links only `tests/TerraBase.TestHarness/**/*.cs`; neither includes nor references Slow. `Makefile:168` builds Fast and the vendored driver tests, then runs their separate executables. Dockerfile's `test-terrabase` stage runs that target. The new file therefore has no identified executable path into Fast; this conclusion comes from the project/build graph and unchanged source, rather than its suite label alone.

**17 current failures are baseline-confirmed by exact name and exception: 15 missing-revision health checks, one health shutdown cancellation, one credential-expiry timeout. The remaining 10 were not observed in that baseline log.** They occur in unchanged source, and direct causation by the new test code is unsupported. They remain unconfirmed runtime failures; do not describe all 27 as proven baseline failures or dismiss the ten as flaky. Adding a file invalidates Docker's whole-source COPY layer, so a fresh build/test execution and changed resource conditions can expose timing-sensitive behavior even with identical Fast source.

The unchanged `SuiteDiagnostics.cs:77` defines the runtime CSV fields. Near completion, base/current samples record elapsed **357.765/1,124.061s**, CPU **1,351.589/1,316.853s**, and accumulated GC pause **258.839/847.348s**. These are evidence of differing execution conditions, not a controlled proof that contention caused any individual failure.

### All 27 exact failures

“Confirmed” means the existing source-base log has the same exact name and exception kind. “Not observed” means no matching failed entry in that log; it does not establish that the test passed there.

| Exact test invocation | Exception | Baseline match | Current / baseline log line |
|---|---|---|---|
| `Fleet_configuration_reads_are_bounded_before_platform_work_and_stop_when_all_jobs_are_busy` | TimeoutException | Not observed | 408 / — |
| `Eight_stalled_customer_key_reads_preserve_platform_restore_and_key_refresh_capacity` | TimeoutException | Not observed | 645 / — |
| `Automatic_checkpoints_across_several_passes_keep_one_generation` | TimeoutException | Not observed | 811 / — |
| `Oversized_multibyte_entries_still_consume_raw_snapshot_and_WAL_plan_budgets(True)` | TaskCanceledException | Not observed | 980 / — |
| `Repeated_writes_invalidate_each_touched_stamp_once_at_commit` | AssertionException | Not observed | 1066 / — |
| `Every_database_gets_its_scheduled_backup_when_more_are_due_than_one_pass_takes` | TimeoutException | Not observed | 1248 / — |
| `An_operator_key_with_an_expiry_stops_authenticating_when_it_expires` | TimeoutException | Confirmed | 1531 / 1145 |
| `Recommendations_keep_counting_recorded_fields_but_stop_recording_new_ones_at_the_cap` | HttpRequestException | Not observed | 1634 / — |
| `Unreadable_records_remain_intact_without_blocking_other_shapes_or_databases_from_flushing(wrong_shape_id)` | HttpRequestException | Not observed | 1690 / — |
| `Client_probes_enforce_region_pins_without_an_api_key(/healthz)` | ArgumentNullException | Confirmed | 1762 / 697 |
| `Probes_answer_on_the_client_listener_without_an_api_key` | ArgumentNullException | Confirmed | 1851 / 735 |
| `Client_probes_enforce_region_pins_without_an_api_key(/readyz)` | ArgumentNullException | Confirmed | 1875 / 759 |
| `Readyz_is_unavailable_once_the_storage_engine_is_closed` | ArgumentNullException | Confirmed | 1899 / 808 |
| `Readyz_reuses_completed_disk_checks_for_the_configured_ttl(null, 1)` | ArgumentNullException | Confirmed | 1923 / 931 |
| `Readyz_concurrent_requests_await_one_probe_when_the_clock_starts_at_zero` | ArgumentNullException | Confirmed | 1947 / 832 |
| `Readyz_is_unavailable_when_shutdown_begins_during_a_probe` | ArgumentNullException | Confirmed | 1972 / 857 |
| `Readyz_reuses_completed_disk_checks_for_the_configured_ttl(2, 2)` | ArgumentNullException | Confirmed | 1997 / 907 |
| `Readyz_keeps_one_disk_probe_after_timeout_or_cancellation_and_recovers(True)` | ArgumentNullException | Confirmed | 2021 / 783 |
| `Readyz_keeps_one_disk_probe_after_timeout_or_cancellation_and_recovers(False)` | ArgumentNullException | Confirmed | 2047 / 882 |
| `Healthz_reports_ok_and_the_running_version` | ArgumentNullException | Confirmed | 2072 / 1003 |
| `Readyz_reuses_completed_disk_checks_for_the_configured_ttl(60, 60)` | ArgumentNullException | Confirmed | 2096 / 979 |
| `Readyz_is_unavailable_when_the_data_directory_is_not_writable` | ArgumentNullException | Confirmed | 2120 / 955 |
| `Readyz_is_ready_on_the_admin_listener` | ArgumentNullException | Confirmed | 2144 / 1051 |
| `Readyz_leaves_no_probe_files_in_the_data_directory` | ArgumentNullException | Confirmed | 2168 / 1027 |
| `Readyz_shutdown_bypasses_a_cached_healthy_result` | TaskCanceledException | Confirmed | 2192 / 1075 |
| `Storage_sampling_covers_more_databases_than_a_minute_samples_whatever_order_they_list_in` | TimeoutException | Not observed | 2252 / — |
| `Catalog_transactions_and_snapshots_larger_than_a_page_replicate_whole` | TimeoutException | Not observed | 2379 / — |

### Representative diagnostics and owner handoff

1. **15 health contract failures, all baseline-confirmed.** `ArgumentNullException: value was null (Parameter 'stringValue')` → `HealthTests.AssertProbeAsync`, `tests/TerraBase.Fast/Platform/HealthTests.cs:503` → `Healthz_reports_ok_and_the_running_version`, `:23`. Line 503 matches `ServiceVersion.Revision` against a SHA regex; revision is null when informational build metadata is absent (`src/TerraBase/Hosting/ServiceVersion.cs:15`). Docker excludes `.git` (`.dockerignore:1`); `ARG TERRABASE_REVISION` exists only in the later publish stage (`Dockerfile:85`), not the test build. Hand this unchanged CI/test-build metadata dependency to its release/health owners.
2. **Seven 120-second deadlines: one baseline-confirmed, six additional.** Example: `TimeoutException: Test 'Fleet_configuration_reads_are_bounded_before_platform_work_and_stop_when_all_jobs_are_busy' timed out after 00:02:00` → `TUnit.Engine.Helpers.TimeoutHelper.ExecuteWithTimeoutAsync(...)`; accompanying canceled-execution frames name `BackupAdmissionTests.cs:258` and `ContinuousBackup.cs:171`. The log explicitly labels the timeout-handler trace as distinct from the blocked test's stack. Four additional deadlines belong to backup, one to storage sampling and one to catalog replication. The credential-expiry deadline at `CredentialApiTests.cs:86` also occurs at baseline. Preserve these failures for their existing owners; no root blocking operation is inferred from the handler stack.
3. **Three additional HTTP 400 failures.** Document cache: `AssertionException: expected OK, received BadRequest`, body `invalid_query: query too expensive` → `DocumentCacheTests.ListAsync`, `DocumentCacheTests.cs:810` → `Repeated_writes_invalidate_each_touched_stamp_once_at_commit`, `:487`. Index recommendations: `HttpRequestException: 400 (Bad Request)` → `IndexApiTests.cs:248` during a 256-condition SQL request. Query-shape flushing: the same exception → `QueryShapeApiTests.PostAsync`, `:472` → fixture `:328`. The latter two response bodies are absent from their logged exception; their error codes are not invented. Baseline also has two different SQL HTTP 400 failures (`Sql_equality_range_order_in_and_prefix_like_use_the_index_and_match_the_same_rows`, log line 406, and `Wide_document_projections_resolve_many_fields_without_repeated_member_scans`, line 594), which supports a broader existing problem but does not establish the same failure for these three names.
4. **One additional backup cancellation.** `TaskCanceledException: A task was canceled` → `HttpClient.HandleFailure` → `S3ObjectStore.SafeAsync`, `ObjectStore.cs:268` → `S3ObjectStore.ListAsync`, `:138` → `ContinuousBackup.RetainAsync`, `ContinuousBackup.Maintenance.cs:86` → fixture cleanup, `CustomerBackupListingBoundaryTests.cs:224`. This is the local object-store integration fixture, not a new live service request. Hand the cancellation/cleanup evidence to backup owners.
5. **One baseline-confirmed health shutdown cancellation.** `TaskCanceledException` → `HealthTests.HeldShutdown.StopAsync`, `HealthTests.cs:377` → `ApplicationRunCompletion.RunAsync`, `ApplicationRunCompletion.cs:22` → `TerraBaseTestServer.Factory.CleanupCoreAsync`, `TerraBaseTestServer.cs:567` → `Readyz_shutdown_bypasses_a_cached_healthy_result`, `HealthTests.cs:285`. Both base and current logs have these representative frames; the health/fixture owner retains this failure.

The existing owners should assess these invariants before release. No product source, owned tests, shared fixtures or build files were changed; no CI rerun, cancellation, skip or retry was used to alter the outcome. The original local focused failure and the corrected-fixture validation are recorded separately below; current-head CI failures remain visible.

## Cancellation fixture correction

The original head's failing test was `Cancelling_between_SQL_rows_releases_the_reader_and_allows_the_next_query`. Its expected `OperationCanceledException` assertion passed. It failed on the next query in the same transaction (`QueryValueRegressionTests.cs:240`) with `InvalidOperationException: The transaction has completed`.

Representative original frames:

```text
SqliteCommand.EnsureExecutable — src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/SqliteCommand.cs:601
SqliteStorageTransaction.HasTable — src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Collections.cs:51
SqliteStorageTransaction.QueryCommand — src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Queries.cs:299
SqliteStorageTransaction.ReadBudgetedAsync — src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.cs:663
QueryValueRegressionTests.Cancelling_between_SQL_rows_releases_the_reader_and_allows_the_next_query — tests/TerraBase.Slow/Management/QueryValueRegressionTests.cs:240
```

The same-transaction reuse expectation was incorrect. `IStorageTransaction.cs:257–265` promises cancellation with `OperationCanceledException`, without promising transaction survival. `docs/terrabase/turso-spike.md:300–302` documents that a failed/stopped SELECT can end its transaction and its later commands are refused, and that closing a reader steps the remaining statement while the cancellation-aware budget stops it. The query cleanup in `SqliteStorageTransaction.cs:737–771` disposes the reader before releasing its operation gate.

Only the reserved new test was corrected: retain the first-row assertion and deterministic cancellation assertion, dispose the canceled reader and transaction, then assert both rows through a fresh read transaction and the HTTP SQL route. No production fix, assertion of same-transaction survival, skipped test, sleep or parallelism change is introduced. This is a fixture contract correction; the original failed run remains reported and is not relabeled as passing.

- Original test/source head: `6b7805355a2fb5caf918001ac1e272ebe50d0d89`; test SHA256 `0ec03f89bd3d6e6f898d4a498d5167774b6968e870ff27ff01a8b0ae2b5d0185`; DLL SHA256 `c9f7f0dfd99666d3c52aba1652cd54891a3c056178de88d47038b3128afc2823`; outcome **6 passed / 1 failed / 0 skipped**.
- Intermediate lifecycle correction head: `c281ab570b9ef06a3bfce1355edf5378135fd71d`; test SHA256 `be35ee0d52c1ada0db5d3d0837be6687f68cf0bc26699803090d67cad629530e`. Its compilation failure is recorded below; the later local candidate was validated separately. The remote PR head remains the original head.
- First corrected admission: session `22609`, waiter PID `2282133`, expired after 1,800 seconds, exit **1**, empty outer log `/tmp/query-value-corrected-validation-c281ab57.log`. Neither compilation nor tests started. A sanitized snapshot at 2026-10-13 04:03:24 UTC showed fresh release compiler processes, justifying one bounded replacement after the original waiter exited.
- First replacement: session `54985`, waiter PID `2478904`, enqueued approximately 2026-10-13 04:05:33 UTC, expired after 1,800 seconds, exit **1**, confirmed exited at 04:35:32 UTC. Outer log `/tmp/query-value-corrected-validation-c281ab57-replacement.log` remained empty; neither compilation nor tests started.
- At the sanitized inspection at approximately 2026-10-13 04:09 UTC, holder PID `2260234` was `flock`, state `S`, elapsed **3,157 seconds**; one holder and eight queued waiters existed. Our sole waiter was `flock`, state `S`, elapsed **243 seconds**, queued, with an empty outer log. Holder descendants included `TerraBase.Fast` PID `2479677`, state `SNl`, elapsed **235 seconds**, establishing active release testing. Only PID, process name/state, elapsed time and lock-held/queued counts were read, without environment values or command-line arguments.
- At 2026-10-13 04:35:32 UTC, after that replacement exited, the sanitized inspection showed one holder and six queued requests. Holder PID `2260234` was `flock`, state `S`, elapsed **4,715 seconds**; fresh descendant `csc` PID `2631029` was running (`RNl`), elapsed **33 seconds**. This actual release progress justified one further bounded replacement, without duplicating a live waiter.
- Second replacement: session `79260`, waiter PID `2633748`, enqueued at 2026-10-13 04:35:59 UTC, expired after 1,800 seconds, exit **1**, confirmed exited at 05:06:10 UTC. Outer log `/tmp/query-value-corrected-validation-c281ab57-replacement2.log` was **0 bytes**; the corrected build and test logs did not exist. Neither compilation nor tests started.
- At the sanitized inspection at approximately 2026-10-13 05:06 UTC, after that waiter exited, one holder and two queued requests remained. Holder PID `2262034` was `flock`, state `S`, elapsed **6,517 seconds**, a turnover from the previous holder `2260234`. Descendants included `TerraBase.Fast` PID `2683865`, state `SNl`, elapsed **269 seconds**, and fresh `TerraBase.Fast` PID `2722969`, state `RNl`, elapsed **0 seconds**. Holder turnover and newly started Fast processes justified one further bounded replacement, without duplicating a live waiter.
- Third replacement: session `91415`, waiter PID `2724868`, enqueued at 2026-10-13 05:06:42 UTC, expired after 1,800 seconds, exit **1**, confirmed exited at 05:36:58 UTC. Outer log `/tmp/query-value-corrected-validation-c281ab57-replacement3.log` was **0 bytes**; the corrected build and test logs did not exist. Neither compilation nor tests started.
- At the sanitized inspection at approximately 2026-10-13 05:37 UTC, after that waiter exited, one holder and one queued request remained. Holder PID `2618273` was `flock`, state `S`, elapsed **4,093 seconds**, a turnover from the previous holder `2262034`. Descendant `TerraBase.Fast` PID `3306406` was `SNl`, elapsed **63 seconds**, alongside `dotnet` PID `3305016`, `SNl`, elapsed **65 seconds**. Holder turnover and newly started Fast work justified one further bounded replacement, without duplicating a live waiter.
- Fourth replacement: session `78189`, waiter PID `3366644`, enqueued at 2026-10-13 05:37:57 UTC, with admission bounded at **3,600 seconds**. It **acquired the existing lock** and ran the focused compilation at head `c281ab570b9ef06a3bfce1355edf5378135fd71d`. Compilation **failed**, exit **1**, **0 warnings / 1 error**, elapsed **54.38 seconds**; the request had exited when inspected at 05:57:37 UTC. The failure was in the reserved new fixture: `QueryValueRegressionTests.cs(230,19): error IDE0063: 'using' statement can be simplified`. This is an owned test syntax error, not another admission timeout or a product-source regression. The conditional wrapper exited before running tests; no corrected test counts were generated.
- Failed-build log associations: `/tmp/query-value-corrected-validation-c281ab57-replacement4.log` and `/tmp/query-value-corrected-build-c281ab57-replacement4.log`, both **7,941 bytes**. No `/tmp/query-value-corrected-tests-c281ab57-replacement4.log` exists. The failed build is preserved alongside the original failed test run.
- The syntax was corrected only in the reserved new test, commit `091f1fe7885bf68aee790bc845ca988d9e32c868`, test SHA256 `470fd053fe739e98ccfcb9b1edc2d282440946ddbf6a87996ee59a2c390efb1c`. The reader now uses an `await using` declaration inside the same outer transaction block; it still disposes before its cancellation source and transaction, and before the fresh read. No assertions, timeouts, parallelism, production code or shared settings changed.
- Completed request: session `18384`, waiter PID `93186`, enqueued approximately 2026-10-13 05:59:35 UTC, for test/source head `091f1fe7885bf68aee790bc845ca988d9e32c868`. Admission was bounded at **3,600 seconds**, build/test execution at **240/180 seconds**, and the whole admitted operation at **420 seconds**. It acquired the existing lock and ran both commands sequentially with `nice -n 19` and the recorded `TsDotnetRoot`. The earlier sanitized inspection at approximately 06:00 UTC showed one holder and two queued requests; our sole request was queued (`flock`, `Ss`, elapsed 33 seconds), with an empty outer log. Holder `3539886` (`flock`, `S`, elapsed 1,131 seconds) had fresh `TerraBase.Fast` descendant `107163` (`SNl`, elapsed 7 seconds), confirming release progress. The preceding failed request had exited; there was no duplicate local waiter.
- Final result, verified after completion at 2026-10-13 06:22:26 UTC: focused build **passed**, exit **0**, **0 warnings / 0 errors**, elapsed **38.35 seconds**; tests **passed**, exit **0**, **7 total / 7 succeeded / 0 failed / 0 skipped**, duration **8.141 seconds**. The test log completed at 06:20:09 UTC. The HTML report was decoded locally and its seven test names, class and namespace matched the source exactly, with every status `pass`. Test assembly informational metadata is `1.0.0+091f1fe7885bf68aee790bc845ca988d9e32c868`; TerraBase's is `0.1.0+091f1fe7885bf68aee790bc845ca988d9e32c868`. Source SHA256 remains `470fd053fe739e98ccfcb9b1edc2d282440946ddbf6a87996ee59a2c390efb1c`; test DLL SHA256 is `764c26b99b0124e586dda9fd4b62ef1b38a92f00322ef33c8458abe2256aabb4`.
- Final logs: `/tmp/query-value-focused-validation-head091f1fe7.log` (**12,794 bytes**, SHA256 `7400d39947238ffebc74fa813b50969e313a7b1158d1c2b6f43c1a15dfb45de5`); `/tmp/query-value-focused-build-head091f1fe7.log` (**7,281 bytes**, SHA256 `7ccef5e6452cf3aee4fc50535033b5eb636793dfb5072d2196147f0c2eaab989`); `/tmp/query-value-focused-tests-head091f1fe7.log` (**5,513 bytes**, SHA256 `48d0b5620f350b41280e9d393be36d9e9d0404bc69dde0e6b5c7214c44a6dfc8`). Preserved HTML: `/tmp/query-value-test-head091f1fe7-report.html`, SHA256 `e0f2d68b49d17c4a0648c185983b72e82725fdfb6ebaaaff038fd49991d1f922`; sanitized named results: `/tmp/query-value-test-head091f1fe7-results.json`. No local CI URL exists; this is local focused validation, separate from CI38336376782.
- The completed waiter is no longer alive or queued and holds no lock. No replacement or further test run was started after success. The remote PR head remains `6b7805355a2fb5caf918001ac1e272ebe50d0d89`; neither local correction is pushed. Both earlier actual failures remain in this report and issue177.

Exact executed tests, from the decoded report:

| Test method in `TerraBase.Slow.Management.QueryValueRegressionTests` | Result |
|---|---|
| `Document_values_round_trip_through_storage_and_SQL_without_losing_JSON_types_or_digits` | Pass |
| `SQL_distinguishes_empty_strings_and_boolean_literals_from_null_or_missing_fields` | Pass |
| `SQL_integer_binding_keeps_adjacent_values_distinct_at_64_bit_and_double_precision_boundaries` | Pass |
| `SQL_strings_bind_quotes_and_Unicode_without_normalizing_or_interpreting_their_contents` | Pass |
| `Empty_SQL_results_and_exact_limit_boundaries_report_only_the_rows_that_exist` | Pass |
| `SQL_check_rejects_unbound_parameters_numeric_overflow_and_unsupported_shapes` | Pass |
| `Cancelling_between_SQL_rows_releases_the_reader_and_allows_the_next_query` | Pass |

Exact combined replacement command (one admission, no separate build/test waiter):

```sh
printf '%s\n' "$$" > /tmp/query-value-focused-waiter-head091f1fe7.pid
exec flock -w 3600 /tmp/ts-heavy.lock timeout 420 nice -n 19 \
  env TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet bash -c '
timeout 240 dotnet build tests/TerraBase.Slow/TerraBase.Slow.csproj \
  --configuration Debug -p:RestoreLockedMode=true --nologo -v minimal \
  > /tmp/query-value-focused-build-head091f1fe7.log 2>&1
query_value_compile_exit=$?
cat /tmp/query-value-focused-build-head091f1fe7.log
if [ "$query_value_compile_exit" -ne 0 ]; then exit "$query_value_compile_exit"; fi
timeout 180 dotnet run --project tests/TerraBase.Slow \
  --configuration Debug --no-build -- \
  --treenode-filter "/*/*/QueryValueRegressionTests/*" --minimum-expected-tests 7 \
  > /tmp/query-value-focused-tests-head091f1fe7.log 2>&1
query_value_tests_exit=$?
cat /tmp/query-value-focused-tests-head091f1fe7.log
exit "$query_value_tests_exit"
' > /tmp/query-value-focused-validation-head091f1fe7.log 2>&1
```
