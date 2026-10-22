# Slow catalog and usage reproduction evidence — 2026-10-22

The reported failures in #158, #159, #198 and #197 did not reproduce on current main's
`e7fa366ddb289f5612c93d316b316fb92c568151`. Each unchanged test passed its baseline and
20 additional repetitions: **84 passes, zero failures, zero skips**. Classify these as
stale against this filtered current-main reproduction; their original full-Slow load
conditions have not been disproved. No implementation or test change is justified by
these results, and no issue was closed.

## Per-issue evidence

All issue bodies and comments were read with `gh issue view <number> --json body,comments`.
The observations below are the unchanged integration tests' assertions, not a separate
measurement of commercial billing.

| Issue | Historical failure | Current-main observation in every run | Additional repeats | Repeat duration range |
| --- | --- | --- | --- | --- |
| [#158](https://github.com/TerraScale/terrascale/issues/158) | Ten-minute timeouts at `90cf9046` and `e9389391`; unexpected 503 responses at `519c9ff9` and `5c2e65bd`. These do not establish a catalog/file mismatch. | Eight workers each complete ten create/collection/write/delete cycles with the expected statuses; the final catalog and `*.db` file counts are both zero. | 20/20; 1,600 complete cycles | 18.713–36.969 s |
| [#159](https://github.com/TerraScale/terrascale/issues/159) | At `90cf9046`, a rejected request returned `503 storage_busy` instead of `409 limit_exceeded`; the report does not show excess accepted creations. | Exactly three of twelve concurrent create-plus-limit commits succeed. All nine others return `409 limit_exceeded` at operation 1. Names, IDs and database files match; no `.creating` files remain. | 20/20; 60 accepted and 180 refused commits | 3.061–7.630 s |
| [#198](https://github.com/TerraScale/terrascale/issues/198) | Ten-minute timeout at `e9389391`; no successful over-limit response was reported. | Usage for 100 databases returns 200 with `databases=100`; after creating database 101, usage returns `400 invalid_query`, `query too expensive`. | 20/20; 2,020 databases created across isolated test servers | 16.052–33.579 s |
| [#197](https://github.com/TerraScale/terrascale/issues/197) | Ten-minute timeout at `e9389391`. | The tenant is Hobby; a transaction containing 1,024 creates returns 200 and the subsequent count is exactly 1,024. | 20/20; 20,480 creates confirmed by counts | 4.394–14.001 s |

No actual plan-limit bypass, extra accepted database, or catalog/file mismatch giving
free capacity was observed. The evidence does not meet the requested P0 threshold.
In particular, #159 tests the caller's explicit commit `limit` operation on Enterprise,
and #198 tests a usage request's work bound on Enterprise, not a commercial database
entitlement. #197 exercises the Hobby accounting path but does not independently
assert billed bytes or charges. `UsageMeter` explicitly describes its operation
counts as best-effort dashboard usage rather than billing.

## Execution identity

- Host: `beta`, NixOS linux-x64, runner reports 12 CPUs and server GC.
- Repository: `beta:~/remote-builds/fix-slow-catalog-evidence`; branch `fix/slow-catalog-evidence`.
  Main and the pushed evidence branch both resolved to the full SHA above before testing.
- .NET SDK: `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`;
  TUnit `1.73.19.0`, Microsoft Testing Platform `2.5.1`.
- Referenced ts-dotnet checkout: `beta:~/remote-builds/ts-dotnet`, SHA
  `e7d9cf9a8c310f7c54940318b76253dd8dda7a81`.
- Repeats: `2026-10-22T12:19:38Z` through `2026-10-22T12:28:07Z`.
  The baseline passed 4/4 in 30.571 s after a successful build with zero warnings/errors.

Both invocations ran through `ts-heavy-any` on beta with invocation-local
`TS_HEAVY_SLOTS=3 TS_HEAVY_HOST=beta`, `--repo terrascale --branch fix/slow-catalog-evidence`.
No script/default capacity was changed. Only this task's still-queued delta wrapper
was cancelled; no delta tests had started. No desktop host was used.

## Commands

The following commands ran inside the remote checkout through the heavy wrapper.
The four methods ran with default parallelism, separate servers and real listeners.
No assertion, timeout, plan policy or fixture clock was changed.

```bash
filter='/*/*/*/(Concurrent_creates_writes_and_deletes_in_one_project_keep_catalog_and_disk_in_step|Concurrent_database_creations_never_exceed_the_database_limit|Project_usage_refuses_more_databases_than_one_request_reads|A_hobby_transaction_commits_all_1024_operations_with_storage_accounting)'

# Baseline: build and run current-main tests through the existing Make target.
nice -n 19 make terrabase-test-slow CONFIGURATION=Release \
  TEST_ARGS="-- --treenode-filter \"$filter\" --minimum-expected-tests 4 --output detailed --no-ansi"

# Twenty additional fresh runner processes, reusing the successful baseline build.
for repeat in $(seq 1 20); do
  nice -n 19 dotnet run --project tests/TerraBase.Slow \
    --configuration Release --no-build -- \
    --treenode-filter "$filter" --minimum-expected-tests 4 --output detailed --no-ansi \
    > "/tmp/slow-catalog-e7fa366d/repeat-$repeat.log" 2>&1 || exit 1
done
```

The execution additionally checked the expected repository SHA and clean tracked files
before and after the repeat batch, checked four `passed` lines per repetition, and
recorded UTC boundaries and wall durations. Post-run verification checked all 21 logs
for the exact four method names and summaries `total: 4`, `failed: 0`, `succeeded: 4`,
`skipped: 0`, plus all twenty sequential `PASS repeat=` records.

## Relevant main history inspected

1. `2010ac17`: releases storage admission when closing a writer fails and preserves the
   committed response when later accounting fails; absent from the first failure's `90cf9046`.
2. `178b32e4` (#209): preserves a registered database copy during failed-create cleanup;
   already an ancestor of #158's latest reported `5c2e65bd` failure.
3. `745ea0d5` (#212) adjusts alpha test-host GC sizing; `f898cefc` installs the ordinary
   management fixtures' still query-deadline clock. These already precede the relevant
   later failures, so neither alone proves their cause or resolution.
4. `f653fb83` (#272): integrates A6 replica recovery/certification and changes the storage
   implementation after the reported failures.
5. `043d7f91` (#341): repairs main compilation after the direct merges; the tested baseline
   includes this repair. No single historical commit was isolated as the fix for these issues.

## Retained logs and limits

Remote logs: `beta:/tmp/slow-catalog-e7fa366d/`.
Verified local copies: `/home/mariogk/.local/state/terrascale/slow-catalog/2026-10-22-e7fa366d-beta/`.
The directory contains `baseline-four.log`, `repeat-1.log` through `repeat-20.log`,
`repeats-environment.log`, `repeats-summary.log`, `SHA256SUMS` and `verified-summary.json`.
All 23 log hashes matched the remote manifest. Manifest SHA-256:
`e771e620a2f6858e15b69ba1b7b8dad68ac1a3874dfa453a0d7fbf8bdc1bfca9`.

Full Slow, Fast execution, CI, live multi-region behavior and commercial billing were
not run. Passing filtered repetitions does not establish behavior under the original
whole-suite load, identify the historical timeout's cause, or prove a global quota guarantee.
This change adds evidence only; API, storage format and authentication behavior are unchanged.
