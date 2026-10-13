# Local backup and restore drill — 2026-10-13

This is local integration evidence. It does not prove Tigris shipping, Fly machine recovery, or Fly snapshot recovery.

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
by this drill. COS replaced the buckets; production now uses Fly-managed `ts-terrabase-backups-20261013-v2` through the app secrets.
The obsolete Tigris CLI/OAuth path is not a live prerequisite. Scheduled object listings with timestamps, three-region digest and bidirectional replication
proof, daily Fly snapshot settings and a snapshot restore, production timings, and PR merge remain outstanding.

## Required full gate: failed, publication blocked

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
`/tmp/cos-backup-reuse-conflicts.log`. One detached full gate follows these controls; no cloud proof is inferred.
