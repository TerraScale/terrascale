# r2 replication decision log

Slice: src/TerraBase/Features/{Replicas,Sync,HomePush,HomeApply,LocalAck,Regions,Clock}, tests/TerraBase.{Fast,Slow}/Sync, tests/TerraBase.Slow/Clock. Base: origin/terrabase-r2 (= main with round 1, PR #1358). Round 1 log: `.untangle/replication.md` on `terrabase-modernize-replication`; its "Remaining" list is empty, so round 2 is the four new house rules.

## Fix 1 — comment deletion across the slice (house rule 1)
~215 full-line comment lines in src and ~90 in the test folders, plus 2 inline (`HomePush.cs:517`, `InRegionAck.cs:191`) and 2 `///` summary blocks in Slow tests. Read before deleting; classification:
- Most explain cross-process concurrency/ordering invariants (regional-owner CAS ordering, lease fencing, handoff phases, HLC reservations, relay semantics). Code stays as-is; per the rule the comment goes and the names/types already carry what they can. Deleting wholesale, one commit per folder.
- THIRD-PARTY (code kept, comment deleted, constraint listed for the coordinator):
  - `Features/Regions/HomeSocketRelay.cs:43` — ASP.NET Core `SetRequestHeader` replaces an existing header, so multi-value headers are joined as the wire format does.
  - `Features/Regions/HomeSocketRelay.cs:11` — relay uses RFC 8441 HTTP/2 extended CONNECT on the internal listener.
  - `Features/Features-free`: `Features/Replicas/RegionReplication.cs:107` — ts-orch does not supply NATS to every cell yet, so replicas fall back to the sweep alone.
- `Features/HomePush/TransactionCrypto.cs:17` describes our own persisted TBP1 envelope: persisted format is a contract, code unchanged, comment deleted, not third-party.
- The "legacy owner"/"legacy platform keys" comments (`RegionalHandoff.Control.cs`, `HomeLeases.cs:620`) describe handling of already-persisted Version:0 records. Persisted formats are not legacy (HARD LIMITS), so the code paths stay; the comments go. Reported under CROSS-SLICE for awareness, not removed.

## Fix 2 — fully qualified names removed (csharp rule 6)
- `Features/Regions/HomeForwarding.cs:214` `System.Globalization.CultureInfo.InvariantCulture` → using/short name.
- `tests/TerraBase.Slow/Sync/LocalWriteWorld.cs:209`, `RouterDifferentialTests.cs:106`, `RegionalWriterRecoveryTests.cs:160,175` — same treatment in test files.

## Fix 3 — workaround hunt (house rule 2), all judged real design, no change
- `HomePush/HomePush.cs` RetryAsync/backoff loops, `RegionalHandoff.cs:97` NATS connect delay, `HomeMoves.cs:394` DrainAsync bounded backoff, `InRegionAck.cs` contact sweep interval, `Clock/ChronyClock.cs`/`ClockReports.cs` reconnect (`MaxReconnectRetry = -1` is the NATS "retry forever" contract): bounded or persistent backoff against external services (NATS, storage, peers); the product's durability contract, not a patch over our bug.
- Bare `catch { cleanup; throw; }` blocks (`RevisionPush.cs:48`, `RegionalIngress.cs:51,81`, `HomePush.cs:868`, `ReplicaVerifier` area) re-throw after dispose: cleanup-on-throw, not swallowing.

## Fix 4 — legacy hunt (house rule 3)
`rg -ni legacy|compat|obsolete|fallback|...` hits are (a) persisted Version:0 owner records and legacy platform keys — persisted formats, kept per HARD LIMITS; (b) local variable names (`old`, `older`, `fallback`) naming the previous writer/record in move and recovery logic — not legacy paths. No dead code, `[Obsolete]` or shims found. No change.

## Fix 5 — unit-test hunt (house rule 4)
Round 1 already reviewed the suites (all drive real listeners/servers via `TerraBaseTestServer`, Postgres wire, drill worlds). Re-check `new [A-Z]` hits in test folders for isolation tests; expected outcome: helpers/fakes of *external* dependencies (NATS fault injection, delayed apply) are legitimate; anything constructing our internals in isolation gets rewritten through the server or deleted. `LocalWriteWorld` constructs `HoldStore`/`InRegionAck` with the real storage engine to fake an unhealthy partner node — a fake of a node's health, driving the real server; check whether it can target the real endpoint instead.

## Fix 6 — modern C# spot checks
Self-check greps (rule 5 of the brief): comments gone, `System.*` qualified names gone. Re-verify no `_ =>` arms on owned closed types appeared; keep round 1's modernizations as they are.

## Outcomes (post-work)
- Fix 1: 185 comment lines deleted in src (Replicas 148, HomePush 12, LocalAck 5, Regions 17, Clock 3) and 76 in tests, plus 2 inline and 2 `///` blocks. THIRD-PARTY kept-code/deleted-comment list above.
- Fix 2: 5 FQN sites fixed (1 src, 4 tests). Self-check grep now empty.
- Fix 3/4: no workarounds or legacy paths needed changes; findings recorded above.
- Fix 5: `RegionReplicaTests` constructs `TursoReplicas` directly but drives the real home server over HTTP with real storage (encryption-at-rest leak scans, delayed-answer interleaving, checkpoint/reopen) — integration tests of the replica subsystem, kept. `LocalWriteWorld` builds a real second node from real stores to fake partner health — kept. `ClockSafetyTests` drives the real gRPC clock service — already through the public interface. No unit tests deleted.
- Reviewed and left: `DateTime.UtcNow` for test credential expiry and one `Task.Wait()` rendezvous in `HomeMoveDeleteFenceTests`' paused-checkpoint drill (test-side blocking by design).
- Checks: `dotnet build src/TerraBase -c Debug` 0 errors/0 warnings; Slow and E2E test builds 0/0; `dotnet format whitespace --verify-no-changes` clean for src, Fast and Slow; `make terrabase-test-fast`: 12.837 s / 30 s, 88 tests, slowest 7.7 s, all passed.

## Round 2 continuation (coordinator review pass)

### Fix 7 — one Backoff sequence for every retry loop
The doubling expression `TimeSpan.FromTicks(Math.Min(max, current * 2))` was hand-rolled five times: `HomePush.cs` ConsumeAsync/ApplyAsync/RetryAsync, `RegionReplication.cs` Follower.RetryAsync, `HomeMoves.cs` DrainAsync. New `Backoff` struct (`Features/Replicas/Backoff.cs`); all five use `Next()`/`Reset()`. Solve: coordinator target 1 (duplicated backoff logic).
- `HomePush.cs` ConsumeAsync/ApplyAsync/RetryAsync
- `RegionReplication.cs:1014` Follower.RetryAsync
- `HomeMoves.cs:393` DrainAsync

### Fix 8 — one rollout-status reader in RegionalHandoff
`GET /v1/rollout/status` + `RegionalStatus` decode was copied 5× and the self-region peer lookup 3×. New `StatusAsync`, `AwaitStatusAsync` (both 25 ms settle polls) and `SelfRegionPeerAsync`.
- `RegionalHandoff.Control.cs` NodeReadyAsync, BeginDrainAsync, ReplacedSourceReadyAsync
- `RegionalHandoff.cs` DrainCoreAsync activation and observer polls

### Fix 9 — HomePush takes RegionalOwners directly
`services.GetService<RegionalOwners>()?.Writer ?? true` was a fallback only the Slow test world needed (round 1 kept it); `RegionalOwners` is now a constructor dependency and `LocalWriteWorld` passes an instance. The test world's PeersFile-less options keep `Writer` true, matching the old default. Solve: coordinator target 4 (test-only seam).

### Fix 10 — file splits by change reason (coordinator target 1)
- `RegionReplication.cs` (1,035) → follower/publish/notice engine + `RegionReplication.Catalog.cs` (peers listing, home catalog reads, record import, missing-replica resolution, teardown).
- `HomeLeases.cs` (833) → per-database lease lifecycle + `HomeLeases.Node.cs` (node session: RenewAsync, Lapse, TakeNodeAsync, PublishNodeAsync).

### Judged and left
- State machines as flags (target 1): `RegionalTransfer.Phase` and `ReplicaStates`/`WriteStates` are persisted/wire string value sets (HARD LIMITS: no contract change); `HomePush`'s `_ready`/`_js`/`_nats` are lifecycle availability, not a closed variant set. No change.
- HomeMoves vs RegionalHandoff (target 2): move flows post `/v1/home-move*` routes with their own timeout helper; handoff posts `/v1/rollout` routes. Different contracts, no shared steps beyond the generic post-JSON shape each class already owns. No copies.
- `ConsumeAsync`'s 100 ms `RegionWriter` poll (target 3): samples a time-varying property (lease expiry timestamp + KV-observed owner + drain flag); an event cannot express time-based expiry, so polling is the design, not a workaround.
- Backoff waits against NATS/storage/peers stay (external, bounded).
