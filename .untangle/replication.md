# TerraBase replication slice — untangle decision log

Slice: `src/TerraBase/Features/{Replicas,Sync,HomePush,HomeApply,LocalAck,Regions,Clock}/**`,
tests `tests/TerraBase.{Fast,Slow}/Sync`, `tests/TerraBase.Slow/Clock`.
Branch `terrabase-modernize-replication` off `origin/terrabase-modernize`.
Untangle steps 2–5 of `skills/untangle-codebase/SKILL.md`; both additions chosen (comments, tests).
Behavior must not change; no public contract, wire format, metric, env var or route changes.

## Fixes (each fix sits above the problems it solves)

### Fix 1 — One route-values → `TenantContext` parser for sync endpoints
- `Replicas/HomeMoveRoute.cs:10` and `Replicas/ReplicaRoutes.cs:8` are line-for-line the same parser; `HomeMoveRoute` keeps only the shared route prefix constant.
- `Replicas/ReplicaForgetEndpoint.cs:33`, `Replicas/ReplicaPlacementEndpoint.cs:33`, `Replicas/HeldRevisionEndpoint.cs:29` each re-implement the same org/project/database validation + `TenantContext` construction inline.
- Every endpoint that parses `{organization}/{project}/{database}` route values now uses `ReplicaRoutes.Database(HttpContext)`.

### Fix 2 — One peer internal-listener address helper (C# 15 extension member)
The same expression `new UriBuilder(Uri.UriSchemeHttp, node.Address.ToString(), options.InternalPort).Uri` (a peer's internal listener URI) is written out 9× in the slice:
- `Regions/RegionPeers.cs:28`, `LocalAck/RegionPartners.cs:27`, `Replicas/ReplicaPeers.cs:35`, `Replicas/RegionalForwarding.cs:42`, `Replicas/RegionalHandoff.cs:559`, `Replicas/RegionalHandoff.cs:606`, `Replicas/RegionalHandoff.Control.cs:221`, `Replicas/RegionalHandoff.Control.cs:336`, `Replicas/RegionalHandoff.Control.cs:392`.
- New `ServingNodeExtensions` (`extension(ServingNode node) { Uri InternalListener(int port) }`) in `Replicas/ReplicaPeers.cs`; all nine sites call it.
- A 10th copy exists outside the slice (`Features/Backup/KeyRevocationPeers.cs:34`) — reported CROSS-SLICE, not edited.

### Fix 3 — One full-order comparison for `DatabaseRevision`
- `Replicas/ReplicaHolds.cs:19` and `Replicas/HomeLeases.cs:768` define the identical `Compare` (incarnation, then generation, then frame); the engine's `DatabaseRevision.Compare` deliberately compares only within one incarnation, so the full order needs one home.
- New `extension(DatabaseRevision)` member `CompareAcrossIncarnations` next to `ReplicaHolds`; both call sites use it; both private copies deleted.

### Fix 4 — `HomeForwarding` stops duplicating its own rules
- `Regions/HomeForwarding.cs:146` re-implements `IsForwardable` (`!Unforwarded.Contains(name) && !name.StartsWith(':')`) inline in `Sign` instead of calling the helper defined at line 56 — two copies of the header-forwarding rule.
- `Regions/HomeForwarding.cs:63-136` (`HomeOfAsync`) resolves `TerraBaseOptions` from `RequestServices` 5×, `IManagementService` 3× and `IRegionPeers` 2×, and repeats the "find peer by home region → HomeNode" mapping twice (105-107, 119-121); now resolved once and one local `PeerFor` helper.

### Fix 5 — `HomePush` owns its subject strings and its writer check
- `HomePush/HomePush.cs:521` and `:533` hand-build the purge filter subject `"terrabase.push.{region}.*.{org}.{proj}.{db}"`, duplicating `PushNames.DatabaseFilter` (the same string, built from the same parts) — the subject format lives in two places.
- `HomePush/HomePush.cs:106,550,579,681` repeat `!(services.GetService<RegionalOwners>()?.Writer ?? true)` — "is this node the region's fenced writer" is one property now (`RegionWriter`).

### Fix 6 — rollout endpoints share their request handling
- `Replicas/RegionalPrepareEndpoint.cs` and `Replicas/RegionalVerifyEndpoint.cs` are the same endpoint (route, Configure text, content-length bound, memory reservation, JSON read) except for one call; the shared body is one helper next to `RegionalHandoff`.

### Fix 7 — needless optional `RegionalOwners` parameters removed
- `Replicas/ReplicaPeers.cs:23`, `Regions/RegionPeers.cs:16`, `Replicas/HomeLeases.cs:35`, `Replicas/RegionReplication.cs:29` all take `RegionalOwners? owners = null`. DI always supplies it (registered first in `ReplicaExtensions`); no test constructs these types. The `null` branches (`owners is null ? new PeersFile(options).ListAsync(...) : ...`, `owners?.Writer ?? true`) are speculative generality.
- Parameter is now required; the fallback branches collapse (`owners.NodesAsync(...)`, `owners.Writer`).

### Fix 8 — pass-through endpoint wrappers removed
- `Replicas/HomeMoves.cs:235` `CatchUpAsync` only forwards to `RegionReplication.CatchUpAsync`; `HomeCatchUpEndpoint` now takes `RegionReplication` directly (it already needs `HomeMoves.ReplicaNotReady`'s home).

### Fix 9 — fully qualified names removed (C# rule: short names + usings)
- `LocalAck/InRegionAck.cs:198,246`; `LocalAck/HoldEndpoint.cs:48,74`; `LocalAck/ReleaseHoldsEndpoint.cs:37`; `HomeApply/ApplyEndpoint.cs:55`; `HomeApply/ApplySql.cs:107,203`; `HomeApply/ApplyRun.cs:297`; `HomePush/PushNames.cs:25`; `HomePush/HomePushExtensions.cs:10`; `Replicas/HomeSessionEndpoint.cs:113,196`; `Replicas/HomeMoves.cs:99,387`; `Replicas/ReplicaStatusReportEndpoint.cs:42`; `LocalAck/HoldStore.cs:132`.

### Fix 10 — comments that restate the code removed (chosen addition)
XML doc comments on every slice type and `//` narration of what the next lines do are gone. Kept: comments stating facts outside the code — third-party/BCL behavior (`SetRequestHeader` replaces), cross-process protocol invariants (regional-owner CAS ordering, lease fencing), and environment facts (ts-orch NATS gaps, RFC 8441 relay semantics).

### Fix 11 — small modernizations
- `Regions/HomeLocator.cs:39-55` `Task.WhenAny` + list-removal loop → `Task.WhenEach`.
- `Clock/ClockGuard.cs:26` `is var age && age > FreshFor` trick → plain expression.
- `HomeApply/ApplySql.cs:91`, `Replicas/HomeSessions.cs:224` `new Dictionary<...>()` → collection expressions; `HomeSessionEndpoint` `?? new Dictionary<>` → `?? []`.
- `Replicas/HomeLeases.cs` now injects `TimeProvider` (uses `TimeProvider.System` directly at 8 sites for timer/delay/monotonic work).
- `LocalAck/HoldStore.cs` `Count` read under a lock it doesn't need → `Interlocked.Read`.

### Tests (chosen addition, done last)
- Fast Sync tests (`PageSyncTests`, `RegionReplicaTests`, `HomePushKeyStoreTests`) drive real listeners/replicas and catch real regressions: kept as-is.
- Slow Sync/Clock tests reviewed for tests that cannot fail or that mirror internals; see the log at the bottom for what was kept/removed.

## C# modernization list
- C# 15 `extension` blocks: `ServingNodeExtensions.InternalListener`, `DatabaseRevisionExtensions.CompareAcrossIncarnations` (Fix 2, 3).
- `Task.WhenEach` (Fix 11).
- Collection expressions for empty dictionaries (Fix 11).
- Short names + `using` directives instead of fully qualified names (Fix 9).
- `TimeProvider` injection completed in `HomeLeases` (Fix 11).
- Existing good practice kept: `Channel<T>` bounded queues, `System.Threading.Lock`, primary constructors, source-generated JSON context, `CancellationToken` everywhere.

## Problems deliberately left (with reasons)
- `HomePush.Dispatch/DrainAsync` per-key `Queue<INatsJSMsg<...>>` under a `Lock`: the drain ends itself when its key's queue empties (self-deregistering consumer); a `Channel<T>` per key cannot express "stop when empty" without an extra completion handshake, so the hand-rolled queue is the simpler correct structure here. Upstream concurrency is already bounded by `MaxAckPending`.
- `HomeApplyExtensions.PrepareReplicatedAsync`'s two overloads: callers are Management endpoints (outside slice); collapsing them would restructure another slice's call sites.
- `WriteStates`/`ReplicaStates` string constants: they are the persisted/wire value sets (`HomeApplyResult.State`, replica status reports); a closed hierarchy would need custom JSON converters for no behavioral gain and contract risk.
- Pass-through properties on `HomePush`/`RegionReplication` (`Clock`, `Time`, `Region`, `Replicas`, …): they exist so the nested `LocalTransaction`/`Follower` classes can reach primary-constructor parameters, which nested types cannot capture in C#.
- ZLogger structured interpolation calls kept: repo-wide logging pattern, not a per-slice decision.

## Work log
- Baseline: build green at `95412a957`; worktree bootstrap needed a writable NuGet cache (redirected to /tmp) and the pinned Turso native assets (`scripts/vendor/download-turso-native.sh`).
- Fix 1 committed: one route parser (`b1866824`).
- Fix 2 committed: `ServingNodeExtensions.InternalListener` extension member (`50fec3f`).
- Fix 3 committed: `CompareAcrossIncarnations` extension member replaces the two `Compare` copies (`d8f6ebe`).
- Fix 4 committed: HomeForwarding single-rule cleanup (`f17592d`).
- Fix 5 committed: HomePush purge subjects + `RegionWriter` property (`4eaa26a`).
- Fix 6 committed: rollout endpoints share `AnswerAsync` (`69b2b36`).
- Fix 7 committed: required `RegionalOwners` (`72ed986`); HomePush's optional lookup kept for the Slow test world (noted in the commit).
- Fix 8 committed: catch-up endpoint takes `RegionReplication` (`234372e`).
- Fix 9 committed: FQN removal with `using static` for the HomePush class/namespace clash (`6eb7203`).
- Fix 10 committed: comments pass, −162 lines (`47cd988`).
- Fix 11 committed: `Task.WhenEach`, ClockGuard age read, HomeLeases `TimeProvider`, `Interlocked.Read` (`f34accc`). `[]` for `IReadOnlyDictionary` fields does not compile (CS9174: not constructible) — reverted those four sites.
- Fix 12 committed: `ReplicaRoutes.DatabasePath` for five client URL spellings; `StatusAsync` token last (`ae8fed4`).
- Tests review: all slice suites drive real servers/listeners and assert behavior (ack quorums, failover, monotonic HLC, page sync, anti-entropy, handoff drills); nothing removed. Test comments stay: they carry drill timing windows and incident/Pivot references. `RegionalHandoffTests` call site updated for the `StatusAsync` signature.
- Checks: `dotnet build src/TerraBase` green (0 warnings) after every commit. `make terrabase-test-fast` first run: 16.3 s / 30 s, 88 tests, all passed except the 10 s single-test gate on one Backup-slice test at 10.008 s (machine load avg 35 from the six parallel workers; later runs tripped the gate on five more other-slice tests). Functional result: all tests pass; see final reply for the last run.
- Slow test project builds clean.

## Remaining
- Nothing worth fixing left in the slice within this pass; ideas that need cross-slice agreement are in CROSS-SLICE below.
