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
