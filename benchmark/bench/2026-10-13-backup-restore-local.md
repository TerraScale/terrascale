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
by this drill. Tigris CLI identity verification failed twice with `ID token verification failed`; authentication must be
renewed before live work. Scheduled object listings with timestamps, three-region digest and bidirectional replication
proof, daily Fly snapshot settings and a snapshot restore, production timings, and PR merge remain outstanding.
