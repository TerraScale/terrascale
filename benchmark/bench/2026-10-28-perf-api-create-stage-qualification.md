# Database-create stage metric qualification, 2026-10-28

Code head: `319b9f224519a302513ff206a4570e0281fabd78`, branch
`fix/terrabase-database-create-stage-metrics`. Evidence is stacked separately to
keep the production/test change at 393 changed lines. No performance claim,
production request, tenant or credential was involved.

## Qualification on delta

The worker exited 0 through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 bash -c
… --repo terrascale --branch fix/terrabase-database-create-stage-metrics`.
It checked the exact head and SDK pin, downloaded the pinned Turso asset, ran
`make fmt-check`, then ran `make terrabase-test-fast` separately for each filter
below. Builds used the Makefile default Debug configuration and warnings as
errors. The worker checked a clean tracked tree after completion.

| Filter | Passed | Failed / skipped |
|---|---:|---:|
| DatabaseCreateStageMetricsTests | 3 | 0 / 0 |
| DatabaseRecoveryTests | 103 | 0 / 0 |
| CatalogCleanupRaceTests | 3 | 0 / 0 |
| ConditionalBranchTests | 12 | 0 / 0 |
| ManagementApiTests | 23 | 0 / 0 |
| Turso.Data.Sqlite.Tests (each of five invocations) | 44 | 0 / 0 |
| Formatting | Passed | — |

Each filter used `TEST_ARGS="--treenode-filter /*/*/<class>/*"`. Locked restore,
Fast and driver builds completed with zero warnings and errors. The SDK was
`/home/mariogk/remote-builds/ts-dotnet-36d114877a69`, verified at
`36d114877a69d18fba33b27ad362bd3e68e9e7ff` through `TsDotnetRoot`.
Output was redirected inside the worker to avoid a previously observed relay
failure; no checks were skipped or weakened.

## Review corrections and scope

Independent source review of the preceding `6ff2d9549` found no production
control-flow regression. Exact-head qualification failed on an unused import.
A throwaway diagnostic removed only that import: two new tests passed and the
HTTP abort test failed in three attempts because it expected zero pending
cleanup intents. DatabaseRecoveryTests passed 103/103 in that diagnostic.

At `create.intent`, an intent has committed but publication is not marked yet.
Injecting an exception there deliberately leaves one intent for recovery. The
corrected HTTP control aborts its real Kestrel request and injects cancellation
at that existing test seam. It checks the exact metric sequence, waits for its
own server's cleanup measurement, and asserts one retained intent, no registered
database and no database file. This tests injected server-side cancellation,
not client-initiated cancellation propagation. No production seam was added.

The report's successful checks apply to `319b9f224`, including the corrected
assertion and removed import. The production metric records bounded stage and
outcome labels; a repeated cleanup attempt can produce another cleanup interval.
`cleanup:completed` does not establish erasure or absence of recovery work.

Full Fast and required CI have not run for this head. The metric covers future
creates, not the prior fra timeout. The owned name
`api-perf-01m610ttq4n8ew5y4q25aj5sc2` remains `reconcile_required`; remote erasure
is unverified. No new tenant attempt is authorized by this evidence.

## Retained logs on delta

| Log | SHA-256 |
|---|---|
| `/tmp/perf-api-create-stage-319b9f224-check.log` (successful qualification) | `156ede57a48e9e3a5c9d951949651be14e157d710a34e0c45caa637abc3e5384` |
| `/tmp/csm-review-6ff2d9549.log` (exact-head build failure) | `96a8d25fba338d4ba917e75685319f0f72df81d67fcad3939cde3feb2f88e507` |
| `/tmp/csm-diag-6ff2d9549.log` (throwaway diagnostic) | `bf08ca3057872846c5c4c275d29bdcaba652e51b21897708be6a7ec4a0cbf079` |
