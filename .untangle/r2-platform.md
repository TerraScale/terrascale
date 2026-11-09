# TerraBase platform slice — round 2 decision log

Slice per brief: `src/TerraBase/{Hosting,Configuration,Primitives,Errors,Tenancy}`, `Program.cs`,
`Features/{Qos,Pressure,Memory,Stats,Catalog,ApiReference,Platform,Health,Rpc}`, the two analyzer
projects, and the owned test folders. Baseline: round 1 (PR #1358) + main.

## Fixes

### F1. Delete every comment in the slice (house rule 1)
All `//`, `/* */`, `///` and commented-out code in the slice's .cs files goes, including docs
kept in round 1. Exceptions kept as load-bearing tooling anchors (listed in THIRD-PARTY):
`TerraBase.Analyzers.BuildCheck` `// TB0001` / `// RS0030` markers, matched by line number by
the Makefile's analyzer check — deleting them breaks `make terrabase-analyzer-check`.
Workaround-describing comments were checked first: none describes a workaround in our code
(see W1–W4 below for the hunt); they state invariants, third-party facts or design rationale,
so the code stays and the comment goes.

### F2. Namespace fixes in owned Slow test folders
Files whose namespace says `TerraBase.Fast.*` while living in `TerraBase.Slow`:
- `tests/TerraBase.Slow/Tenancy/TenantIsolationTests.cs:13`
- `tests/TerraBase.Slow/Tenancy/DatabaseEncryptionTests.cs:7`
- `tests/TerraBase.Slow/Catalog/SqlCatalogTests.cs:7`
- `tests/TerraBase.Slow/Rpc/ManagementRpcTests.cs:16`

### F3. `ListenerMetadata.Management` — NOT folded (brief's slice-specific item)
`Hosting/Listeners.cs:29` — `Management = (Internal, Client)` and `Client = (Client, Internal)`
have the same listener set (order never matters: every consumer uses `Contains`), so as a
listener set it is a duplicate. But its *reference identity* carries meaning outside the slice:
`Features/UserAuth/OperatorHandler.cs:57` and `Features/Replicas/RegionalForwarding.cs:17`
branch on `ReferenceEquals(metadata, ListenerMetadata.Management)` to give management endpoints
different operator-auth/forwarding treatment than client endpoints. Folding it into `Client`
would change authorization behavior of public routes (hard limit) or require redesigning the
management/auth distinction across UserAuth/Replicas (other workers' slices). Reported as
CROSS-SLICE instead.

### F4. Legacy management routes — CROSS-SLICE, not removed
`Program.cs:151` calls `UseTerraBaseLegacyRoutes()` (`Features/Management/LegacyRoutes.cs`,
management worker's slice): it aliases old project paths onto `/v1/...` and threads
`LegacyManagementRoute` through auth, QoS and Location headers in six other features. Under
house rule 3 it is a compatibility path, but it serves public HTTP routes and response headers,
which HARD LIMITS forbid changing, and every file involved belongs to other workers. Reported
to the coordinator.

## Workaround hunt (house rule 2) — findings

- W1 `Features/Qos/StorageQuota.cs:114,154` bare `catch` — both rethrow after releasing gates;
  correct cleanup, not swallowing. Stays.
- W2 `Features/Platform/PlatformDatabases.cs` `PlatformSeed` — 5 s retry loop at startup against
  catalog readiness; bounded startup backoff over an external dependency, and it exits on Done
  or Conflict. Real design, stays. `Task.Delay(Retry, time, stoppingToken)` uses the injected
  TimeProvider already.
- W3 `catch (Exception … when …)` filters in `StrictJson`, `TerraBaseOptions`, `PressureSignals`,
  `RequestJson`, `PeersFile`, `Program.cs` — all typed `when` filters at parse/config boundaries
  mapping to validation errors; none swallows. Stays.
- W4 `Retry-After` headers / `retryAfter` values in QoS, `ErrorResponse`, `BadRequests` — part
  of the public 429/503 contract. Hard limit, stays.
- No `Thread.Sleep`, `SpinWait`, `TODO`, `FIXME`, "for now", "hack" or "workaround" hits in the
  slice.

## Tests (house rule 4)
Owned test folders re-checked: every test drives the real server (HTTP / WebSocket / Postgres
wire / harness); none constructs internal production types directly
(`rg "new (QosAdmission|StorageQuota|MemoryBudget|CatalogGate|StatsService|PressureSignals|…)("`
in owned test folders: no hits). Harness types (FakeS3, TestIssuer, …) are fakes of *external*
systems (S3/Tigris, OIDC), which the repo rules allow. No test rewrites needed beyond F2.

## Comment inventory (F1 scope)
- src slice: ~69 lines.
- owned tests: Slow/Platform 18, Slow/Tenancy 25, Fast/Platform 1, top-level 1, TestHarness 314
  (mostly FakeS3 `///` docs describing S3/Tigris protocol behavior — deleted, listed in
  THIRD-PARTY where they record a third-party fact).

## Reserved / not touched (per brief)
`MemoryLane` parameter, `SqlFailure`/`ReplicaVerdict`/`PlanTier` enums, `SqlErrors`
classification, `_ts_conflicts` column list, `DatabaseRoute.CheckTenantAsync` boilerplate,
copied `Value(name)` helpers, `QosRequestLease` dispose convention, `TerraBaseJsonContext`
structure, `TerraBase.csproj` global usings.
