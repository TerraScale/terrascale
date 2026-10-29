# Durable sync revision in production (R1), 2026-10-29

**Verdict: PASS, 3/3 repeats.** In every repeat, every proving integration test passed:

- 0 lost acked writes.
- 0 skipped commits.
- A restore or failover started a new incarnation and forced a rebootstrap.

The production TerraBase now carries the W0-2 prototype's durable revision (`ts1:<incarnation>:<generation>:<frame>`, [2026-10-29-durable-revision.md](2026-10-29-durable-revision.md)), including its retained page-history window and coordinated checkpoints. A restore or failover starts a new incarnation (PIVOT D6, D18).

## Pins and environment

| Item | Value |
| --- | --- |
| TerraScale commit | `2ab9e356923d6477b99d9d5faf3d57fcc015e1dd` (PR #910) |
| Native library | [`turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r4`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r4), linux-x64 SHA-256 `39dc75c6662f51b2e1ca386f13226530592b116498f6353f00cf02b7592c2a9c` (verified before each run) |
| Turso source | 0.8.2, `5c168be5ef6a32ac966daf5983502a4f694b37ad`, plus `src/vendor/Turso.Native/patches/0004-explicit-wal-revision-contract.patch` |
| Machine | beta (AMD Ryzen 5 PRO 6650H, 12 CPUs), via `ts-heavy-any`, `nice -n 19` |
| Build | Release, .NET 11, warnings as errors |

## What each test proves

Each TerraBase test starts a real server with its own data directory, on encrypted (aegis256) Turso files. It writes through the production REST route `/v1/projects/{project}/databases/{name}/sql/execute`, then pulls through `IDatabaseRevisions`, the seam the R2 sync host serves. A test replica applies every pulled page. Every test ends with three checks, all of which must hold:

- **Skipped commits = 0:** the incrementally pulled replica is byte-equal, page by page, to a fresh bootstrap at the same revision.
- **Lost acked writes = 0:** every row whose write was acked is on the server with its latest value, and no unacked row is there.
- **Replica holds every acked row:** each acked row's marker is in the replica's pages.

| Test | Scenario | Expected answer |
| --- | --- | --- |
| `IncrementalPullsStayExactAcrossPolicyCheckpoints` | 3,000 inserts, 200 updates and 100 deletes across several 1,024-frame coordinated checkpoints | `incremental`, same incarnation, later generation |
| `PullsDuringConcurrentWritesConverge` | pulls running alongside 3,000 concurrent inserts | every pull `incremental` |
| `AnUncoordinatedCheckpointThenOneInsertIsNeverAnEmptyPull` | the PIVOT I.2 bug: `wal_checkpoint(TRUNCATE)` outside the coordinator, then one insert | `full;rewritten`, never an empty pull |
| `IncrementalPullsSurviveAGracefulRestart` | 1,200 inserts after the client's pull, a graceful stop, a restart on the same directory, 50 more inserts | `incremental`, same incarnation |
| `IncrementalPullsSurviveACrashImage` | the data directory copied while the server runs (no clean shutdown), then started on the copy | `incremental`, same incarnation |
| `ARewriteResendsEveryPage` | a healthy rewrite after deletes | `full;rewritten` |
| `ARestoreOrFailoverStartsANewIncarnationAndForcesARebootstrap` | `StartIncarnationAsync` (the restore and failover hook) | `full;other-incarnation`, then `incremental` with 0 pages |
| `ManualWalKeepsFramesUntilAnExplicitTruncate` (driver) | manual WAL past the automatic checkpoint threshold, close and reopen, snapshot reads, busy and stale truncates | WAL kept, database file untouched, bounded truncate resets frames |
| `FramePageRangesAndBuffersAreChecked` (driver) | overlapping buffers, frames past the end | refused |

## Results

| Test | Repeat 1 | Repeat 2 | Repeat 3 |
| --- | --- | --- | --- |
| `IncrementalPullsStayExactAcrossPolicyCheckpoints` | pass, 8.6 s | pass, 7.6 s | pass, 9.1 s |
| `PullsDuringConcurrentWritesConverge` | pass, 8.1 s | pass, 6.9 s | pass, 8.4 s |
| `AnUncoordinatedCheckpointThenOneInsertIsNeverAnEmptyPull` | pass, 2.9 s | pass, 2.6 s | pass, 2.9 s |
| `IncrementalPullsSurviveAGracefulRestart` | pass, 5.5 s | pass, 4.9 s | pass, 5.8 s |
| `IncrementalPullsSurviveACrashImage` | pass, 4.2 s | pass, 3.7 s | pass, 4.6 s |
| `ARewriteResendsEveryPage` | pass, 3.8 s | pass, 3.3 s | pass, 3.9 s |
| `ARestoreOrFailoverStartsANewIncarnationAndForcesARebootstrap` | pass, 3.1 s | pass, 2.7 s | pass, 3.0 s |
| `ManualWalKeepsFramesUntilAnExplicitTruncate` | pass, 1.9 s | pass, 1.8 s | pass, 1.8 s |
| `FramePageRangesAndBuffersAreChecked` | pass, 0.08 s | pass, 0.05 s | pass, 0.04 s |
| **Lost acked writes** | **0** | **0** | **0** |
| **Skipped commits** | **0** | **0** | **0** |

Durations are wall time for each test inside a parallel TUnit run.

The native WAL contract smoke (`scripts/vendor/turso-native-wal-smoke.c`) passed in both independent builds on each architecture in [maintenance run 40932252046](https://github.com/TerraScale/terrascale/actions/runs/40932252046).

## Reproduce

```sh
ts-heavy-any bash -c 'scripts/vendor/download-turso-native.sh && make terrabase-build &&
  dotnet build tests/Turso.Data.Sqlite.Tests -c Release --no-restore && dotnet build tests/TerraBase.Fast -c Release --no-restore &&
  for r in 1 2 3; do
    dotnet run --project tests/Turso.Data.Sqlite.Tests -c Release --no-build -- --treenode-filter "/*/*/WalRevisionContractTests/*"
    dotnet run --project tests/TerraBase.Fast -c Release --no-build -- --treenode-filter "/*/*/DurableRevisionTests/*"
  done' --repo terrascale --branch <branch>
```

## Limits

- One node over loopback. No replica process exists yet; the test replica applies pages in memory. Region replicas are R3, and the sync protocol surface over HTTP is R2.
- The crash case is a copy of the data directory taken between acked writes, not a power cut or a kill in the middle of a checkpoint. Truncate and manifest ordering under a real kill is covered by design (seal, fsync and rename before the bounded truncate; a stamp or salt change means a full resend), not by a kill test.
- Restore is exercised through the engine's `StartIncarnationAsync`, not an HTTP restore endpoint; those are being rebuilt by E2 and E8.
- Writes use main's SQL route, which runs on the document-era relational layer until E1 (native SQL tables) lands; the revision layer sees only pages, so the proof does not depend on that layer.
