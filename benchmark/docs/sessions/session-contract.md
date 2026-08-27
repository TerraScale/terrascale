# GAP-D01 session and unit-of-work contract

Status: contract record for ticket TerraScaleV2 #58 (GAP-D01). The
ownership decision this contract implements is `gapd01-ownership-decision.md`
(same directory); this document maps every contract rule to V1
`IDocumentSession` observable behavior, quoting the pinned sweep identity
`fa99b9257776dbe625e6a2388c74c9ce916637ee`
(`docs/parity/ps-01-documents-sweep.md:16-17`). The implemented surface is
`src/session` (engine-side session over the landed engine seam); tests live
in `src/session/tests.rs` (commit path) and `tests/session/` (public
vocabulary). This is a contract record, not a parity claim: no row status
or Proof ID changes.

## Vocabulary

- **Session** — one engine-side unit of work bound to one `Engine`, one
  `TenantScope` (Platform-minted), and one option set. V1 analogue: one
  `IDocumentSession` (`Session/IDocumentSession.cs`).
- **Tracked document** — a document the session pinned in its identity map
  with the value it was loaded with (`original`) and the value staged now
  (`current`). V1 analogue: `DocumentsById` + `DocumentsByEntity`
  (`Session/DocumentsById.cs`; `InMemoryDocumentSessionOperations.cs:134-136`).
- **Pending put / delete** — a deferred write that exists only until the
  next save. V1 analogue: `Store`/`Delete` queuing commands
  (`Session/IDocumentSession.cs` `Store`/`Delete` members).
- **Deferred command** — a typed family write staged into the save
  transaction. V1 analogue: `Defer(ICommandData)`
  (`InMemoryDocumentSessionOperations.cs:1512-1541`).
- **Pending lazy read** — a registered read that has not executed. V1
  analogue: `PendingLazyOperations`
  (`Session/DocumentSession.Lazy.cs:52-53`).

## Contract rules

### R1 Opening and options

`Session::open(&Engine, &TenantScope, SessionOptions)` validates the scope
against the engine, then binds. Options (V1 `SessionOptions`,
`Session/SessionOptions.cs:79-177`):

| V2 option | V1 analogue | V2 rule |
|---|---|---|
| `tracking: Tracked \| NoTracking` | `SessionOptions.NoTracking` (`:90`) | `NoTracking` reads never populate the identity map or known-missing set; `RegisterMissing`-equivalents are silent no-ops (`InMemoryDocumentSessionOperations.cs:1626-1631`) |
| `transaction_mode: SingleNode \| ClusterWide` | `TransactionMode` (`:152`, enum `:6-19`) | `ClusterWide` fails at open with a typed error — no V2 cluster transaction exists (SH-05/#51); V1 validates at save (`:1007-1035`) |
| `durability: LocalDurability` | (none; V1 has no durability dial) | The local durability point every save commits to (`src/types.rs:370-379`) |

No-tracking refusals match V1's observable set: `store` throws on a
NoTracking session (`StoreInternal`, `InMemoryDocumentSessionOperations.cs:810-814`),
`save` throws (`DocumentSession.cs:86-104`), and this contract additionally
refuses `delete`, `defer`, and `lazy_load` for the same reason — their
effects only exist through tracking.

### R2 Store — deferred puts (PB-2008)

- `store(collection, id, value)` stages an overwrite put
  (`ExpectedVersion::Any`) — V1 `Store(entity, id)`: "If other entity exists
  with the same id it will be overwritten" (`Session/IDocumentSession.cs`).
- `store_new(collection, id, value)` stages a create (`ExpectedVersion::New`)
  — V1's forced-concurrency store for ids it could not extract.
- `store_checked(collection, id, value, expected)` stages the put with an
  explicit expected version — V1 `Store(entity, changeVector, id)` sets the
  change vector and forces the concurrency check
  (`InMemoryDocumentSessionOperations.cs:818-829`). The staged expectation is
  **last-write-wins session state**: a later `store`/`store_checked` on the
  same key replaces it (tracked or pending-put path alike), and a successful
  save consumes it — the re-based document saves unchecked again until a new
  checked store records one.
- Storing into a tracked document updates `current`; nothing is sent until
  save. V1 `Store` on a tracked entity only refreshes concurrency metadata
  (`InMemoryDocumentSessionOperations.cs:818-829`).
- `store_generated(collection, value)` is the no-explicit-ID path (GAP-D05
  engine half): the id is allocated at save from the identity family in the
  same transaction (decision record, class 12).

### R3 Delete — deferred deletes (PB-2009)

`delete(collection, id)` stages a delete for a tracked or untracked id,
`ExpectedVersion::Any`; `delete_expected` carries an explicit expected
version (V1 `Delete(string id, string expectedChangeVector)`,
`Session/IDocumentSession.cs`). Deleting a tracked document reports
`DocumentDeleted` in WhatChanged until the save commits.

Store and delete on one key are **last-command-wins**:

- A delete cancels a pending put for the same key. V1's `Delete` removes
  the id from `DocumentsById` and registers known-missing
  (`InMemoryDocumentSessionOperations.cs:730-763`), so the pending put
  never reaches the batch: the save sends the delete alone, post-save
  `what_changed` reports nothing, and no later save resurrects the
  document. This direction matches V1 exactly.
- A store queued after a delete subsumes it. **Accepted difference from
  V1**: V1 refuses this sequence outright — `Store` throws
  `InvalidOperationException` when a deferred command is registered for
  the id (`InMemoryDocumentSessionOperations.cs:847-848`) — while V2
  commits the subsuming put (the stored value survives, and the store
  clears the delete's known-missing registration like V1's `StoreInternal`
  at `:867-868`). The outcome-equivalence rationale: V1's own batch
  orders deletes before puts (`PrepareForEntitiesDeletion` before
  `PrepareForEntitiesPuts`, `InMemoryDocumentSessionOperations.cs:985-986`),
  so a delete+put pair V1 *could* assemble would land on the stored value —
  but that pair is unreachable in V1, and the divergence is recorded here
  and in the WS-PARITY proposals (PB-2008/PB-2009) rather than passed off
  as parity. The engine's one-write-per-target transaction contract
  (`src/storage/file.rs`) makes the subsuming put the only stageable
  command; a typed refusal was considered and rejected at the repair
  re-gate — last-command-wins is deterministic, tested, and creates no
  resurrection or durability hazard.

### R4 Load — identity map, batch, known-missing (PB-2005)

- `load(collection, id)` resolves in order: pending puts (read-your-writes),
  pending deletes (absent — the delete registered known-missing, and V1's
  `Load` answers null for a session-deleted id before consulting the
  identity map, `LoadOperation.cs:236,255`), the identity map, then one
  engine snapshot read. The first loaded value is pinned: later external
  commits are invisible to `load` until `refresh`, `evict`, or a save —
  V1's one-instance-per-document guarantee.
- `load_many` resolves a batch of ids in one snapshot pass.
- An absent document registers **known-missing**: `is_loaded` reports `true`
  (V1 `IsLoaded`/`IsLoadedOrDeleted`, `InMemoryDocumentSessionOperations.cs:457-469`)
  and `is_known_missing` distinguishes it; repeated loads do not re-track.
- Collection-scoped keys: identity map entries key by (collection, id) —
  storage keys documents by tenant, collection, and identifier
  (`src/engine/mod.rs:939-985`).

### R5 Refresh, evict, clear (PB-2010)

- `refresh(collection, id)`: re-read from a fresh engine snapshot and replace
  both `original` and `current` (V1 `Refresh<T>` discards local changes,
  `IAdvancedSessionOperations.cs:40`).
- `evict(collection, id)`: remove from the identity map, the known-missing
  set, the pending-lazy set, and the delete queue (V1 `Evict<T>` does all
  four, `InMemoryDocumentSessionOperations.cs:1471-1489`).
- `clear()`: empty the identity map, known-missing, deletes, pending puts,
  deferred commands, and pending lazy reads (V1 `Clear`,
  `InMemoryDocumentSessionOperations.cs:1492-1509`).

### R6 WhatChanged (PB-2012, GAP-D06)

`what_changed()` returns one report per changed document, in canonical
(collection, id) order, comparing `current` against `original`:

- V1's change-type set maps 1:1: `DocumentAdded`, `DocumentDeleted`,
  `FieldChanged`, `NewField`, `RemovedField`, `ArrayValueChanged`,
  `ArrayValueAdded`, `ArrayValueRemoved`
  (`Session/DocumentsChanges.cs`, `ChangeType`).
- Each field change carries old value, new value, field name, dotted field
  path, and `field_full_name` (`path.name`, name alone at the root) — V1's
  `FieldOldValue`/`FieldNewValue`/`FieldName`/`FieldPath`/`FieldFullName`.
- Array diffs are index-positional: same index with a different value is
  `ArrayValueChanged`, indices past the old length are `ArrayValueAdded`,
  indices past the new length are `ArrayValueRemoved`; the field name is the
  index as text. Within one document, fields report in canonical
  (alphabetical) name order — a deterministic order, where V1 reported in
  serialization order.
- Re-inspection is idempotent and non-consuming: two calls return equal
  reports (V1 `WhatChanged()` builds a fresh dictionary per call).
- `has_changed(collection, id)` and `has_changes()` answer V1's
  per-entity/whole-session questions; `ignore_changes_for` keeps a document
  tracked but outside every save and report (V1 `IgnoreChangesFor`,
  `InMemoryDocumentSessionOperations.cs:1460-1468`).

### R7 SaveChanges — the unit-of-work boundary (PB-2001)

`save_changes()` executes, in order:

1. **Pending lazy reads execute as one batch** (GAP-D14's batch-at-save
   rule; V1's batch shape from `ExecuteAllPendingLazyOperations`,
   `DocumentSession.cs:191`).
2. **Save-time validation**, before anything is staged: a deferred command
   guarding a document this save also deletes refuses the whole save —
   V1 `ThrowInvalidDeletedDocumentWithDeferredCommand` (delete preparation,
   `InMemoryDocumentSessionOperations.cs:1148-1152`). No partial commit is
   possible because nothing has been staged yet.
3. **Hooks** fire with the same cannot-partially-commit guarantee: every
   `before_store` (value mutable, veto aborts the save), every
   `before_delete` (veto aborts) — V1 fires both inside save preparation
   where a throw sends nothing (`PrepareForEntitiesPuts`/`Deletion`).
4. **Generated ids** allocate from the identity family in this
   transaction (before the puts that carry them).
5. **One engine transaction** stages writes in V1's batch order —
   **deletes, then puts, then deferred commands** (V1's
   `PrepareForEntitiesDeletion` before `PrepareForEntitiesPuts`,
   `InMemoryDocumentSessionOperations.cs:985-986`; the session-vs-deferred
   merge is `Session/Operations/BatchOperation.cs:37-39`) — and commits
   once at the configured durability.
6. **On success**: committed state clears — changed documents re-base
   (original := current, the checked expectation consumed — the next save
   is unchecked until a new `store_checked`), deleted documents leave the
   identity map and known-missing set, deferred commands, pending puts,
   and generated-id reservations report once through the save result then
   clear. V1 `ClearSessionStateAfterSuccessfulSaveChanges`
   (`InMemoryDocumentSessionOperations.cs:2605-2630`).
7. **On rejection or unknown outcome**: session state is kept for retry;
   the engine's `CommitOutcome` (with the operation identity for
   `resolve_commit`) passes through unchanged; the frozen retry mapping is
   `CommitError::retry_disposition` (`src/engine/mod.rs:332-350`).

The save returns `Result<CommitOutcome, SessionError>`: pre-commit refusals
(validation, hooks, NoTracking) are `Err` and commit nothing; engine commit
outcomes are the pass-through `Ok(..)` arm.

### R8 Defer (PB-2013, GAP-D08)

`defer(SessionCommand::Family(FamilyWriteRequest))` appends to an ordered
queue. At save, deferred commands stage into the same transaction after the
puts (V1 order, R7.5), so documents and family writes commit atomically —
the primitive the sweep already proves (`src/families/mod.rs:661-677`;
`src/engine/tests.rs:887`). Save-time validation covers the
deferred-vs-delete rule (R7.2); operation-identity and tenant binding are
enforced by the family stage seam itself.

### R9 Lazy reads (PB-2019, GAP-D14)

`lazy_load` registers into the pending set without reading. Any of
`execute_all_pending_lazy_operations()` or the next `save_changes()` runs
the **whole pending set as one batched load** (one batch per execution,
observable through `lazy_batches_executed()`), tracking each result — V1
evaluates any lazy value by executing all pending lazy operations
(`Session/DocumentSession.Lazy.cs:128-141`). Evict and clear remove pending
entries. The engine's batch read is per-collection, so a pending set
spanning several collections resolves as one batched load per collection
inside the single execution — one execution, one `lazy_batches_executed`
increment, however many collection batches it took.

### R10 Streaming (PB-2023, GAP-D15)

- `stream_documents(selector)` opens a W4-STREAM cursor over a fresh
  snapshot; `next_stream_batch(stream, limit)` serves bounded batches in
  canonical key order with exactly-once terminal completion
  (`src/streaming/mod.rs:84-350`). Stream-result metadata rides the batch:
  items carry document + metadata, completion carries the terminal
  observation.
- `load_starting_with(collection, prefix, matches, start, page_size)` is
  V1's paged prefix load (`IAdvancedSessionOperations.Load.cs`;
  `DocumentSession.Stream.cs:114`) over the same selector seam: canonical
  order, skip `start`, take `page_size`. The numeric `start` is a skip in
  canonical key order (accepted difference: the seam is cursor-based).

### R11 Hooks (PB-2027, GAP-D16)

Engine-side lifecycle hooks, registered before save:

| Hook | V1 event | May mutate | May veto | When |
|---|---|---|---|---|
| `before_store` | `OnBeforeStore` | the staged `DocumentValue` | yes — aborts the save, nothing staged or committed | save preparation, per put |
| `before_delete` | `OnBeforeDelete` | nothing | yes — aborts the save | save preparation, per delete |
| `after_save` | `OnAfterSaveChanges` | nothing | no (observation only) | after the commit outcome is known |

Conversion hooks are accepted-difference (decision record, class 10);
request-success/failure and topology-update events belong to the host
executor (SE-08, PB-2022).

### R12 Generated identity (PB-2003 engine half, GAP-D05)

`store_generated(collection, value)` reserves a slot; at save the session
reads the collection's identity range (`documents/<collection>`) on the
save snapshot, stages `IdentityCommand::Allocate { count }` in the same
transaction, and assigns sequential decimal-string ids in store order. The
assigned ids are reported by the save result (`generated_ids`) — the range
and the documents commit or roll back together, so a crashed save leaks no
ids. Cross-client HiLo block distribution is GAP-F9's; the recorded limit:
two concurrent sessions whose snapshots predate each other's commits can
compute overlapping hand-outs — the allocation guard for that window is
GAP-F9's block-distribution work (decision record, class 12).

## What this contract does not claim

- No V1/V2 parity claim and no performance claim; nothing here ran the
  benchmark lane.
- Rows PB-2000/2001/2003(engine half)/2005/2008/2009/2010/2011/2012/2013/
  2016(mode)/2019/2023/2027 gain V2-side focused-test evidence at the
  session seam; their `unresolved` statuses and Proof IDs are the
  integrator's to change (`docs/parity/gapd01-ws-parity-proposals.md`
  records the proposals).
- The C# host session API, TBEP session frames, MCP exposure, bulk-insert
  streaming, cluster-wide transactions, includes (GAP-D11), conditional
  load (GAP-D02/D03), and request caching (GAP-D12, SDK-side) are out of
  this contract's scope.
