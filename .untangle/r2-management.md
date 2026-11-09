# TerraBase Management slice — round 2 decision log

Slice: `src/TerraBase/Features/Management/**`, tests `tests/TerraBase.Fast/Management`, `tests/TerraBase.Slow/Management`. Base: `origin/terrabase-r2`.

## Fixes (above the problems they solve)

- Delete every remaining comment line (27) across src and tests; round 1 kept them for external citations, house rule 1 now deletes them all. Genuinely third-party constraints (RFC 3339, RFC 6750, Cloudflare refusal behavior) are reported under THIRD-PARTY; doc/ticket citations are ours and carry no code meaning.
  - 27 lines listed by the `rg "^\s*(//|/\*)"` sweep (src 16, Fast 5, Slow 6).
- `RemoteDatabases.ForgetAsync` takes the `TenantContext` it is forgetting and builds the peer path with `ReplicaRoutes.DatabasePath` instead of a hand-built `v1/sync/organizations/.../databases/...` string.
  - `RemoteDatabases.cs:74` hand-builds the exact path `ReplicaRoutes.DatabasePath` produces (hidden copy across slices).
  - `EngineManagementService.cs:445` unpacks `storage.OrganizationId/ProjectId!/DatabaseId!` only so `ForgetAsync` can reassemble them.
- Replace the two remaining fully qualified names with `using System.Globalization;`.
  - `Usage/ListUsageAllowancesEndpoint.cs:36`, `tests/.../ShapeDeletionTests.cs:37` (`System.Globalization.CultureInfo.InvariantCulture`).

## Problems inspected and left as real design

- Retry/backoff/error strings containing "retry": plan fanout, enrollment fanout, usage deadline (Retry-After contract), replica forget sweep, durable-catalog write retries. All bounded, externally-driven or product-contract designs; not bug patches.
- `catch (Exception ...)` sites all sit at top-level boundaries (background sweeps, fanout logging, test fixture startup/cleanup) with `when` filters; per csharp section 2 these are the sanctioned boundaries.
- `tests/TerraBase.Slow/Management/CrossRegionProjectEnrollmentTests.cs:51` polls cross-region eventual consistency with a bounded loop (50 x 100ms) in Slow; polling an eventually-consistent cluster state is the contract under test, not a sleep hiding a bug.
- `LegacyRoutes.cs` (`/v1/management/organizations/...` aliases) looks like a compat shim but is documented public HTTP surface (docs/terrabase/backup.md:72, docs/terrabase/customer-api-key-ownership.md:12); HARD LIMITS forbid changing routes. Reported as CROSS-SLICE for the coordinator.
- `_ => throw new UnreachableException()/InvalidOperationException()` error-half arms and `static _ =>` lambdas in catalog read/write helpers: the price of exhaustive unions (round 1 note) and lambda discard parameters, not switch defaults on owned unions.
- `PlanTier`-related `_ =>` plan validation arm at `ManagementRoute.cs:104` touches the reserved `PlanTier` enum; left.
- `DateTime.UtcNow` only in tests minting skewed tokens (comparing against the real clock); not business logic.
- Locks verified as `System.Threading.Lock` fields (round 1); syntax `lock (x)` unchanged.
- EngineManagementService.cs main partial still 1223 lines but cohesive (round 1 note); left.
- No namespace mismatches in test folders; no tests construct internal service types (all drive the server via HTTP/SDK; `RealAuthFixture` builds SDK clients and TLS plumbing only).

## Worklog

- [in progress] round 2 log
