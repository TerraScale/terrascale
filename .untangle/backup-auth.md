# TerraBase untangle: backup-auth slice (Backup, FileStorage, Functions, UserAuth, ApiKeys)

Scope: `src/TerraBase/Features/{Backup,FileStorage,Functions,UserAuth,ApiKeys}`, tests
`tests/TerraBase.{Fast,Slow}/Backup`, `tests/TerraBase.Fast/Functions`. Behavior must not change.
Method: skills/untangle-codebase SKILL steps 2–5 with both user-chosen additions (comments, tests).

## Fixes, each above the problems it solves

### F1. Delete dead code that nothing can ever call again
- `ObjectStore.History.cs:13` `HoldsHiddenVersionsAsync` — the only reference in the repo is its
  own declaration; `PurgePrefixAsync` (ObjectStore.Purge.cs) is the erasure proof used since TS-873.
  The whole file (82 lines) exists for it.
- `ContinuousBackup.cs:157` `WaitForShippingAsync` — no caller in src, tests or benchmark.
- `ContinuousBackup.Maintenance.cs:38,367` `RetentionWaiting` hook — never assigned; only null-invoked.
- `FunctionScheduler.cs:12,16,32` `_ticked`/`NextTickAsync` — `NextTickAsync` has no caller; the
  `TaskCompletionSource` + `Interlocked.Exchange` machinery serves nothing.
- `BackupReadObservation.cs` (whole file) plus the `observation` plumbing in
  `BackupReader.cs:12,178-241,494-511` and `BackupObject.cs:87-89,186-201`: both `new BackupReader(`
  call sites (`BackupTargets.cs:95`, `benchmark/dotnet/TerraBase.LiveRestoreDrill/Program.cs:97`) use
  the default `null`, and nothing anywhere constructs a `BackupReadObservation` ("the selected
  saturation fixture" does not exist). Speculative generality; every observed path was a no-op.

### F2. One customer-request deadline helper in S3ObjectStore
`ObjectStore.cs:347-355` (`ReadAsync`), `:386-390` (`ListAsync`), `:451-455` (`DeleteAsync`) repeat
the same three lines: customer-only `CancellationTokenSource(RequestTimeout, _time)` plus a linked
token source. Add one private helper returning the effective token; `PutAsync`'s variant keeps its
own deadline math. One place changes when the deadline policy changes.

### F3. One revoke core in EngineApiKeyStore
`EngineApiKeyStore.cs:74-95` (`RevokeAsync`) and `:101-118` (`RevokeProjectBatchAsync`) both perform
the identical four catalog deletes (key record, hash→id, id→hash, project entry) with only the lookup
differing. Extract a private static core taking the resolved key, hash and project so the deletion
order and set can never drift apart (project closure and operator revocation must stay the same act).

### F4. One cached-answer preamble in TsAuthKeyAuthority
`KeyAuthority.cs:230-247` (`AdmitAsync`) and `:264-280` (`CheckAsync`) duplicate the whole
"answer present → still fresh? → refresh in background at half-life → return" block; the only real
difference is the two thresholds (Admit: `RefreshAfter`/`AdmissionRefresh`; Check:
`Fresh`/`RefreshAfter`). Extract `TryKnown(id, key, fresh, refreshAt, out result)`. Keeping the two
public methods' timing contract in one place is the point: they must differ by exactly those bounds.

### F5. Name the key scope once in ShreddedDatabaseSweep
`ShreddedDatabaseSweep.cs:40-45` computes `BackupLayout.KeyScope(tenant)` three times and tests
`record is not null` twice in adjacent statements; hoist one `scope` local and drop the double test.

### F6. Fully qualified names → usings (C# rule: never fully qualified)
- `PointInTimeRestore.cs:109,241,260,285,342` `System.Diagnostics.UnreachableException`;
  `:420` `System.Globalization.CultureInfo`.
- `InvokeFunctionEndpoint.cs:46,72` `System.Text.Encoding`.
- `ApiKeysExtensions.cs:23` `Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions`;
  `:29-33` `UserAuth.UserAuthExtensions.*`, `Management.LegacyManagementRoute`.
- `FunctionScheduler.cs:8` `Configuration.TerraBaseOptions`.
- `FileStorageUsage.cs:47` `Primitives.EntityPayload`.
- `UserAuthExtensions.cs:388` `Primitives.EntityIds`; `:38` `System.Threading.Timeout`.

### F7. Comment pass (user decision: TerraBase carries no comments)
Remove every `///` doc and `//` comment that restates code — CS1591 is NoWarn'd in
Directory.Build.props precisely because "TerraBase ... carries no comments". Keep (as plain `//`)
only comments about something outside the code: S3/Tigris/R2 behavior quirks, IANA registries,
RFC citations, incident dates, benchmark citations, third-party bugs (e.g. the AWS SDK response
event note in ObjectStore.cs:83-85, the Tigris unversioned-bucket marker note in
ObjectStore.Purge.cs:63-64). Fix the typo `before204` → `before 204` in `KeyRevocationPeers.cs:10`.

### F8. Test fixes (user decision, done last)
- `tests/TerraBase.Fast/Backup/CopyGate.cs` — a 120-line hand-written delegating wrapper over all
  26 `IManagementService` members just to gate `CopyDatabaseAsync`; it breaks on every interface
  growth (needless link between test harness and interface internals). Rewrite with
  `DispatchProxy` (the interface has no events): ~25 lines, forwards automatically, same gating
  behavior through the same DI decoration.
- `tests/TerraBase.Slow/Backup/CryptoShreddingTests.cs:11` and
  `tests/TerraBase.Slow/Backup/SqlTableBackupTests.cs:13` declare `namespace TerraBase.Fast.Backup`
  from the Slow suite — wrong namespace hidden from the reader; fix to `TerraBase.Slow.Backup`.
- `DeleteFunctionEndpoint.cs:23`, `GetFunctionEndpoint.cs:19` `Description(FunctionRoute.Description + "")`
  — the `+ ""` is a no-op; write `Description(FunctionRoute.Description)`.
- All retained tests assert behavior (HTTP status/JSON or real catalog state); none repeat internals
  and none are unconditional, so no test deletions.

## C# modernization list (mechanical, behavior-identical)
- `new[] { ... }` → collection expressions: `UserAuthenticationHandler.cs:105`, `BackupReader.cs:259`.
- `ContinuousBackup.cs:1097` `lock (file)` → a `private readonly Lock` gate on `ShippedFile`
  (never lock a bare object).
- API edges to `IReadOnly*`: `FileStorageContracts.cs` `FileSignedUrl.Headers`
  `Dictionary<string,string>` → `IReadOnlyDictionary<string,string>`; `Functions.cs`
  `FunctionLogBody.Lines` `string[]` → `IReadOnlyList<string>` (JSON shapes unchanged).
- `EngineApiKeyStore.cs:137` hand-rolled `Base64Url` → `System.Buffers.Text.Base64Url.EncodeToString`.
- `ContinuousBackup.Maintenance.cs:429` `live.AsSpan().SequenceEqual(rebuilt)` → direct call
  (first-class span conversions).
- `BackupHealthEndpoint.cs:36-37` — `Files.Count > 0` + `Where(Shipping).All(...)` → one
  collection-pattern expression over the read-only list.
- `DataKeys.cs:24-49` — mixed field naming (`store`, `master`, `time`, `freshKey` beside
  `_generation`, `_readSlots`): normalize to `_camelCase` primary-constructor assignment style used
  by the rest of the project.
- `FileStorageService.CreateUploadAsync` `admission switch { ... Unavailable => throw ... }` —
  keep (the compiler cannot prove the exhaustiveness); no suppression anywhere.

## Problems seen and deliberately NOT fixed (high-risk backup/restore paths)
- `DatabaseBackupPurge.cs:125-128` second `_pending.Verified` loop — redundant with the per-job path
  in the common case, but it also marks crash-repaired jobs (`BackupVerifiedAt` set, progress
  `KeysShreddedAt` lost) whose re-purge transiently failed; gauges-only impact, purge logic left
  untouched.
- `ContinuousBackup.cs` (1407 lines) — dense but cohesive; scheduling/discovery/shipping/side work
  are one state machine over `ShippedFile`. No restructuring: too risky for a parallel refactor.
- `NoBackup` null-object registration (`BackupExtensions.cs:21-24`) — config-driven
  (`options.Backup` unset in dev/test), not a feature flag; behavior must stay.

## CROSS-SLICE (for the coordinator, not fixed here)
- `IManagementService` has 26 members; test seams that decorate it (CopyGate here) suggest a
  dispatch-proxy helper in the shared `TerraBase.TestHarness` would serve several suites
  (the harness is owned by platform).
- `EngineManagementService` (management slice) is reached from Backup via
  `Lazy<EngineManagementService>` + `IServiceProvider.GetRequiredService` in `DatabaseBackupPurge`
  and `KeyRevocationLocal` — a service-locator pattern across feature boundaries that the management
  slice may want to make explicit.

## Progress
- [x] Decision log written, committed, pushed (this file).
