# TerraBase engine slice — round 2 decision log

Slice: `src/TerraBase/Engine/**`; tests: `tests/TerraBase.Slow/{Storage,Driver,Revisions}`, `tests/TerraBase.TestHarness/{Storage,Revisions}`, `tests/TerraBase.E2E/**`. Round 1's `.untangle/engine.md` does not exist on `origin/terrabase-modernize-engine` (verified `git ls-tree -r`); nothing to work through from it.

## Fixes

- Inject `TimeProvider` through the engine instead of direct `TimeProvider.System` reads in Engine.
  - `Engine/DatabaseFences.cs:81,91` reads `TimeProvider.System` in `WaitAsync`.
  - `Engine/HomeLeaseGate.cs:22` reads `TimeProvider.System` in `Holds`.
  - `Engine/Turso/TursoChangeTap.cs:445` constructs the rewrite tap with `TimeProvider.System` although the store already carries `Time`.
  - `Engine/Turso/TursoDatabaseStore.cs:158,160` falls back to `TimeProvider.System` per access / via init-only property the engine always sets.
  - `Engine/Turso/TursoStorageEngine.cs:103` mutable `ReadinessClock` defaults to `TimeProvider.System`; `:325,327` ctor fallbacks.
  - `Engine/Turso/TursoStorageEngine.RewriteBudget.cs:41,43` builds the deadline CTS and pressure timer on `TimeProvider.System` although the engine carries `Time`.
- Model `FullReason` (`Engine/Revisions/RevisionPlanner.cs`) as a closed hierarchy instead of an enum + nullable field.
- Model `RewriteRefusal` (`Engine/IHealthyRewrite.cs`) as a closed hierarchy instead of an enum carried on `RewriteRefusedException`.
- Delete every comment in the slice (house rule 1); classify third-party constraints (Turso/SQLite/provider/kernel) for the coordinator, keep their code.
- Keep the bounded busy-wait designs that encode third-party contracts (Turso busy timeout 0, no interruptible statements, checkpoint/apply pager contention) as real design; no root-cause fix is possible inside our code (listed under THIRD-PARTY in the report).

## Problems found while scanning (not fixed here)

- `Engine/Turso/TursoIoPool.cs` `// ponytail` notes (queue lock, bounded scans) are forward-looking perf notes, not workarounds; deleted with the comment pass.
- Cross-slice candidates reported to the coordinator in the final report.

## Round 2 additions (coordinator review)

- `RewriteRefusal` redone as a `closed` record hierarchy with sealed data-less cases; the metric label is one exhaustive `Label` switch returning the exact old strings; comparison uses `is Pressure`.
  - The `..^4` type-name `ToString()` was an enum in disguise tying a metric label to a class name (IHealthyRewrite.cs, TursoStorageEngine.Rewrite.cs).
- `FullReason` stays an enum: no case carries data and nothing switches on it (planner constructs, `RevisionPush.cs:87` null-checks, tests compare). A closed hierarchy would be the same fake.
- Removed the swallow-everything catches around commit-path logging (`TursoDatabaseStore` LogAfterCommit, LogLongHold, group-commit error log): ZLogger does not throw; the wraps hid real logging failures.
- Busy-step pauses now use the injected clock: `TursoSession.ExecuteAsync` (`Task.Delay(50, time, ct)`, clock threaded through `OpenAsync` from `store.Time`), `TursoReplicas.OpenReaderAsync` (`time` on the Replica, defaulting `TimeProvider.System`), and the rewrite `QuietPoll` delays (`TursoDatabaseStore.Rewrite.cs`).
- `RevisionManifest` is an immutable record; `Seal`/`RewoundFull` return the next manifest; `RevisionManifestFile.Load` builds it; `TursoRevisionLedger` reassigns `_manifest` under its gate. `HorizonBucket` is an immutable record with `IReadOnlyList`/`IReadOnlySet`.
  - Before: Load, the ledger, the planner and the memory-retention path shared one object with mutable `List`/`HashSet`/setters, so every reader depended on the writer's in-place edits.
- `TursoStorageEngine` construction: dead test seams removed (`createWorker` incl. `TursoIoPool`, `afterBootstrap`, `maxIdle`, `maxIdlePerDatabase` — no caller anywhere), remaining config grouped into `StorageEngineOptions` built in `EngineExtensions`; services stay ctor params. `SharedProcess` (harness) follows with the smallest edit.
- `TursoChangeTap.cs` (1123 lines) held three top-level types changing for different reasons: split `ChangeLog` (CDC log wire codec, 476 lines) and `SqliteRecord` (record decode, 104 lines) into their own files.
- `TursoStorageEngine.cs` (1679 lines): extracted the data-directory ownership/layout block (`Own`, `CheckLayout`, `RecordedFormat`, `IsLockConflict`, `IsReservedRootName`, `IsFlatDatabaseFile`) into `TursoStorageEngine.Directory.cs` — changes when the on-disk layout changes, not when stores change.
- Judged and left: `TursoDatabaseStore.cs` (1320) is writer/transaction lifecycle and `TursoReplicas.cs` (1148) is replica lifecycle; each is one concern, already partial-split (`Rewrite`, `Rewind`, `Revisions`, `Writes`, `Backup`). `TursoReplicas`' replica key record handling mirrors `DatabaseKeys`' DB key record handling but the formats and publication steps differ (replica key overwrite vs create-new, `Task.Run` vs io pool); unifying them would parameterize a persisted-format path for little gain.
