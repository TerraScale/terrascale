# TerraBase platform slice — untangle decision log

Slice: `src/TerraBase/{Hosting,Configuration,Primitives,Errors,Tenancy}`, `Program.cs`,
`Features/{Qos,Pressure,Memory,Stats,Catalog,ApiReference,Platform,Health,Rpc}`, analyzers,
and the owned tests. Baseline: build green, `make terrabase-test-fast` green (cold first run
hits the 10 s per-test budget; warm run passes).

Fixes are listed above the problems they solve (untangle steps 3–4). Status markers: [x] done,
[ ] remaining.

## Fixes

### F1. Delete surplus code that nothing uses
- [x] `Features/Stats/DatabaseStats.cs:145` — private `Find` never called.
- [x] `Features/ApiReference/ApiExamples.cs:180,182` — private `DataPage`/`Items` never called.
- [x] `Primitives/ExactNumber.cs:78` — `TryParse(string, …)` overload has no callers and
  allocates a UTF-8 copy of its input.
- [x] `Hosting/TerraBaseJsonContext.cs:32,88` — `[JsonSerializable(typeof(ManagedDatabaseBody))]`
  appears twice; one is redundant.

### F2. One organization-usage scan instead of three (hidden link: copies of logic)
`StorageQuota` walks "list engine databases → catalog-registered? → open → sum stored bytes"
three times with three slightly different filters. Extract one scan that yields per-organization
bytes; the three call sites keep only their own gating.
- [x] `Features/Qos/StorageQuota.cs:78-104` (`EnterAsync` measure closure) and
  `Features/Qos/StorageQuota.cs:205-232` (`OrganizationUsageAsync`) duplicate the same walk plus
  `Features/FileStorage/FileStorageUsage` base bytes; only the filter differs
  (own database excluded vs. one organization).
- [x] `Features/Qos/StorageQuota.cs:178-201` (`ReadOrganizationsAsync`) walks it again for every
  organization at once.

### F3. Union narrowing instead of unreachable switch arms (needless link: callers depend on
exhaustiveness boilerplate)
After `result is not CaseA value` on a union `U(CaseA, Unavailable)`, return the narrowed value
directly instead of a switch whose second arm is `throw new UnreachableException()`.
- [x] `Features/Qos/IStorageQuota.cs:72-77` and `140-145` (`CheckAsync` × 2)
- [x] `Features/Qos/StorageQuota.cs:33-38, 53-58, 89-94, 148-153, 186-191, 218-223`

### F4. Short names with using directives, never fully qualified names
Every fully qualified `System.*` / `Microsoft.*` / `TerraBase.*` / relative `Features.X` name in
the slice becomes a using (alias only for real clashes, e.g. `HomePush.HomePush`).
- [x] `Program.cs:101,107,108,120,141,151`
- [x] `Hosting/TerraBaseJsonContext.cs` (≈ 24 `[JsonSerializable(typeof(TerraBase.Features…))]`)
- [x] `Hosting/InternalCredential.cs:53,62`
- [x] `Hosting/BadRequests.cs:55-56`
- [x] `Hosting/Features/Health/HealthzEndpoint.cs:24` (`Engine.Turso.StorageFormat.Id`)
- [x] `Configuration/TerraBaseOptions.cs:136,230`
- [x] `Features/Catalog/CatalogExtensions.cs:24-25`
- [x] `Features/Qos/StorageQuota.cs:77,207`
- [x] `Features/Health/NodeReadiness.cs:13-14`

### F5. Exhaustive switches on the owned PlanTier enum — NOT ACHIEVABLE, reverted
CS8524 fires even with every member listed because unnamed enum values always exist; an enum
cannot be exhaustive without a default arm. The real fix is converting PlanTier to a closed
hierarchy, which is cross-slice (every slice compares orders and serializes it by name).
- [ ] `Features/Qos/PlanPricing.cs:19` and `Features/Qos/QosPolicy.cs:45` keep their `_` arms;
      moved to CROSS-SLICE below.

### F6. One source for the build's informational version
- [x] `Features/Pressure/PressureSignals.cs:31` re-derives the informational version through its
  own reflection over `AssemblyInformationalVersionAttribute`; `Hosting/ServiceVersion.cs` already
  owns that. Expose the informational version from `ServiceVersion` (internal) and reuse it.

### F7. Remove comments that restate the code (user decision: both additions)
TerraBase code carries no comments; keep only comments about something outside the code
(third-party/framework behavior, `ponytail:` performance markers, external doc references).
Load-bearing markers consumed by tooling stay: `// TB0001`, `// RS0030` line anchors in
`TerraBase.Analyzers.BuildCheck` are matched by line number in the Makefile.
- [x] Shared folders: -264 lines of restating `///`/`//`, +9 external-fact lines re-added
      (BackupSettings Fly/OCI provenance, RequestBodies 404 rationale, ServiceVersion CI
      provenance, ITenantDirectory protocol ordering, OutcomeUnknown decision 0013a, PlanTier
      ordering). Features + Program.cs + the union analyzer: same rule; QosPolicy keeps the
      pricing.json entitlement linkage, RootRedirect keeps the Scalar/relative-Location facts.
      Test harness docs kept: they carry scenario/protocol facts, not restatement; only the lone
      `<inheritdoc/>` in ClientTls.cs was removed. `TerraBase.Analyzers.BuildCheck` untouched:
      its `// TB0001` / `// RS0030` markers are matched by line number in the Makefile.
      `make terrabase-analyzer-check` passes.

### F8. Tests: keep behavior guards, drop what cannot catch a bug (user decision; done last)
- [x] `tests/TerraBase.Fast/Platform/ConfigurationTests.cs` — behavior (config contract), keep.
- [x] `tests/TerraBase.Fast/Platform/HealthTests.cs` — public probe contract, keep.
- [x] `tests/TerraBase.Fast/Platform/ApiReferenceTests.cs` — public OpenAPI surface table, keep.
- [x] `tests/TerraBase.Fast/Platform/TransportSecurityTests.cs` — tls_required contract, keep.
- [x] Tenancy Fast/Slow, Slow Platform/Catalog/Rpc — reviewed, no deletions: every owned test
      drives the real server over HTTP/WebSocket (behavior tests); none can pass vacuously.
      The one internals touch (Slow SharedProcessTests reading TursoStorageEngine state to
      observe the writer bound) asserts on HTTP behavior and data integrity, so it stays.

## C# modernization list

- [x] `TerraBase.csproj`: `<Features>runtime-async=on</Features>` added; build clean,
      `make terrabase-test-fast` green with it on.
- [x] F3 union narrowing via two helpers (a `file`-local rule class and `StorageQuota.Failure`
      extractions); F5 proved impossible for enums (CS8524) and was reverted.
- [x] `TenantContext` empty record body `{ }` → `;`.
- [x] F4 using aliases for clashes.
- [x] `TenantContext` record body `{ }` → `;` (empty body removed).
- Already present in the slice (verified, no work): file-scoped namespaces, sealed types,
  primary constructors, `Lock`, `TimeProvider`, `PeriodicTimer`, field-backed properties,
  collection expressions, `FrozenSet`, `SearchValues<char>`, raw strings, source-generated JSON
  context, `Channel`-free bounded designs, `CancellationToken` on every async path.

## Problems found and deliberately not fixed (cross-slice)

- `Tenancy/PlanTier` → closed hierarchy so per-tier switches (PlanPricing, QosPolicy, plus
  consumers in other slices) become exhaustively checked without `_ => throw` arms. Blocked by
  CS8524 as long as it stays an enum.

- `Features/Memory/IMemoryBudget.cs:16-17` — the `MemoryLane lane` parameter is accepted at ~70
  call sites across every slice but `MemoryBudget` ignores it entirely (speculative generality).
  Removing it touches ~40 files owned by the other five workers; reported to the coordinator.
- `Tenancy/PlanTier` — an ordered, name-serialized enum that a closed hierarchy would serve
  better, but every slice compares and serializes it; coordinator-level change.
- `Hosting/TerraBaseJsonContext.cs` — registers request/response types for every feature, so
  every slice edits one file the coordinator merges; noted for the coordinator.
- `Hosting/Listeners.cs` — `ListenerMetadata.Management` equals `ListenerMetadata.Client`
  (same two listeners); possible dead distinction, but the constant is referenced by other
  slices' endpoint metadata; left as is.

## Notes

- Sandbox: NuGet http cache is redirected to `.nuget-http/` in the worktree (untracked,
  excluded via git add discipline); native Turso assets fetched once via `make turso-native`.
- Build/test commands used for every check:
  `NUGET_HTTP_CACHE_PATH=$PWD/.nuget-http dotnet build src/TerraBase/TerraBase.csproj -c Debug`
  and `NUGET_HTTP_CACHE_PATH=$PWD/.nuget-http make terrabase-test-fast`.
