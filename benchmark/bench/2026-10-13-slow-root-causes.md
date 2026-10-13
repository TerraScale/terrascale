# Slow suite root causes, 2026-10-13

Validation runs on mariogk-dev in isolated repository and ts-dotnet worktrees under `~/.local/bin/ts-heavy`.
The SDK checkout is pinned to `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. Release builds keep analyzers and warnings
as errors. Suite times below exclude compilation; they are diagnostic observations, not a runner speed comparison.

| Check | Base and change | Passed / total | Failed / skipped | Suite wall time | Result |
|---|---|---:|---:|---:|---|
| Auth Slow baseline | T0 `ae4bac41` | 23 / 25 | 2 / 0 | 65.949 s | Signing-key concurrency fixtures return HTTP 404 |
| Auth Slow fixed | Same base, project records seeded before replicas start | 25 / 25 | 0 / 0 | 43.098 s | Full suite passed |
| Native unread-reader regression | Same base, reset before finalization | 1 / 1 | 0 / 0 | 0.857 s | No unread callbacks or later SQL; prior writes survive commit; repeated abort is safe |
| TerraBase SQL cancellation regression | Same base, reader reset before cleanup | 1 / 1 | 0 / 0 | 4.458 s | Next query succeeds in the same transaction after cancellation between rows |
| Full TerraBase Slow | Same base plus both fixes | Pending | Pending | Pending | Running; no complete result claimed |

Auth reports [#199](https://github.com/TerraScale/terrascale/issues/199) and
[#200](https://github.com/TerraScale/terrascale/issues/200) were fixture errors: key creation atomically requires an
existing project, but the Slow fixture omitted it. The fix supplies the project records just as the existing Fast
fixture does. The production guard, races, assertions, test count and parallelism remain unchanged.

TerraBase report [#201](https://github.com/TerraScale/terrascale/issues/201) was reader cleanup: both managed close
and native finalization execute unread rows. After cancellation at a consumer boundary, cleanup invoked the cancelled
scan callback and Turso rolled back the transaction. The pinned native C API's `turso_statement_reset` releases the
unfinished read before finalization without evaluating unread rows. It also discards later batch statements. Existing
query budgets, rollback detection and the operation gate remain intact. The vendor patch includes the native regression
and documents its provenance; no native package upgrade is required.

| Docker `test` observation | Input | Total wall time | Auth Fast | TerraBase Fast | Limitation |
|---|---|---:|---|---|---|
| First complete run after host reboot | T0 `ae4bac41`, prepared Docker stages, no Slow fixes | 482 s | 664 / 664, 424.796 s | 1492 / 1558, 66 failures, 388.936 s | Older T0 base; `_system` replication errors; failed build; partial cache from interrupted earlier run |
| Current candidate | T0 `62b2c7bb` plus both fixes | Pending | Pending | Pending | Running; full required `make check` also running independently |

Reproduce full suites with `make auth-test-slow CONFIGURATION=Release TEST_ARGS=--no-ansi` and
`make terrabase-test-slow CONFIGURATION=Release TEST_ARGS=--no-ansi`. Run `make docker-test
TS_DOTNET_DIR=<pinned-sdk-worktree>` for the Docker `test` target, including its required named SDK build context.
Interrupted runs are excluded: the host reboot interrupted the first full TerraBase Slow and Docker runs.

This report has no merged PR, main-job result, live e2e result or deployment speedup yet. Workflow and preview pushes
are held for the T0 and T2 merges; production machines remain owned by T2.
