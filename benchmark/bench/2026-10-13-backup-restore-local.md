# Local backup and restore drill — 2026-10-13

This is local integration evidence. It does not prove Tigris shipping, cloud machine recovery, or cloud snapshot recovery.

| Environment | Value |
|---|---|
| Source commit | `8a1f626ebfa8425f81fb8ddd67c636051e47a863` |
| SDK dependency | ts-dotnet `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` (CI pin) |
| Host | delta; Linux x64; Intel Core i7-13620H; 16 logical CPUs visible; 31,791 MiB host RAM |
| Limits and contention | Shared host, no CPU quota; user cgroup memory limit 26 GiB; `ts-heavy` runs at nice 19 |
| Build / runtime | Debug; .NET SDK `11.0.100-rc.1.26425.128`; TUnit default parallel execution, runner cap 16 |
| Object store | Real AWS S3 adapter with an in-process FakeS3 transport; encrypted backup objects; no external network |

| Measurement | Result | Boundary |
|---|---:|---|
| Documents recovered | 25 / 25 | 24 before the snapshot, one in a subsequent CDC segment |
| Backup | 730.0 ms | Both shipping passes; excludes writing the initial documents |
| Restore | 221.5 ms | Management PITR request, including download, replay, integrity check and registration |
| Data integrity | All 4096 bucket digests identical | Compares restored database with the live pre-wipe digest; all 25 documents also read through the client listener |
| Integration suite | 260 passed, 0 failed, 0 skipped | Backup namespace, including 10 restore-gate cases; 56.956 seconds |
| Driver regressions | 6 passed, 0 failed | Same invocation |

Single timing sample under concurrent integration-test load; no throughput or production recovery estimate is inferred.
The wipe deletes the live database through the real storage engine and confirms its file and digest are absent before restore.

Command (in this task worktree):

```sh
TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-backup-restore-sdk \
  ~/.local/bin/ts-heavy make terrabase-test-fast TEST_ARGS='--treenode-filter /*/TerraBase.Fast.Backup/*/*'
```

Retained successful test output, extracted from the TUnit HTML report:

```text
RESTORE_DRILL documents=25 backup_ms=730.0 restore_ms=221.5 digest=a4e98df3bf80a16c51900cbb22a59594ee6f86763c51e2fa2e134c4a5bc46700
Test run summary: Passed!
  total: 260
  failed: 0
  succeeded: 260
  skipped: 0
```

The gate cases cover current catalog restart (standalone and directory-backed), a fresh replacement with a destroyed
historical member, failed revocation anchoring (direct route and commit replay), and stale catalogs with present, missing,
tampered or replayed backup state. Unverified catalogs refuse public requests, management, API-key provisioning, readiness,
system replication export and backup shipping. Anchor GETs must carry `X-Tigris-Consistent: true`.

Production acceptance is pending COS `GO DRILL`, after T2 and the live replication matrix. No cloud resource was changed
by this drill. Current production bucket ownership and recovery remain outside this local drill.
The obsolete Tigris CLI/OAuth path is not a live prerequisite. Scheduled object listings with timestamps, three-region digest and bidirectional replication
proof, daily cloud snapshot settings and a snapshot restore, production timings, and drill PR merge remain outstanding.

## Historical required gate failures before the product correction

The subsequent frozen `ts-heavy make check` exited 2: Endpoints 88/88, Logging 46/46, Auth Fast 664/664 and
Turso driver 6/6 passed. TerraBase Fast ran 1,571 cases: 1,513 passed, 58 failed, zero skipped, in 89.706 seconds.
Build, whitespace/style, analyzers, security/OpenAPI and benchmark checks passed before the suites.

The failures repeated `id_taken` during replication fixture registration. The gate pulled the catalog before local
storage existed; commit preflight rejected the already registered database ID instead of checking for an identical
entry. Operation 2 is the zero-based `create_database` index. Rebased `2ac0e39f` reproduced all 58 failures plus one
shutdown-caused cancelled RPC (1,512/1,571 passed; 143.733 seconds). Both old and rebased trees were identical.
Bisect identified `2cb75e1f` as the first bad commit; the fix is in the product, not fixture registration shortcuts.

No push or PR was made because required checks are red. This full failure does not replace the passing 260-case backup
namespace evidence above, and that evidence does not satisfy the full required gate. Raw local logs are
`/tmp/cos-backup-namespace-final.log` and `/tmp/cos-backup-frozen-check.log`; no production proof exists.

## Product correction controls

The product now reuses matching organization/project/database entries without rewriting their catalog versions and
initializes missing local database storage after a successful commit. Different content, ownership, placement and
tombstones remain conflicts. Genuine shutdown-caused cancelled RPCs are normalized; other faults remain visible.

The focused batch passed 131/132 cases, clearing every earlier replication/shutdown failure. Its sole failure was the
new deletion control's setup: replicas must be removed before a database is deleted. The corrected five conflict
controls then passed 5/5. Driver regressions passed 6/6. Logs: `/tmp/cos-backup-product-focused-v4.log` and
`/tmp/cos-backup-reuse-conflicts.log`. The detached full gate below validates the corrected combined tree; no cloud proof is inferred.

## Corrected combined tree: full gate passed

`06748fc0856594ce00939e9a7991fe99eed027c6` passed ONE detached `make check` on delta, with the same pinned SDK. Start: 2026-10-13 19:04:36 UTC; end: 19:12:29 UTC; exit 0 (7 minutes 53 seconds total). Initial launcher errors ran no tests and receive no validation credit.

| Required check | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Endpoints | 88 | 0 | 0 |
| Logging | 46 | 0 | 0 |
| Auth Fast | 664 | 0 | 0 |
| Turso driver | 6 | 0 | 0 |
| TerraBase Fast | 1,579 | 0 | 0 |

TerraBase Fast duration: 133.093 seconds. Formatting, builds, analyzers, security/OpenAPI and benchmark checks passed. All earlier replication registration and shutdown failures cleared. Command:

```sh
TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-backup-restore-sdk \
  ~/.local/bin/ts-heavy make check
```

Raw output: `/home/mariogk/.local/state/cos-backup-restore/full-gate-06748fc0.log`; recorded exit file: `full-gate-06748fc0.exit` in the same directory. Runtime/security changes are isolated in gate PR #213, held for T9's combined format-6 reset. The independent local drill slice contains no runtime gate changes. Its latest GitHub `test` must pass before merging. Production shipping, regional recreation and cloud snapshot recovery are still untested.

## Independent drill on ungated main

The same `RestoreDrillTests.cs` passed independently on main `df8ff7ebf1091a07ad7ff5d41ff0b7927128e566`, without the gate runtime diff. Host, SDK dependency and Debug build match the environment above. This is a single focused sample, not a performance comparison with the concurrent 260-case run.

| Measurement | Result |
|---|---:|
| Backup | 457.7 ms |
| Restore | 156.8 ms |
| Recovered documents | 25 / 25 |
| Bucket digest matches | 4096 / 4096 |
| Focused Fast | 1 passed, 0 failed, 0 skipped |
| Driver regressions | 6 passed, 0 failed, 0 skipped |

```sh
TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-backup-restore-sdk \
  ~/.local/bin/ts-heavy make terrabase-test-fast TEST_ARGS='--treenode-filter /*/*/RestoreDrillTests/*'
```

```text
RESTORE_DRILL documents=25 backup_ms=457.7 restore_ms=156.8 digest=696793cbceb702ed118f406d1d9833a7b4ce800c9e81019f0441fb500b4881cd
Test run summary: Passed!
  total: 1
  failed: 0
  succeeded: 1
  skipped: 0
END 2026-10-13T19:17:15Z EXIT 0
```

Raw output and extracted HTML timing: `/home/mariogk/.local/state/cos-backup-restore/local-drill-main.log` and `local-drill-main-timing.txt`. This focused run does not claim an independent full `make check`; that full check validated the combined tree above. The independent PR requires its own latest green GitHub `test` before merging.
