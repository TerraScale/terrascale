# TerraBase Management slice — untangle decision log

Slice: `src/TerraBase/Features/Management/**`, tests `tests/TerraBase.Fast/Management`, `tests/TerraBase.Slow/Management`.
Decisions already made: comments removed (keep only external-constraint comments), tests pruned/rewritten last. LangVersion preview (C# 15).

## Fixes (above the problems they solve)

- Delete the route-level `WritableDatabase`/`WritableProject` unions; endpoints and helpers use the identical service unions `WritableDatabaseTenantResult`/`WritableProjectResult`.
  - `ManagementRoute.cs:22` `WritableDatabase(TenantContext, NotFound, Forbidden, Unavailable)` duplicates `IManagementService.cs:67` `WritableDatabaseTenantResult` case-for-case.
  - `ManagementRoute.cs:25` `WritableProject(Project, NotFound, Forbidden, Unavailable)` duplicates `IManagementService.cs:69` `WritableProjectResult` case-for-case.
  - `ManagementRoute.cs:88-104` `CheckWritableDatabaseAsync`/`CheckWritableProjectAsync` exist mostly to re-wrap one union into its twin.
  - `Backups/ProjectBackupDestinations.cs:35,153` converts `WritableProjectResult`→`WritableProject` arm-for-arm and casts back.
- Make `ManagementRoute.ProjectAsync`/`DatabaseAsync` return the service call instead of an identity switch over the same union.
  - `ManagementRoute.cs:72-86` switch over `GetProjectResult`→`GetProjectResult` and `DatabaseTenantResult`→`DatabaseTenantResult`: every arm maps to itself.
- Merge `SystemCatalog.WriteAsync`/`WriteDurableAsync` into one core with a `durable` flag.
  - `SystemCatalog.cs:71-102` two copies of the same guard+check+write pipeline differing only in `durable: true`.
- Replace fully qualified names with usings.
  - `Backups/GetRestoreWindowEndpoint.cs:53`, `Usage/GetProjectUsageEndpoint.cs:58`, `Usage/ListUsageAllowancesEndpoint.cs:56`, `BranchManagedDatabaseEndpoint.cs:46`, `DeleteManagedDatabaseEndpoint.cs:41`, `MoveDatabaseHomeEndpoint.cs:72`, `SetOrganizationPlanEndpoint.cs:36,47` (`System.Diagnostics.UnreachableException`); `Usage/UsageMeter.Buffer.cs:39` (`System.Collections.Concurrent.ConcurrentDictionary`); `DatabaseTtl.cs:44` (`System.Text.Json.JsonSerializer`); `EngineManagementService.CatalogHelpers.cs:28`, `SetOrganizationPlanEndpoint.cs:36` (`TerraBase.Tenancy.PlanTier`).
- Return `IReadOnlyList<T>` from public edges instead of `List<T>`.
  - `Usage/PeerUsage.cs:50,103` return `List<ProjectUsage>?`/`List<AllowanceUsagePage>?`.
  - `SystemCatalog.cs:223` `ListAllAsync` returns `ValueTask<List<T>>`.
- Remove every comment that restates code (TerraBase: no comments); keep only comments about something outside the code (external system behavior, third-party bugs, protocol constraints).
  - 881 comment lines across the slice (`//` and `///`).
- Tests: prune tests that cannot catch a real bug; rewrite internals-tied tests to behavior (done last).

## C# modernization list

- Unions/closed hierarchies: already used throughout; verify no `_` arms on owned unions remain (none found in slice).
- Locks: already `System.Threading.Lock` everywhere (verified all 11 declarations).
- TimeProvider: already injected everywhere; no `DateTime.Now/UtcNow` in slice.
- FQN removal + usings (above).
- `IReadOnlyList<T>` at API edges (above).
- CancellationToken: present on service interfaces; check private helpers flagged below during edits.

## Problems observed but NOT fixed (cross-slice, other workers own them)

- (to fill in final report as found)

## Worklog

- [in progress] initial log
