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
