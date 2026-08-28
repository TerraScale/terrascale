# GAP-Q18: Index lifecycle operations, state, staleness (decision record)

Status: implemented (engine seam, in-memory catalog). Folds GAP-Q9
(bounded wait-for-non-stale and state-specific staleness) and the
engine-statistics residual of GAP-D18's ownership split.

Rows covered: PB-4053 (create/delete/start/stop/reset/enable/disable),
PB-4054 (state and staleness), PB-4055 (errors and failure information),
PB-4056 (engine-owned observability subset), PB-4057 (lock/priority and
side-by-side reset), PB-4026 (GAP-Q9 wait semantics). PB-4062
(admin authorization) is explicitly **not** touched: per charter
PD-2026-08-26-11 there is no authorization surface at the engine seam;
the authorization class resolves at the Platform Adapter (W5).

V1 anchors cite the pinned read-only tree at
`/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly`
as `V1:` plus `file:line`.

## D1. Two lifecycles, one per index

V1 keeps one `IndexState` per index — `Normal`, `Disabled`, `Idle`,
`Error` (V1: `src/TerraBase.Client/Documents/Indexes/IndexState.cs:3-9`)
— driven by both administration (start/stop/enable/disable/reset) and
engine-internal failure (error limits move the index to `Error`,
V1: `src/TerraBase.Server/Documents/Indexes/Index.cs:2196-2218`).

V2 already lands one lifecycle: the **automatic** lifecycle of
optimizer-created indexes (`IndexLifecycleState::{Active,Idle,Retired}`,
IX-03, `src/query/optimizer/lifecycle.rs`), driven by measured usage
thresholds. GAP-Q18 adds the second, orthogonal lifecycle — the
**operational** state every explicitly created index carries:

```
IndexOperationalState { Normal, Paused, Disabled, Error }
```

Mapping to V1's single enum and its two axes:

| V1 concept | V1 anchor | V2 home |
| --- | --- | --- |
| `IndexState.Normal` | IndexState.cs:5 | `OperationalState::Normal` |
| paused (node-local stop, `StopIndex`) | StopIndexOperation.cs:9-13 | `OperationalState::Paused` |
| `IndexState.Disabled` (persistent stop, `DisableIndex`) | DisableIndexOperation.cs:8-14 | `OperationalState::Disabled` |
| `IndexState.Error` (failure entry) | Index.cs:1235, 2196-2218 | `OperationalState::Error` |
| `IndexState.Idle` (auto-index idle) | IndexState.cs:7 | `IndexLifecycleState::Idle` (IX-03, unchanged) |
| faulty-always-stale | Index.cs:1373 | D3 rule for `Error` |

Both axes fold into one enum here (V1 carries `State` and
`IndexRunningStatus` separately); the fold keeps one unambiguous legal
transition table instead of two interacting ones. `Disabled` dominates
`Paused`: a disabled index is inert at the stronger, persistent level.

Ownership rule (fail-closed): the typed lifecycle operations apply to
**explicit indexes only**. An `Auto/` namespace refuses them with
`IndexOperationError::NotExplicitIndex` — the automatic lifecycle of an
auto index belongs to the optimizer (charter PD-2026-08-26-09:
auto-indexes are merged, superseded, and retired automatically), so no
administrative transition ever fights it. V1 allows admin stop of auto
indexes; V2's zero-configuration rule is the recorded difference
(WS-PARITY proposal P1, below).

## D2. The state machine (exhaustive)

Operations: `start` (resume from pause — V1: `StartIndexOperation.cs:9-13`),
`stop` (pause — V1: `StopIndexOperation.cs:9-13`), `enable`
(clear persistent disable — V1: `EnableIndexOperation.cs:11-16`),
`disable` (persistent stop — V1: `DisableIndexOperation.cs:8-14`),
`reset` (drop indexed data and re-index — V1:
`ResetIndexOperation.cs:9-19`), `clear_errors` (admin error clearing),
and the engine-internal `record_failure` ingress. Every cell is one of:
**transition** (state changes), **no-op** ( succeeds, `changed == false`,
V1's already-running guard — V1: `Index.cs:1020-1022`), or **refused**
(`IndexOperationError::IllegalTransition`, state and all other index
state unchanged).

| from \ op | start | stop | enable | disable | reset | clear_errors | record_failure |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Normal | no-op | **Paused** | no-op | **Disabled** | **Normal** (data dropped, rebuilt) | refused | records; at threshold **Error** |
| Paused | **Normal** | no-op | no-op | **Disabled** | **Normal** | refused | refused (nothing indexes while paused) |
| Disabled | refused | refused | **Normal** | no-op | **Normal** | refused | refused (no indexing runs) |
| Error | refused | refused | refused | refused | **Normal** (errors cleared) | **Normal** (errors cleared) | records, stays Error |

Anchors for the refusals:

- `start`/`enable` refuse from `Error`, and `start` refuses from
  `Disabled`, because V1's indexing thread refuses to start in exactly
  those states (V1: `Index.cs:1040-1041`:
  `if (State == IndexState.Disabled || State == IndexState.Error) return;`).
  The documented way out of `Error` is `reset` (V1:
  `IndexStore.cs:1522-1524` replaces the instance on side-by-side reset)
  or `clear_errors` once the fault is cleared (V1's non-error transition
  clears the error reason — V1: `Index.cs:2806-2812`).
- `stop`/`record_failure` refuse from `Disabled` and `Error`: those
  states index nothing (V1: Index.cs:1040-1041), so there is nothing to
  pause and nothing that can fail.
- `clear_errors` refuses on every non-`Error` state: clearing an error
  list that does not exist is an ambiguous request, and the seam prefers
  one typed refusal over a silent second meaning for an operation name.
- `enable` from `Paused` is a no-op, not a transition: `enable` addresses
  the persistent axis only; the transient pause stands until `start`
  (the two-axis fold, D1).

`reset` accepts a mode (V1: `ResetIndexOperation.cs:44-46`):
`InPlace` or `SideBySide` (D6). Both return the index to `Normal`,
clear the recorded error list and the consecutive-failure counter, and
drop pendings — V1: "If the index is disabled or paused, resetting will
return it to the normal running state" (ResetIndexOperation.cs:17-19).

## D3. Staleness contract per state

Staleness is a function of guarded catalog state only — deterministic,
no clock input:

| state | staleness rule |
| --- | --- |
| Normal | coverage-based: stale iff the namespace's covered storage sequence trails the latest observed publication sequence (`CoverageBehind`), or nothing was ever published while publications exist (`NotYetPublished`). An engine with no observed publication at all has nothing to cover: not stale. |
| Paused | same coverage rule (V1: "Although a paused index can still be queried, results may be stale" — StopIndexOperation.cs:12-13). The state is reported as a reason when stale, because a paused index cannot catch up (D4 proves it terminates). |
| Disabled | same coverage rule (V1: "Querying a disabled index is still allowed, but results may be stale" — DisableIndexOperation.cs:9-10). Same cannot-catch-up consequence. |
| Error | **unconditionally stale** — mirrors V1's faulty index, whose `IsStale` adds the reason "Index is faulty." and returns true regardless of coverage (V1: Index.cs:1362-1376). The query seam sees this through `available_generation`: an Error namespace supplies no generation, so every staleness observation against it reports stale and a strict policy fails with `QueryError::IndexNotReady` — an error index never silently vouches for coverage it cannot have. |

Typed reasons (`StalenessReason`): `ErrorState`, `CoverageBehind {
covered, latest }`, `NotYetPublished`. They are the typed form of V1's
`stalenessReasons` list (V1: Index.cs:1362-1376).

## D4. Bounded wait-for-non-stale (folded GAP-Q9, PB-4026)

V1's contract: a query may wait for non-stale results under an explicit
timeout; when the bound elapses and the result is still stale, the
client observes a timeout error (V1:
`src/TerraBase.Client/Documents/Session/Operations/QueryOperation.cs:453-465`,
`TimeoutException` "Waited … for the query to return non stale
result."). The default bound is 15 seconds (V1:
`src/TerraBase.Client/Documents/Conventions/DocumentConventions.cs:91`;
the server applies the same default when the request carries none — V1:
`src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs:191-194`).

V2's seam-level contract (`IndexStore::wait_for_non_stale`):

- The bound is a `Duration` carried by `WaitForNonStale`
  (`DEFAULT` = 15 s, V1 parity). A zero bound is legal and means
  "evaluate once, never step".
- The wait **never sleeps**. The engine seam is synchronous, so the wait
  *drives* progress instead of parking: each iteration runs at most one
  bounded rebuild step (the same step `IndexStore::rebuild` runs), then
  re-evaluates staleness.
- Termination is deterministic on three exits, whichever comes first:
  1. **NonStale** — the namespace is fresh;
  2. **TimedOut** — the lifecycle clock says the bound elapsed
     (checked before every step, so a zero bound returns TimedOut after
     one evaluation without doing work);
  3. **TimedOut (no progress)** — the step could not run or changed
     nothing (namespace paused, disabled, in error, or already
     up-to-date while something else holds the staleness). No sleep and
     no spin: a state that cannot progress ends the wait immediately.
- The outcome carries the final `IndexStaleness` (state + reasons), so
  the caller — host or adapter, the engine's callers own client-facing
  error mapping — can translate a TimedOut exactly like V1's client
  does. The engine returns the typed outcome; it does not throw.

GAP-Q9 disposition: **met** at this seam (bounded, deterministic,
state-aware wait), recorded as implemented. What GAP-Q9 explicitly does
not claim: a wire-protocol wait flag and its host-side error mapping
(adapters wave), and any wall-clock latency behavior — the wait contains
no timing claim (PD-2026-08-26-03).

## D5. Error indexes (PB-4055)

- Build and publish failures record one content-safe
  `IndexErrorRecord { action, category, at }` — action
  (`Build`/`Publish`) and a typed failure category drawn from the
  existing `IndexError` vocabulary. No document data, no field values,
  no paths: the record names the violated rule class only.
- The per-index error list is bounded: the newest
  `MAX_INDEX_ERRORS_PER_INDEX` (64) records are retained.
- Consecutive failures accumulate; at `INDEX_ERROR_STATE_THRESHOLD` (8)
  the operational state moves to `Error` — the typed form of V1's
  error-limit rule (V1: Index.cs:2196-2218 — a failure past
  `UnexpectedErrorsLimit` calls `SetState(IndexState.Error)`).
  Any successful step resets the consecutive counter.
- The ingress is engine-internal (`IndexCatalog::note_operation_failure`,
  crate-private): only the engine's own build/publish paths feed it.
  There is deliberately no public failure injector — fabricating engine
  failures is not a capability. In-crate tests drive the ingress
  directly; the observable consequences (unconditional staleness,
  refused transitions, clearing, listing) are tested at the public seam.
- `errors()` lists per tenant (every explicit index that carries recorded
  errors, in catalog order); `clear_errors` is the typed clearing op (D2).
- The "performs no indexing" rule of the non-normal states is enforced by
  the driver — the maintenance step and the wait refuse to build in a
  non-normal state (`RebuildStepOutcome::Skipped`) — while the
  compare-and-publish core stays state-agnostic. In this engine the only
  production drivers of explicit builds are the typed step and the wait,
  and the optimizer's automatic pass touches auto indexes only (whose
  operational state is always `Normal`), so the invariant holds by
  construction and stays testable at the seam.

## D6. Reset modes and side-by-side swapping (PB-4057)

- `InPlace`: every epoch's published segments and publication history
  are dropped and generation numbering restarts — "removes all existing
  indexed data and re-indexes" (V1: ResetIndexOperation.cs:11-12). The
  index is stale (`NotYetPublished`) until the next rebuild publishes.
- `SideBySide`: the namespace allocates the next epoch **keeping its
  definition and deployment record**; the old epoch's segment keeps
  serving until the new epoch's first publication cuts over — the
  in-memory catalog's epoch mechanism *is* the side-by-side slot
  (compare V1: `IndexStore.cs:1522-1524` resets side-by-side by building
  a replacement). Queries against old snapshots keep resolving their
  observed generations from the old epoch's history; the switch of
  `available_generation` to the new epoch is the cutover, and — exactly
  like any epoch change — a strict staleness policy against an
  old-snapshot observation refuses rather than comparing across epochs
  (existing W3-2 rule, unchanged).

Locking and priority (PB-4057):

- `IndexLockMode { Unlock, LockedIgnore, LockedError }` — V1:
  `IndexLockMode.cs:22-27`, semantics per V1: IndexStats.cs:119-126.
  A re-`create` (definition change) under `LockedIgnore` succeeds as a
  recorded no-op (`Ignored`); under `LockedError` it fails closed with
  `IndexOperationError::DefinitionLocked`.
- `IndexPriority { Low, Normal, High }` — V1: `IndexPriority.cs:22-27` [integrator rider: cite corrected — the pinned file ends at line 28].
  Priority is deployment metadata, observable in statistics. It has no
  scheduling effect in the in-memory catalog (there is no indexing
  thread pool to order); the IX wave owns any scheduling semantics.
  Recorded as qualified, not behavioral.

## D7. Engine statistics surface (PB-4056 subset, GAP-D18 residual)

Per index (`IndexStatistics`), the engine-owned subset of V1
`IndexStats` (V1: `src/TerraBase.Client/Documents/Indexes/IndexStats.cs`):

| V1 field (line) | V2 typed shape |
| --- | --- |
| `Name` | namespace + catalog name |
| `State` (:101) | `IndexOperationalState` (+ auto lifecycle state for auto indexes) |
| `Priority` (:106) | `IndexPriority` |
| `LockMode` (:126) | `IndexLockMode` |
| `IsStale` (:118) | `staleness().stale()` + typed reasons (D3) |
| `MapAttempts`/`MapSuccesses`/`MapErrors` (:19/:24/:29) | saturating `u64` counters (build attempts / successes / failures) |
| `CreatedTimestamp` (:111) | lifecycle-clock instant of registration |
| `LastIndexingTime` (:116) | lifecycle-clock instant of the last successful publication |
| `LastQueryingTime` (:93) | the existing usage report (`mark_queried`, AX-20) |
| — (entries) | published segment's entry count and covered sequence (coverage facts staleness derives from) |

Deliberately excluded: `MappedPerSecondRate`/`ReducedPerSecondRate`
(:69-77) and every rate/percentage field — they are performance
statements, and PD-2026-08-26-03 forbids any timing claim outside
TerraBase.Benchmark receipts (PB-4061/GAP-Q20 own the observation
contract); reduce-side counters (map-reduce output) wait for PB-4042;
`Collections` breakdown, `Type`, `SearchEngineType` have no V2
counterpart yet (PB-4052/GAP-Q17, PB-4058).

`Engine::indexes().engine_statistics()` (D18 residual, engine-owned aggregate):
per-tenant counts of explicit indexes by operational state, plus totals
(build attempts/successes/failures summed, saturating) and the bounded
recorded-error total. Shape: tenants in name order, one row per tenant,
no per-document data.

GAP-D18 split recorded here: engine-owned = the typed per-index and
per-tenant statistics above (implemented); Platform-owned = anything
authorization-gated, node-routed, or cluster-scoped (admin operation
classes, cluster-wide enable/disable — V1: EnableIndexOperation.cs:19-26
`clusterWide` — billing/alerting surfaces), which stay outside the
engine per charter PD-2026-08-26-11. GAP-D18 itself (the ownership
adjudication) remains the parent's record; this section is the
engine-side input to it.

## D8. The engine seam

`Engine::indexes() -> IndexStore<'_>` — the borrowed-store pattern of
`Engine::queries()`/`Engine::families()`/`Engine::streams()`. One
store, one engine-owned catalog, all operations typed:

`create` (definition + deployment options), `delete`, `start`, `stop`,
`enable`, `disable`, `reset`, `set_lock`, `clear_errors`, `state`,
`staleness`, `wait_for_non_stale`, `rebuild` (one bounded maintenance
step; hosts own cadence per PD-2026-08-26-01 D1), `errors`,
`statistics` (per index), `engine_statistics` (aggregate).

`delete` removes the namespace and keeps a bounded tombstone in the
existing retired-records store (explicit tombstones carry the kind and
an empty transition history), drops pendings that name it, and bumps
the catalog revision — the same publication-order bookkeeping the auto
retirement uses.

Deferred (not claimed): database-wide start/stop-indexing operations
(V1: StartIndexingOperation/StopIndexOperation) — the per-index ops are
the seam; a database-wide pair is a host-convenience fold recorded for
adjudication. `StaticIndexDefinition` (GAP-Q06 language) → catalog
registration lowering is not wired: the catalog's definition shape is
one collection plus typed fields today, and the language's multi-map /
all-documents sources need their own catalog support first; recorded as
the GAP-Q06 follow-up. Persistence of operational state across restart
is not claimed (the catalog is in-memory W3-2 state; V1 persists
`Disabled` — DisableIndexOperation.cs:12-14 — via its database record;
recovery re-derives V2 state from the same definitions and commits).

## D9. WS-PARITY proposals (for the parent to adjudicate)

- P1: admin start/stop of *auto* indexes refused (V1 allows) — V2
  zero-configuration rule owns the auto lifecycle. Proposed
  disposition: accepted-difference.
- P2: `Idle` lives in the automatic lifecycle (preference), not the
  operational one — same observable state set, different owner.
- P3: `enable`-from-`Paused` is a no-op (V1's two axes compose
  differently). Proposed: accepted-difference (fold documented in D1).
- P4: database-wide start/stop indexing and cluster-wide enable/disable
  deferred to the host/Platform (D8). Proposed: accepted-difference
  until a host surface exists.
- P5: wait-for-non-stale returns a typed outcome; the timeout *error*
  is caller-mapped (V1 throws client-side — QueryOperation.cs:461-464).
  Proposed: parity-preserving at the engine seam.

## Test evidence

RED (tests written first, before any implementation existed;
`cargo test --lib index::operations_tests --offline --locked` failed to
compile with 57 errors, exit non-zero):

- `error[E0432]: unresolved import 'super::operations'` — the typed
  surface module does not exist.
- 24 × `error[E0433]: cannot find 'operations' in 'super'` — the state,
  error, wait, and statistics types do not exist.
- 20 × `error[E0599]: no method named 'indexes' found for struct
  'Engine'` — the engine seam does not exist.
- 9 × `error[E0599]: no method named 'note_operation_failure'` — the
  failure ingress does not exist.
- `error: could not compile 'terrabase-v2' (lib test) due to 57 previous
  errors`.

Rust cannot compile assertion-level tests against a seam that does not
exist, so the RED evidence is the compile failure of the full test
suite, written before the implementation and unchanged in its contract
after it. Two test-side mechanical defects were fixed during the RED
phase before any implementation existed (a `matches!` message placed
inside the macro, and V1-wrong enum variant names
`LocalDurability::Durable`/`ExpectedVersion::None` corrected to the
crate's `Synced`/`Any`); one test defect was fixed after the first
GREEN run (`enable` used where the D2 matrix means `start` from Paused
— the implementation was correct, the test contradicted the documented
matrix).

GREEN (`cargo test --lib index::operations_tests --offline --locked`,
dev profile): 20 passed, 0 failed — the exhaustive matrix test
(`lifecycle_transition_matrix_is_exhaustive_and_typed`) covers all 24
(op-state × operation) cells. Full crate suite after implementation:
828 passed, 0 failed, 1 ignored.

## Integrator receipts (post-merge, develop)

- Focused gate receipts reproduced green by both gates; the full
  `scripts/check.sh` obligation is discharged by the integrator's
  merged-develop run recorded at push time (the packet worktree ran it
  green at `e79688e`).
