# .NET parity create-timeout reconciliation controls, 2026-10-28

An HTTP 408 create response cannot establish that the owned database was never
created. The lifecycle layer now keeps that outcome `uncertain`, so a subsequent
404 delete still emits `reconcile_required: true`. An accepted exact-name delete
clears reconciliation while retaining `remote_erasure: unverified`.

| Control | Create state | Deletion | Reconciliation | Run outcome |
|---|---|---|---|---|
| Create 408, delete 404 | uncertain | absent | required | rejected, setup:408 |
| Create 408, delete 204 | uncertain | logical_accepted | not required | rejected, setup:408 |

Code: lifecycle `0bf082ec8`; tested evidence head
`9d5eda942cfadac68b1a6ed835d0dc2d5b1c2bf2`. Every file under
`benchmark/clients/dotnet` is identical between those heads. The change is one
classification guard, its namespace import and two fake-transport lifecycle
cases; it preserves all deadlines and cleanup calls. This is a follow-up to the
historical stack qualification, not a claim that its old heads contained this fix.

| Delta `make check` through `ts-heavy-any nice -n 19` | Result |
|---|---|
| SDK-preparer controls | 14 passed |
| Locked restore | passed |
| Release build | passed, 0 warnings/errors |
| Format verification, all three projects | passed |
| Parallel benchmark tests | 196 passed, 0 failed/skipped |

SDK pin remains `36d114877a69d18fba33b27ad362bd3e68e9e7ff`.
Three initial launches failed with `cat: -: Invalid argument` and a make broken
pipe before any build or test result. The preparer passed independently under
shell tracing. The unchanged check then exited 0 with output redirected inside
the heavy job. This implicates the output relay, but does not establish its
underlying cause. No checks were skipped or weakened.

| Log on delta | SHA-256 |
|---|---|
| `/tmp/perf-api-dotnet-parity-408-check-d.log` (successful full check) | fc175b361ee11c9449f37bb68fc659c6c40b0e306cceff5dccd6470aed04ad7b |
| `/tmp/perf-api-dotnet-parity-408-check.log`, `-check-b.log`, `-check-c.log` (three identical failures) | fd9fc8b8a84eea34fba1bf43a6262467f6ec618af66847d2fbbe7ab66efb3349 |
| `/tmp/perf-api-dotnet-parity-408-preparer-debug.log` | a0114b0bcf8093f6374e56c7276cbe4b847f06b2c2096cf9abcf45509da475da |

No live calls, tenant or credentials were used; SDK/dependency preparation is
separate from the fake HTTP controls. TLS, ALPN, real signal delivery and remote
erasure are not established. Required CI has not run on these updated heads.
The earlier Go run-d database remains unresolved; this correction does not
reconcile it. The original <10% target and its engine denominator are unchanged.
