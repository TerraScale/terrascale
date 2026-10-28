# Writer billing and response controls, 2026-10-28

Test-only source `8befa94d0`, based on main `86c7545af`. No production code or
configuration changes; the rejected native-writer optimization is not applied.

| Real HTTP control on delta | Result |
|---|---|
| Throw after an unflushed writer advance | Clean error JSON; billed bytes equal response |
| Client abort before flush | No unflushed bytes billed |
| Writer flushes across a fake-clock minute boundary | Per-minute and active reporting match received bytes |
| Unflushed writer followed by stream write | Per-minute and active reporting match received bytes |
| Writer followed by response start | Only transferred bytes billed in each minute |
| Targeted Fast suite | 5 passed, 0 failed, 0 skipped |

Command: `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 env
TsDotnetRoot=<qualified SDK checkout> bash
benchmark/scripts/usage-writer-controls-20261028.sh --repo terrascale --branch
fix/usage-writer-error-controls`. It verifies/downloads the pinned Turso asset,
then uses `make terrabase-test-fast` filtered to `UsageWriterTests`. Tests run
in parallel with their own real server, database and fake clock. They register
only test endpoints through the existing endpoint-registration seam.

Artifacts remain on delta under
`~/remote-builds/terrascale/fix-usage-writer-error-controls-d9d980e63761b5327ca00c01678778dc2cd526d7832d3621d64f7719b592b03a/benchmark/bench/usage-writer-controls-20261028-a/`.
The full Fast suite has not run locally; required CI must gate merge. These
controls protect response/billing semantics and establish no performance win.
