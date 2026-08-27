# GAP-D01 ownership decision record: engine-side vs host-side vs SDK-side session semantics

Status: decision record (the ticket's first deliverable, written before any
code). Ticket TerraScaleV2 #58 (GAP-D01, folds riders D05/D06/D08/D12/D14/
D15/D16). V1 evidence is quoted against the pinned sweep commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee`
(`docs/parity/ps-01-documents-sweep.md:16-17`); every V1 citation below was
re-resolved against that checkout. No parity row status changes here.

## Inputs

- Charter PD-2026-08-26-01 D1 (language split): this repository's Rust scope
  is the engine core — storage, WAL, engine, families, query, index,
  streaming, replication and sync core, event backbone, interop ABIs. Public
  API hosts, CLIs, and adjacent tooling are C#/.NET under `host/` and `cli/`,
  speaking TBEP to the engine process (`../specs/GOAL.md:1241-1249`).
- Charter PD-2026-08-26-12 D1..D3 (native clients): SDK sync clients are
  native-language implementations of one wire contract; conformance, not
  compilation, is the compatibility guarantee; more languages implement the
  same contract natively later.
- PS-01 sweep (rows PB-2000..PB-2028): V1 rows are session-shaped, V2 has
  the engine seam and no session layer; the engine commit core for the unit
  of work is proven (`docs/parity/ps-01-documents-sweep.md:130-160`).
- The landed engine seam: `src/engine` (Engine, Transaction, CommitOutcome,
  retry dispositions), `src/families` (typed family writes that already
  stage atomically with documents), `src/streaming` (W4-STREAM StreamStore).
- SE-14 TBEP (`crates/terrabase-tbep`, `docs/sync/tbep.md`): the engine
  protocol between the C# host and the engine process; its registry already
  carries a `session_refused` error code and document read/write frames.
- SE-08 host (`host/README.md`, in flight): the C# API host that SDKs and
  MCP face; owns the request executor and topology failover (PB-2022).

## The decision

V2 splits V1's single client-side `IDocumentSession` into a three-tier
contract. The dividing test for each operation class is mechanical, not
tasteful:

1. If the rule must hold **inside one atomic commit** (nothing partially
   staged, nothing partially committed, ordering validated against
   authoritative state), it is **engine-side** — only the engine can
   enforce it, because only the engine owns the transaction.
2. If the rule is about **serving callers over a topology** (retry, node
   selection, streaming transport, per-request events), it is
   **host-side** — SE-08's C# host, over TBEP.
3. If the rule is about **a developer's language-typed objects** (entity
   conventions, conversion, caching policy), it is **SDK-side** — the
   native clients under PD-2026-08-26-12.

### Per-operation-class ownership

| # | Operation class (V1 observable) | Owner | Evidence and rejected alternatives |
|---|---|---|---|
| 1 | Deferred-until-save queue; commit boundary; committed-state clearing (PB-2000/2001) | **Engine** | One `SaveChanges` = one atomic batch is already the engine's transaction: `Transaction` stages writes and `commit` publishes all or nothing (`src/engine/mod.rs:898-1067`; `one_transaction_publishes_all_writes_atomically`, `src/engine/tests.rs:887`). A host- or SDK-side queue would assemble a batch it cannot validate atomically; the engine would still have to enforce atomicity, so the queue would be duplicated. *Rejected:* host-side queue (two validators, partial-commit window across TBEP); SDK-side queue (N implementations, PD-12 D3). |
| 2 | Deferred typed command queue with ordering and save-time validation (GAP-D08 / PB-2013) | **Engine** | V1 appends deferred commands after deletes and puts (`Session/Operations/BatchOperation.cs:37-39`: session commands are deletes then puts, then `AddRange(DeferredCommands)`), and refuses a save mixing a deferred command with a deleted or modified document (`ThrowInvalidDeletedDocumentWithDeferredCommand`, `InMemoryDocumentSessionOperations.cs` delete preparation; `ThrowInvalidModifiedDocumentWithDeferredCommand`, put preparation). That refusal must abort the whole save — enforceable only where the commit is. Family commands already stage atomically with documents in one Engine transaction (`src/families/mod.rs:661-677`). *Rejected:* host-side Defer (validation would race the engine's conflict check; ordering unenforceable cross-process). |
| 3 | Identity map; one-instance-per-document; `IsLoaded`; known-missing; refresh; evict; clear (PB-2005/2010/2011) | **Engine** (authoritative); SDKs may cache projections | The authoritative instance is the one bound to the read path the save conflict-checks against. V1's `DocumentsById` identity map pins the first-loaded instance until evict/save (`Session/DocumentsById.cs`; `IsLoaded` `InMemoryDocumentSessionOperations.cs:457`). Refresh is a fresh engine read replacing tracked state (`Refresh<T>`, `IAdvancedSessionOperations.cs:40`). The engine owns the snapshot the pin is pinned to. *Rejected:* SDK-only identity map (two SDKs could disagree with the engine's conflict answer for the same save). |
| 4 | WhatChanged field-level reporting (GAP-D06 / PB-2012) | **Engine** | Old/new values must be computed against the same immutable view the commit conflict-checks; snapshot immutability is engine-proven (`read_snapshot_remains_immutable_after_a_later_commit`, `src/engine/tests.rs:974`). A client-side diff compares against a possibly-stale local copy. V1's report shape is `DocumentsChanges` with `FieldOldValue`/`FieldNewValue`/`Change`/`FieldName`/`FieldPath`/`FieldFullName` and the eight-value `ChangeType` set (`Session/DocumentsChanges.cs`). *Rejected:* SDK-side diffing (non-authoritative old values; N implementations). |
| 5 | No-tracking reads (PB-2011) | **Engine** session option; SDKs surface it | V1's refusal set is contract, not implementation: `Store` throws on a NoTracking session (`StoreInternal`, `InMemoryDocumentSessionOperations.cs:810-814`), `SaveChanges` throws (`DocumentSession.cs:86-104`), and missing-id registration silently skips (`RegisterMissing`, `:1626-1631`). The refusal must be consistent with where tracking lives (row 3). |
| 6 | Transaction-mode selection (PB-2016 mode contract) | **Engine** (enum + fail-closed refusal); cluster execution is SH-05/#51 | V1's `TransactionMode {SingleNode, ClusterWide}` is a session option (`Session/SessionOptions.cs:6-19`) validated at save (`ValidateClusterTransaction`, `InMemoryDocumentSessionOperations.cs:1007-1035`). V2 has no cluster transaction (`docs/parity/ps-01-documents-sweep.md:420-440`), so the mode is selected at open and `ClusterWide` fails closed with a typed error rather than silently downgrading to single-node. *Rejected:* host-side mode switch (would imply a cluster commit protocol the host cannot run). |
| 7 | Lazy/deferred reads; pending set; batched execution (GAP-D14 / PB-2019) | **Engine** | V1 batches: evaluating any lazy value executes all pending lazy operations in one combined request (`AddLazyOperation`, `Session/DocumentSession.Lazy.cs:128-141`; `ExecuteAllPendingLazyOperations`, `DocumentSession.cs:191`). The batch combines engine reads; the engine owns the read path, so batching at the engine session is one implementation, one conformance target. GAP-D14 additionally requires pending lazy reads to execute at save. *Rejected:* SDK-only lazy batching (each SDK re-implements combination rules; engine cannot report batch shape). |
| 8 | Session stream surface; LoadStartingWith (GAP-D15 / PB-2023) | **Engine** thin surface over W4-STREAM | The seam exists: `StreamStore::open`/`next_batch` with `DocumentStreamSelector` (id/prefix/match/exclusion) and exactly-once terminal completion (`src/streaming/mod.rs:84-350`; proven `src/streaming/tests.rs:224-333`). V1's `Stream`/`LoadStartingWith` are thin document-paging surfaces over exactly such a selector (`Session/DocumentSession.Stream.cs:114`; `IAdvancedSessionOperations.Load.cs`). *Rejected:* host-side stream synthesis (would duplicate selector semantics the engine already serves and tested). |
| 9 | Session lifecycle hooks: before-store, before-delete, after-save (GAP-D16 / PB-2027) | **Engine** pre-commit; request/topology events **host-side** | V1 invokes `OnBeforeStore` during save preparation where it can still change or veto the put, and `OnBeforeDelete` before the delete command is built (`PrepareForEntitiesPuts`/`PrepareForEntitiesDeletion`, `InMemoryDocumentSessionOperations.cs`). The GAP-D16 requirement "cannot partially commit" forces the hook to run inside the save, before staging. Request success/failure and topology-update events ride the request executor = host (PB-2022, SE-08). *Rejected:* host-side hooks (a veto arriving after TBEP submit cannot unstage engine writes atomically). |
| 10 | Conversion hooks (entity↔document) | **Accepted-difference** (SDK-side mapping concern) | V1 converts language entities to BlittableJson through session converters; V2's engine seam carries typed `DocumentValue` documents — there is no conversion step inside a session. The PS-01 sweep already records conversion hooks as candidates for accepted-difference under typed documents (`docs/parity/ps-01-documents-sweep.md:655-669`). Each SDK maps its entities to `DocumentValue` under PD-12. |
| 11 | Identity conventions: entity-id extraction | **SDK/host-side** | V1's conventions reflect over C# entities to find an `Id` property (`Conventions`). That is a language-typed concern; the engine seam's documents carry an explicit `DocumentId` (`src/types.rs:147-188`). SDK tickets own extraction conventions per language. |
| 12 | Identity generation for no-explicit-ID stores (GAP-D05 engine half) | **Engine** save-time allocation; HiLo block distribution **GAP-F9** | Uniqueness is a database invariant, so the no-explicit-ID store path allocates from the engine's identity family in the **same transaction** as the save (`identities_read`/`identities_stage`, `src/families/store.rs:670-712`), which makes a crashed save leak no ids. Client-side HiLo generators (V1's model) cannot guarantee this across clients. Cross-client HiLo block distribution stays with GAP-F9 per the ticket. *Rejected:* SDK-side generators (post-crash duplicate risk); host-side allocation (extra tier for an engine-owned invariant). |
| 13 | Request cache / aggressive caching (GAP-D12 / PB-2021) | **SDK-side** (PD-2026-08-26-12); engine supplies freshness primitives only | The ticket itself constrains this: "request-cache/aggressive-caching revalidation is a client/SDK concern under PD-2026-08-26-12". The engine provides the eviction inputs (publication events, `PublicationRegistry`, `src/engine/mod.rs:68-130`; ordered `ChangeEvent` delivery, `src/streaming/mod.rs:741-824`) and the freshness check (GAP-D02's HEAD/conditional-load contract) but owns no cache. *Rejected:* engine-side cache (contradicts PD-12; couples SDK freshness policy to engine state). |
| 14 | Retry semantics at the unit-of-work boundary | **Engine** mapping (frozen); transport retry **host-side** | `CommitError::retry_disposition` is the closed, stable mapping (`src/engine/mod.rs:332-350`); the session surfaces `CommitOutcome` unchanged so unknown outcomes resolve by operation identity (`Engine::resolve_commit`). Node failover and request retry are the host's executor (PB-2022, SE-08). |
| 15 | Bulk-insert stream (PB-2015) | **Host** (public path), engine multi-put core | Already adjudicated by the sweep: the streaming ingestion writer is blocked on #11 (`docs/parity/ps-01-documents-sweep.md:382-395`). Not a GAP-D01 deliverable; the session's save path reuses the same atomic multi-put core. |

### Why an engine-side session does not violate the D1 language split

D1 reserves Rust for the engine core and moves *public API surfaces* to C#.
The session layer decided here is not a public API surface: it is the
database-behavior contract of the unit of work — atomicity, identity,
change-detection, deferred-command validity — which this repository owns
(`AGENTS.md`: "This repository owns database behavior"). The C# host and the
native SDKs surface their own ergonomic APIs over TBEP; the engine-side
session is the single semantic definition they conform to, which is exactly
PD-12's conformance model. No C ABI, no in-process embedding, no host code
is added by this decision.

## Rider dispositions (folded tickets)

| Rider | Disposition | Where |
|---|---|---|
| GAP-D05 identity conventions | **Implemented here (engine half) + folded elsewhere (SDK half)**: save-time no-explicit-ID allocation from the identity family in the same transaction; entity-id convention extraction is SDK-side; HiLo block distribution stays GAP-F9 | `src/session` (`store_generated`), SDK tickets, GAP-F9 |
| GAP-D06 WhatChanged | **Implemented here**: engine-authoritative field-level old/new/change-type reports with V1's change-type vocabulary | `src/session` (`what_changed`) |
| GAP-D08 Defer and batch commands | **Implemented here**: typed deferred family-command queue, deletes→puts→deferred ordering, deferred-vs-delete save-time validation, atomic same-transaction commit | `src/session` (`defer`, `save_changes`) |
| GAP-D12 request cache / aggressive caching | **Folded elsewhere (SDK)** under PD-2026-08-26-12; engine keeps freshness primitives only (publication events, GAP-D02 HEAD/conditional-load) | SDK tickets; GAP-D02 landed the primitives |
| GAP-D14 lazy and deferred reads | **Implemented here**: pending lazy set, one-batch execution on `execute_all_pending_lazy_operations` and at save, batch counter observable | `src/session` (`lazy_load`, `execute_all_pending_lazy_operations`) |
| GAP-D15 session stream surface | **Implemented here**: thin session surface over W4-STREAM (`stream_documents`, `load_starting_with`); backbone delivery stays #5 | `src/session` |
| GAP-D16 session event and conversion hooks | **Split**: pre-commit lifecycle hooks (before-store with value mutation + veto, before-delete veto, after-save observation) implemented here with a cannot-partially-commit contract; conversion hooks **accepted-difference** (typed documents — no session conversion step exists); request/topology events folded to SE-08 | `src/session` (`hooks`), SE-08 |

Accepted differences (V1 → V2, deliberate, none silent):

1. **Conversion hooks do not exist** (typed documents; SDK-side mapping).
2. **ClusterWide mode fails at open**, not at save: no V2 cluster
   transaction exists (SH-05/#51), so accepting one and failing later would
   let a session accumulate work that can never commit.
3. **Numeric `start` paging in `LoadStartingWith`** maps onto the stream
   seam's cursor/batch contract: the session skips `start` items in
   canonical key order rather than seeking by offset.
4. **WhatChanged compares engine `DocumentValue`s**, not language entities:
   the report names engine fields (dotted paths, array indices); SDKs may
   add entity-level diffs locally.
5. **Optimistic concurrency is explicit**: V1's `UseOptimisticConcurrency`
   toggle collapses to V2's `ExpectedVersion` per staged write; the session
   default is V1's default overwrite (`Any`), with `store_new`/checked
   forms for `New`/`Exact`.

## Write-scope proposal (before code, per the ticket)

New files only, plus one pre-existing-file edit:

- `docs/sessions/gapd01-ownership-decision.md` (this record).
- `docs/sessions/session-contract.md` — the V2 session contract documented
  against V1 `IDocumentSession` observable behavior, citing the pinned
  sweep.
- `src/session/mod.rs`, `src/session/tests.rs` — the engine-side session
  and its focused in-crate tests (commit-path behavior; `TenantScope` has
  no public constructor by design, mirroring the GAP-D02 evidence pattern).
- `tests/session/main.rs`, `tests/session/session_public_construction.rs`
  — public-vocabulary tests.
- `docs/parity/gapd01-ws-parity-proposals.md` — WS-PARITY proposals for the
  touched rows (docs only; no `parity/` status changes).
- **Edited:** `src/lib.rs` — module registration and re-exports.
- **Edited:** `src/families/mod.rs` and `src/families/payloads.rs` — one
  crate-internal `rebase_operation` constructor on `FamilyWriteRequest` and
  its write payloads (a field set, no contract change): the family stage
  seam requires a staged write's operation identity to equal the save
  transaction's, and the session owns that identity, so deferred commands
  adopt it at save time. *Amendment, recorded when implementation revealed
  the family-stage operation-identity requirement — this bullet was added
  after the families code, not written before it; everything above it is
  the original before-code proposal.* These are the only pre-existing
  files this ticket touches. GAP-D02's in-flight files
  (`src/engine/mod.rs`, `src/engine/metadata*.rs`, `tests/engine/*`,
  migration mapping) are untouched; the session composes the existing
  public engine seams (`Engine::read_snapshot`, `begin`, `Transaction`,
  `Engine::families`, `Engine::streams`) and needs no engine edit.

Deferred by this record (recorded, not silently dropped): session exposure
over TBEP frames and the C# host session API (SE-08 owns the host facade;
the TBEP registry's `session_refused` error code is already reserved),
MCP exposure of session operations (AI-01 rules apply when it lands), and
the sharded single-bucket/non-transactional multi-bucket save variants
(SH-05/#51).
