# Deletion retention focused integration evidence — 2026-10-23

Worktree: `fix/deletion-retention`, based on main `26a438f1ac7950cf503c318a1b2eef685427fbd3`. These are local focused integration results, not production erasure evidence. Every build/test ran at low CPU priority; suites retained their normal parallel execution.

| Scope | Observed result | Covered behavior |
| --- | --- | --- |
| Engine `DatabaseErasureTests` | 5 passed, 0 skipped; 1.725 s | Held document reader, backup reader, salvage copy, measure and read; permanent rejection after customer deletion |
| Management `DatabasePurgeTests` after receipt-race correction | 6 passed | Configured nine-day PITR gives fixed 39-day backup deadline; overdue/missing holder; restart and catalog GC; stale-row fences; unknown neighbor preservation; Forget versus customer deletion; late-authority held-reader race |
| Management recovery compatibility after receipt-race correction | 9 passed | Two abandonment races and seven recovery-failure cases |
| Auth deletion cleanup | 5 new cases passed; corrected periodic case also passed five repeats | Failure/restart, reused email, unrelated tenant, controlled 31-day overdue recovery, 105-record bounded paging, late-token/API-key fences, all invitation states and owner handoff remaining pending |
| Existing account deletion | 2 passed | Account closure and last-owner behavior preserved |
| `DatabaseBackupPurgeTests` | 8 passed, 0 skipped; 4.228 s | Protected 128-version pages, same-key continuation and restart, independent historical destinations, expired mutation budget, multipart fairness, last snapshot and known/chunked oversized inventory |
| Vendor driver regressions during backup test target | 22 passed | Existing native driver integration gate |
| Live backup expiry | Initial 23 focused cases passed; six fairness/regression cases passed after review | Hard-expired baseline/WAL, last bucket snapshot, failed creation, denied/transport-failed/timed-out early WAL does not starve later batches or snapshot expiry; failed passes preserve prior size |

The storage command used the already built current Fast binary:

```sh
nice -n 19 dotnet run --project tests/TerraBase.Fast --no-build --no-restore -- --treenode-filter '/*/*/DatabaseErasureTests/*'
```

The backup command used the repository target, including its driver tests:

```sh
nice -n 19 make terrabase-test-fast TEST_ARGS='--treenode-filter /*/*/DatabaseBackupPurgeTests/*' TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet
```

Time-controlled failures remain pending/overdue until fresh absence verification succeeds; test clock advancement does not claim deletion during storage outages. Holder tests synthesize remote receipts in the real local catalog and are not a live multi-node disk-erasure drill. Shared auth/SYSTEM physical history, durable cross-service account/project handoff and unknown offline copies remain unresolved. TS-873 must stay open.
