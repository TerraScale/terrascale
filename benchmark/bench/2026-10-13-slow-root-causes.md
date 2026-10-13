# Slow suite root causes, 2026-10-13

Validation runs on mariogk-dev in isolated repository and ts-dotnet worktrees under `~/.local/bin/ts-heavy`.
The SDK checkout is pinned to `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. Release builds keep analyzers and warnings
as errors. Suite times below exclude compilation; they are diagnostic observations, not a runner speed comparison.

| Check | Base and change | Passed / total | Failed / skipped | Suite wall time | Result |
|---|---|---:|---:|---:|---|
| Auth Slow baseline | T0 `ae4bac41` | 23 / 25 | 2 / 0 | 65.949 s | Signing-key concurrency fixtures return HTTP 404 |
| Auth Slow fixed | Same base, project records seeded before replicas start | 25 / 25 | 0 / 0 | 43.098 s | Full suite passed |
| Auth Slow current candidate | T0 `62b2c7bb` plus both fixes and strengthened Fast cancellation fixture | 25 / 25 | 0 / 0 | 36.840 s | Full suite passed again; identical source tree after rebase onto merged main |
| Native unread-reader regression | Same base, reset before finalization | 1 / 1 | 0 / 0 | 0.857 s | No unread callbacks or later SQL; prior writes survive commit; repeated abort is safe |
| TerraBase SQL cancellation regression | Same base, reader reset before cleanup | 1 / 1 | 0 / 0 | 4.458 s | Next query succeeds in the same transaction after cancellation between rows |
| Full TerraBase Slow, older base | T0 `ae4bac41` plus both fixes | 57 / 59 | 1 / 1 | 601.707 s | 100k-count test hit the existing 600 s timeout; real Tigris test lacked credentials |
| Targeted 100k-count test | T0 `62b2c7bb` plus both fixes, temporary timing diagnostics | 1 / 1 | 0 / 0 | 451.464 s | Passed unchanged assertions and timeout; sampled SQL preparation during document seeding |
| Fast stream lifecycle cases | Same current base, deterministic native cancellation plus boundary cases | 8 / 8 | 0 / 0 | 4.501 s | Native cancellation ends the transaction; consumer-boundary cancellation preserves its snapshot |
| Full native driver suite | Same current base and strengthened fixture | 7 / 7 | 0 / 0 | 112.370 s | Original six finalizer regressions plus unread-reader regression all passed |
| Full TerraBase Slow, current candidate | Same current source tree, existing dev credentials supplied privately | 58 / 59 | 1 / 0 | 589.904 s | Count passed unchanged; real Tigris test failed because the old dev bucket had been deleted |
| Real Tigris backup regression | Same current source tree, isolated private bucket with vendor-issued credentials | 1 / 1 | 0 / 0 | 21.412 s | Passed against real storage; not a full green Slow run |
| Final full Auth Slow | Same current source tree, isolated real bucket | 25 / 25 | 0 / 0 | 37.260 s | Full run passed |
| Final full TerraBase Slow | Same current source tree, isolated real bucket | 59 / 59 | 0 / 0 | 571.280 s | Full run passed; real Tigris exercised; count timeout and parallelism unchanged |

Auth reports [#199](https://github.com/TerraScale/terrascale/issues/199) and
[#200](https://github.com/TerraScale/terrascale/issues/200) were fixture errors: key creation atomically requires an
existing project, but the Slow fixture omitted it. The fix supplies the project records just as the existing Fast
fixture does. The production guard, races, assertions, test count and parallelism remain unchanged.

TerraBase report [#201](https://github.com/TerraScale/terrascale/issues/201) was reader cleanup: both managed close
and native finalization execute unread rows. After cancellation at a consumer boundary, cleanup invoked the cancelled
scan callback and Turso rolled back the transaction. The pinned native C API's `turso_statement_reset` releases the
unfinished read before finalization without evaluating unread rows. It also discards later batch statements. Existing
query budgets, rollback detection and the operation gate remain intact. The vendor change is covered by the native regression
and documents its provenance; no native package upgrade is required.

Two Fast stream fixtures had asserted native rollback after cancelling at the consumer boundary, conflicting with the
Slow contract. Their existing rollback and overlap assertions remain: the fixture now cancels from the existing native
scan callback on its own writer session, proving cancellation actually happened during native execution. Two added
boundary cases require snapshot reuse. Test parallelism is unchanged.

| Docker `test` observation | Input | Total wall time | Auth Fast | TerraBase Fast | Limitation |
|---|---|---:|---|---|---|
| First complete run after host reboot | T0 `ae4bac41`, prepared Docker stages, no Slow fixes | 482 s | 664 / 664, 424.796 s | 1492 / 1558, 66 failures, 388.936 s | Older T0 base; `_system` replication errors; failed build; partial cache from interrupted earlier run |
| Latest T0, before fixture correction | T0 `62b2c7bb` plus both fixes | Not used for comparison | Build cancelled after TerraBase failure | 1543 / 1561, 18 failures, 246.380 s | Local wrapper omitted the build revision; native/boundary cancellation fixture conflict |
| Current candidate | Merged main plus both fixes, corrected fixture and automatic build revision | 816 s | 664 / 664, 564.864 s | 1563 / 1563, 322.643 s | Passed exit 0, 18:39:54Z–18:53:30Z; prepared Docker stages, partial warm cache; native 7/7 also passed |

Reproduce full suites with `make auth-test-slow CONFIGURATION=Release TEST_ARGS=--no-ansi` and
`make terrabase-test-slow CONFIGURATION=Release TEST_ARGS=--no-ansi`. Run `make docker-test
TS_DOTNET_DIR=<pinned-sdk-worktree>` for the Docker `test` target, including its required named SDK build context.
Interrupted runs are excluded: the host reboot interrupted the first full TerraBase Slow and Docker runs.

The local `make docker-test` wrapper now supplies the checkout's HEAD as `TERRABASE_REVISION`, matching CI's existing
build input and preserving the health assertions. No manually supplied option is needed. The previous full required
check passed formatting, security, build, OpenAPI, benchmarks, libs, Auth Fast (664/664) and native driver (7/7), then
failed only the two old Fast cancellation fixtures (1559/1561). Final `make check` (18:30:01Z–18:47:53Z, 1072 s) passed those gates and the corrected cancellation fixtures,
then failed one T0-owned shutdown race: TerraBase Fast 1562/1563, 279.940 s.
`Shutdown_ends_a_replacement_waiting_for_its_predecessors_refill` fails at `PeerStreamTests.cs:1505`
when `RawPush.WelcomeAsync` sees an HTTP/2 CANCEL. No second identical retry was started.
Auth Fast passed 664/664 in 468.610 s; the native driver passed 7/7 in 58.170 s.

The final combined Slow command passed (exit 0) from 18:53:35Z to 19:04:15Z, 639.597 s including builds.

This report has no merged fix PR, main-job result, live e2e result or deployment speedup yet. T0 PR
[#206](https://github.com/TerraScale/terrascale/pull/206) merged with its PR `test` green, but subsequent [main run 38446893001](https://github.com/TerraScale/terrascale/actions/runs/38446893001)
failed two Fast cases: the same shutdown race and replica-rebuild registration. T0 owns main `test` SUCCESS. Workflow and preview
pushes remain held for T2's deploy re-enable; production machines remain owned by T2.
