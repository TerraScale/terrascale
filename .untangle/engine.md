# TerraBase Engine slice — untangle decision log

Slice: `src/TerraBase/Engine/**`, tests `tests/TerraBase.Slow/{Storage,Driver,Revisions}`,
`tests/TerraBase.TestHarness/{Storage,Revisions}`, `tests/TerraBase.E2E/**`.
User chose BOTH additions (comments, tests). Behavior must not change.

## Fixes (each addresses the problems listed under it)

### F1. One reserved-root-name table for the data directory layout
The set of root file names the engine owns (`_system`/`_intents` × `.db` + every sidecar
suffix) is spelled out longhand in three places that must change together when a sidecar
kind is added.
- `Turso/TursoStorageEngine.cs:744` `EnumerateDatabaseFiles` lists 15 `SequenceEqual`
  disjunctions for `_system`/`_intents` + each suffix.
- `Turso/TursoStorageEngine.cs:1187` `IsFlatDatabaseFile` rebuilds the same names with
  different code (`Sidecars.Any(...)` chains).
- `Turso/TursoStorageEngine.cs:34` `Sidecars` is the single source of suffixes the other two
  must stay in sync with, and nothing says so.
Fix: build `ReservedRootNames` once from `{SystemName, IntentName} × {""} + Sidecars` and
consume it in both predicates.

### F2. One store factory instead of four hand-rolled constructions
`TursoDatabaseStore` is constructed in four places with 13 positional arguments each plus
overlapping object-initializer sets; a new cross-cutting store dependency (clock, counter,
ledger hook) must be threaded through all four by hand.
- `Turso/TursoStorageEngine.cs:822` (system store), `Turso/TursoStorageEngine.cs:1582`
  (tenant store), `Turso/TursoStorageEngine.Intents.cs:35` (intent journal store),
  `Turso/TursoStorageEngine.Recovery.cs:10` (recovery scratch store).
- The unused second constructor `Turso/TursoDatabaseStore.cs:39` exists only as an alternate
  argument shuffle and has no callers.
Fix: private `SystemStore(path)` / `TenantStore(key, path)` factories on the engine holding
the shared core; recovery keeps its bare construction; delete the dead constructor.

### F3. Remove unused test seams and dead code (surplus: nothing will ever change them)
- `Turso/TursoDatabaseStore.cs:881,883` `AfterGroupCommit`/`AfterTursoCommit` are invoked but
  never assigned anywhere (src + tests) — speculative hooks; their invocations
  (`TursoDatabaseStore.cs:1055`, `TursoStorageTransaction.cs:271`) are no-ops.
- `Turso/TursoDatabaseStore.cs:877` `_batched`/`BatchedWrites` written and read by nobody.
- `Turso/TursoDatabaseStore.cs:168` `IdleCount` and `Turso/TursoSessionPool.cs:129,141,45`
  `IdleCount`/`IdleCountOf`/`MaxIdle` have no callers.
- `Turso/TursoStorageEngine.cs:103,105,107,49,51` `BeforeReadinessProbe`,
  `ReadinessProbeTask`, `InitializationsAwaitingRelease`, `CreationHeld` unused (the probe
  hook's only reader is the probe itself).
- `Turso/TursoChangeTap.cs:56-62` `AfterSnapshotPinned`/`BeforeTrim`/`BeforeSnapshotEnd`/
  `AfterSnapshotBegin` never assigned; their guarded invocations are no-ops.
- `Turso/TursoStorageEngine.cs:680` `if (existed) { }` empty statement left by a removed log.
- `Engine/QueryCapacityException.cs` parameterless and `(message, inner)` constructors have
  no callers.

### F4. Share the row-materialization helpers between transaction and replica reader
The row-stepping loop of a customer read exists twice: the value-conversion switch over
`TursoBindings.GetValue` and the per-column stored-size sum are copies that must change
together when the driver's value surface changes.
- `Turso/TursoStorageTransaction.Relational.cs:190-227` (transaction `Step`).
- `Turso/TursoReplicas.cs:1116-1144` (replica `Reader.Step`).
Fix: internal static helpers (`ValueSize`, `Value`) on `TursoStorageTransaction` used by
both loops; the loops themselves stay separate (budget and truncation rules differ on
purpose).

### F5. One record-skip reader for the change log
`ChangeLog` models a fixed set of record kinds (Upsert/Delete/Commit/Create/Drop/
TimedCommit/ReadMark) but each of three switches re-encodes every kind's payload layout;
adding a kind needs three edits plus the writer, and nothing warns.
- `Turso/TursoChangeTap.cs:693` (`Cut`), `:765` (`EndMark`), `:872` (`Apply`).
Fix: extract `SkipRecord(segment, kind)` shared by `Cut` and `EndMark` (their skipping is
identical); `Apply` keeps its own switch because it consumes values, and the missing-kind
`default` arm stays the build-time-visible boundary. (`TursoBindings` reads prevent a
closed-hierarchy rework here without behavior risk; noted for the future.)

### F6. Namespace/folder mismatch in tests
- `tests/TerraBase.Slow/Revisions/DurableRevisionTests.cs:4` declares
  `namespace TerraBase.Fast.Revisions` while living in Slow — a Slow suite test hidden in
  the Fast namespace.

### F7. Comment pass (user addition)
- Remove `//` comments and `///` docs that restate code or contract (TerraBase rule: code
  carries no comments; CS1591 is suppressed in `Directory.Build.props`).
- Keep comments about something outside the code: vendor/provider behavior
  (`FaultAwareCopySource`, `DatabaseKeys`, `TursoSession` native-handle notes), POSIX
  constants (`PageCache`), `ponytail:` measured-tradeoff markers, decision-doc/task
  references (0003/0010/TS-633…), and the test-placement rationale in
  `DurableRevisionTests`.

## Problems noted, deliberately not fixed (cross-slice or risk)
- `SqlFailure`, `ReplicaVerdict`, `FullReason`, `RewriteRefusal` are fixed-set enums that
  would read better as closed hierarchies, but each is consumed by other slices
  (`Features/Sql`, `Features/Replicas`, `Features/Sync`); converting them forces edits
  outside this slice. Reported to the coordinator instead.
- `DatabaseFences`/`HomeLeaseGate` read `TimeProvider.System` directly (no injected clock);
  they are constructed inside the engine and injecting a clock threads a new parameter
  through engine construction for little gain. Left; noted.
- `TursoStorageEngine`'s constructor takes 20 parameters, most of them test seams
  (`maxIdle`, `createWorker`, `afterBootstrap`, …). Consolidation into an options record
  would churn `EngineExtensions` (composition root) and every fixture for no structural
  gain; left as is.
- `TursoSession.Native` reaches into the vendored driver's private `_database` field by
  reflection (third-party limitation, documented in place). Kept; names cleaned only.
- `Task.Delay` retry pauses (`TursoSession.ExecuteAsync`, `TursoReplicas.OpenReaderAsync`)
  are wall-clock on purpose (50 ms busy-step); not converted to TimeProvider.

## C# modernization list
- Delete fully qualified names (`global::Turso.Raw.*`, `System.Text.*`,
  `System.Globalization.*`, `System.Reflection.*`, `System.Data.Common.*`,
  `System.Collections.Immutable.*`, `System.Diagnostics.*`) in:
  `TursoSession.cs`, `TursoChangeTap.cs`, `TursoCopy.cs`, `TursoDatabaseStore.cs`,
  `TursoDatabaseStore.Rewrite.cs`, `TursoReplicas.cs`, `TursoReplicas.Writes.cs`,
  `TursoStorageTransaction.Shapes.cs`, `SqlText.cs` — add `using` directives instead.
- Simplify `x is var y && ...` chains that only bind a local (`TursoStorageEngine.cs:1146`,
  `TursoDatabaseStore.cs:463,515`, `DatabaseFences.cs:26`, `StorageFaultLedger.cs:47,330`,
  `TursoStorageTransaction.cs:122`).
- `DatabaseFences.Set`: drop the if/else for `previous?.Changed.TrySetResult()`.
- `TursoStorageTransaction.StartQueryBudget`: `_queryBudget` assigned twice (line 71 and
  78) — assign once.
- `TursoDatabaseStore.FilePresent`: name the 1000 ms existence-cache window.
- `DatabaseTransactions` extension: convert to an `extension` block (C# 14) when touched.
- Collection expressions / `Lock` / field-backed properties / `field ??=` already in use
  across the slice; keep and extend where touched.

## Tests (user addition, done last)
- All retained tests in the slice guard real behavior (power-loss durability, WAL contract,
  revision convergence, erasure verification): kept.
- F6 namespace fix only; no test logic changes.

## Work log
- F1 committed: `ReservedRootNames` table + `IsReservedRootName`; both longhand predicates now
  read from it. Build green, Fast green.
- F2 committed: `CreateSystemStore`/`CreateTenantStore` factories; second store constructor
  deleted; intent journal uses the system factory (it now carries `RewriteSteps` it can never
  record). Build green.
- F3 committed: removed `AfterGroupCommit`/`AfterTursoCommit` (+ invocations), `_batched`/
  `BatchedWrites`, store `IdleCount`, pool `IdleCount`/`IdleCountOf`/`MaxIdle`,
  `BeforeReadinessProbe`/`ReadinessProbeTask`/`InitializationsAwaitingRelease`/`CreationHeld`,
  the four `TursoChangeTap` hooks (+ guarded invocations), the empty `if (existed) {}`, and the
  parameterless `QueryCapacityException` constructor. Fast suite 88/88.
- F4 committed: `ValueSize`/`Value` shared stepping helpers.
- F5 committed: `SkipPayload` record-skip table; `Cut`/`EndMark` keep only value-consuming arms.
- F6 committed: Slow revision tests namespace fixed; Slow project builds.
- Modernization committed: using directives replace inline `System.*`/`global::Turso.*` names
  in eleven files; `is var` unwound in four places; `DatabaseFences.Set` null-conditional;
  duplicate `_queryBudget` assignment dropped; `FileSeenWindow` named; `DatabaseTransactions`
  converted to a C# 14 `extension` block.
- Comment pass committed: 389 `///` lines removed across 32 files; 69 marker-bearing lines and
  all `//` constraint comments kept; the trusted-operator threat model kept as a `//` comment
  on `IStorageEngine`. Fast suite 88/88 after it.
- Sweep two committed: `TursoStorageEngine.Backup`/`TursoShapeReader` qualified names; dead
  `DisposeResponseAsync` test helper removed.

## Remaining (not worth the risk; for the coordinator)
- Fixed-set enums (`SqlFailure`, `ReplicaVerdict`, `FullReason`, `RewriteRefusal`) as closed
  hierarchies: cross-slice consumers, see above.
- `RevisionManifest` exposes `List<>`/`HashSet<>` properties: it is an in-place builder that
  `TursoRevisionLedger` and `RevisionManifestFile.Load` mutate; sealing that behind read-only
  views would rework the ledger's update flow for no behavioral gain.
- `DatabaseFences`/`HomeLeaseGate` construct with `TimeProvider.System` (no injected clock).
