# PS-01 documents/transactions parity sweep: PB-2000..PB-2028

Status: row-by-row verification note for Forgejo ticket TerraScaleV2#53
(PS-01), recorded 2026-08-26 against the current `develop` tree at commit
`d8569bccef28e4f3a9b118ab2b586b1d10f4e31a` (working tree also carried
in-flight edits from parallel sweeps — `parity/v1-test-gaps.json`,
`src/streaming/tests.rs`, untracked `docs/sharding/`,
`docs/adr/0011-sharding-model.md`, `docs/parity/ps-02-query-sweep.md` —
none of which this sweep touched). Documentation only: no parity status,
disposition, Proof ID, overlay, `parity/contracts/`, `CONTEXT.md`, or code
changes were made. All rows remain `unresolved` with empty Proof ID lists
(`parity/overlays/PB-2000.json`..`PB-2028.json`); this sweep changes no
row's status.

Authoritative row source: `parity/contracts/documents.json` (29 items,
PB-2000..PB-2028, counted from the contract file) at the pinned V1 source
identity `fa99b9257776dbe625e6a2388c74c9ce916637ee`. Note: the PS-01 brief
said "26 rows"; the contract fragment contains 29 contiguous rows and all
29 are covered here. Primary V2 evidence sources: `src/engine/**` (W3-1
transaction/commit seam), `src/storage/**`, `src/operations.rs`,
`src/types.rs`, with adjacent evidence from `src/families/**` (reserved
family records), `src/streaming/**` (W4-STREAM), `src/replication/**`
(W4-REPLICATION), `src/adapters/**`, and `src/migration/**`. Design
records: `docs/engine/w3-engine.md`, `docs/storage/`,
`docs/streaming/w4-stream.md`, `docs/replication/`, `docs/sharding/`.

## Verification vocabulary

- **implemented**: engine-side behavior matching the row's semantics
  exists with focused-test evidence in this tree.
- **partially-implemented**: a real, tested V2 primitive covers part of
  the row's semantics; the rest is absent. Partial is a fact about the
  tree, never a parity claim.
- **not-implemented**: no engine-side implementation of the row's core
  behavior. Absence is a fact about the tree, not a disposition: rows
  owned by other waves (below) are absent here because their wave has not
  landed.

No row reaches **implemented** on its full contract semantics: the
V1 rows are session-shaped, and V2 has no session layer yet — the engine
seam (`Engine`, `Transaction`, typed requests) is what exists. Engine
sub-cores that are proven with focused tests are recorded inside the row
sections as evidence for later `implemented-with-proof` dispositions once
the owning contract exists.

## Wave ownership cross-reference (checked before flagging gaps)

| Wave | Ticket | Rows owned per the PS-01 brief and in-tree records |
| --- | --- | --- |
| SE host (TBEP, C# host) | #11 (SE-08; `host/README.md`) | PB-2022 (topology failover/retry, request executor), PB-2025 (client error/cancellation/idempotency contract) |
| SE-03 change-event backbone | #5 | PB-2024 (document change notifications; document/prefix observers, connection states, topology-aware reconnect) |
| SH sharding | #47-#51; SH-05 = #51 | PB-2017 (cluster-wide transactions; `docs/sharding/dispositions.md:121` routes cross-shard transactions to SH-05), PB-2016 sharded mode variants |
| Replication algorithm decisions | D-1/D-2/D-3 (PB-5016, PB-5021, PB-5022, PB-5026; `docs/parity/w4-row-mapping.md`) | change-vector representation (PB-2004/2007), conflict policy (PB-2002), multi-member commit (PB-2017) |
| W5 Platform Adapter | Platform dispatch `unimplemented!` (`src/adapters/platform/mod.rs:26-33`) | PB-2025 public path, PB-2028 control-plane separation |

## Summary table

Verdict describes the current tree; disposition proposes WS-PARITY
routing. All rows stay `unresolved`.

| Row | Name | Class | Verdict | Disposition (proposed) |
| --- | --- | --- | --- | --- |
| PB-2000 | document-store-and-session-lifecycle | core | partially-implemented | GAP-D01 + GAP-D04 |
| PB-2001 | session-savechanges-unit-of-work | core | partially-implemented | GAP-D01 (engine commit core proven) |
| PB-2002 | document-conflicts-and-tombstones | legacy | partially-implemented | GAP-D09 (blocked on D-3/PB-5022) |
| PB-2003 | document-identity-generation-and-id-forms | core | partially-implemented | GAP-D05 |
| PB-2004 | document-metadata-and-last-modified | core | partially-implemented | GAP-D02 (blocked on change-vector decision) |
| PB-2005 | load-by-id-and-batch | core | partially-implemented | GAP-D01 + GAP-D04 |
| PB-2006 | exists-and-head | core | not-implemented | GAP-D03 (blocked on GAP-D02) |
| PB-2007 | conditional-load-and-change-vector | core | not-implemented | GAP-D03 (blocked on GAP-D02/PB-5021) |
| PB-2008 | store-put-and-overwrite | core | partially-implemented | GAP-D01 + GAP-D04 (engine put proven) |
| PB-2009 | delete-and-expected-change-vector | core | partially-implemented | GAP-D01 (engine delete proven) |
| PB-2010 | refresh-and-eviction | core | not-implemented | GAP-D01 (session identity map) |
| PB-2011 | session-tracking-and-change-detection | core | not-implemented | GAP-D01 |
| PB-2012 | what-changed | core | not-implemented | GAP-D06 |
| PB-2013 | session-command-defer-and-batching | core | partially-implemented | GAP-D08 |
| PB-2014 | document-patch-operations | core | not-implemented | GAP-D07 (patch-language decision) |
| PB-2015 | bulk-insert | core | not-implemented | GAP-D10 (public path blocked on #11) |
| PB-2016 | single-node-transactions | core | partially-implemented | GAP-D01 (mode contract) + #51 (sharded variants; engine core proven) |
| PB-2017 | cluster-wide-transactions | core | not-implemented | cross-referenced to #51 (SH-05); blocked on D-1/D-2 |
| PB-2018 | compare-exchange | core | partially-implemented | GAP-D13 (cluster integration via #51) |
| PB-2019 | lazy-and-deferred-reads | core | not-implemented | GAP-D14 |
| PB-2020 | document-includes | core | not-implemented | GAP-D11 (coordinate with PS-02 GAP-Q7) |
| PB-2021 | request-cache-and-aggressive-cache | core | not-implemented | GAP-D12 (blocked on #5 and GAP-D03) |
| PB-2022 | topology-failover-and-retry | protocol | not-implemented | cross-referenced to #11 (SE-08 host / SE wave) |
| PB-2023 | document-session-stream-and-projection-boundary | protocol | partially-implemented | GAP-D15 (engine seam done by W4-STREAM) |
| PB-2024 | document-change-notifications | protocol | partially-implemented | cross-referenced to #5 (SE-03 backbone) |
| PB-2025 | document-error-cancellation-and-idempotency | protocol | partially-implemented | cross-referenced to #11 (SE wave) + GAP-D04 |
| PB-2026 | document-revisions | legacy | partially-implemented | GAP-D17 (ownership decision) |
| PB-2027 | document-session-event-and-conversion-hooks | core | not-implemented | GAP-D16 |
| PB-2028 | server-wide-document-maintenance-ownership | admin | not-implemented | GAP-D18 (ownership adjudication, by design) |

Counts: partially-implemented 15, not-implemented 14, implemented 0.
Proposed dispositions: gap-ticket-needed 25, cross-referenced-to #N 4
(PB-2017→#51, PB-2022→#11, PB-2024→#5, PB-2025→#11),
implemented-with-proof 0 at whole-row scope.

## Row sections

### PB-2000 document-store-and-session-lifecycle — partially-implemented

What exists is the durable engine lifecycle, not a session lifecycle:

- `EngineConfig::durable` is the only configuration path; empty and
  non-durable locations are rejected (`src/engine/mod.rs:186-216`), and
  compile-fail doc tests pin the absence of in-memory/ephemeral
  constructors (`src/engine/mod.rs:161-171`, `:450-456`; runtime half
  proven by `production_api_has_no_ephemeral_durable_engine_constructor`,
  `src/engine/tests.rs:1367`).
- `Engine::open` / `Engine::shutdown` with typed open errors including
  `FormatMismatch` (`src/engine/mod.rs:484-506`, `:645-648`); public-path
  reopen and fail-closed proof in `tests/engine/engine_public_construction.rs:29-44`
  and `:46-51`, and `engine_opens_only_configured_durable_storage`
  (`src/engine/tests.rs:114`).
- No session object exists. No `OpenSession`/`OpenAsyncSession`
  equivalent, no database selection (V2 binds a tenant via `TenantScope`,
  which has no public constructor by design — Platform mints it after
  authorization; `src/types.rs:76-94`, noted in
  `tests/engine/engine_public_construction.rs:6-8`), no session disposal
  hooks (PB-2027). The typed document request constructors are explicitly
  `unimplemented!` "later behavior packet" (`src/operations.rs:70-75`
  read, `:117-126` write), so no public document operation can even be
  constructed yet.
- Synchronous only; `Deadline`/`Cancellation` types exist
  (`src/types.rs:191-271`) but no async session form.

Disposition: gap-ticket-needed (GAP-D01 session contract; GAP-D04
document request packet).

### PB-2001 session-savechanges-unit-of-work — partially-implemented

The engine-side unit of work is implemented and proven; the session-level
SaveChanges contract is not defined:

- Writes stay staged until commit: `Transaction` holds `Vec<StagedWrite>`
  (`src/engine/mod.rs:878-889`; `StagedWrite` at
  `src/storage/intent.rs:12-40`), staged by `put` (`src/engine/mod.rs:925-935`)
  and `delete` (`:943-947`), and published only by
  `Transaction::commit(durability)` (`:955-993`).
- Atomicity: one call publishes all writes or none —
  `one_transaction_publishes_all_writes_atomically`
  (`src/engine/tests.rs:887`) and
  `transaction_conflict_returns_rejected_without_partial_writes` (`:847`).
- Failure observability and retry: `CommitOutcome::{Committed, Rejected,
  Unknown}` (`src/engine/mod.rs:353-361`), `UnknownCommit` retains the
  operation identity (`:364-404`), and `Engine::resolve_commit` resolves
  it from the durable operation ledger (`:616-635`); proven by
  `retrying_the_same_operation_id_returns_one_commit` (`:532`),
  `roll_forward_digest_lets_a_same_identity_retry_replay_after_reopen`
  (`:582`), `unknown_commit_resolves_from_the_durable_operation_ledger`
  (`:680`), and `pending_resolution_is_not_reported_as_a_rejection`
  (`:713`).
- Missing: session state clearing after successful save, deferred-command
  ordering rules, SaveChangesAsync with cancellation, per-mode commit
  semantics. The V1 "clear only the committed session state" bullet has
  no V2 counterpart because there is no session state.

Disposition: gap-ticket-needed (GAP-D01). The engine commit core is
implemented-with-proof at the engine seam and should anchor that ticket's
evidence.

### PB-2002 document-conflicts-and-tombstones — partially-implemented

Implemented (write-write conflicts, engine-local):

- Every staged put and delete is conflict-checked against both the
  transaction's snapshot table and the published table, including the
  absent-document delete case (`src/storage/file.rs:256-286`;
  `conflict_between` `:486`, `conflict_for_absent_document` `:501`).
- A conflict is recorded as a durable rejection and surfaced as typed
  `CommitError::Conflict` (`src/storage/file.rs:565-567`;
  `src/engine/mod.rs:319-330`) with retry disposition `NewOperation`
  (`:341-349`). Engine proof: `src/engine/tests.rs:847`, `:1384-1453`.
- Adjacent, not client-visible: the publication history carries exact
  delete facts — collection, id, `version_at_delete`
  (`PublishedWrite::Delete`, `src/storage/mod.rs:184-213`) — and
  replication replays deletes under `ExpectedVersion::Exact(
  version_at_delete)` (`src/replication/member.rs:1036-1056`). Family
  conflict-tombstones exist as reserved records
  (`FamilyKind::ConflictTombstones`, `src/families/mod.rs:73-74`;
  `tombstones_read`/`tombstones_stage`, `src/families/store.rs:1005/:1025`;
  `FamilyError::TombstoneConflict`, `src/families/mod.rs:499-500`; test
  `family_tombstone_blocks_stale_local_resurrection`,
  `src/families/tests.rs:1242`).

Missing: the V1 client-visible contract entirely — conflict metadata,
`DocumentConflictException`-equivalent typed result, GetConflicts
operation, and the deleted-vs-missing distinction on loads (a snapshot
read returns `None` for both, `src/storage/mod.rs:54-60`). The migration
disposition for V1 conflict artifacts is open (the migration record names
tombstones as a category, `src/migration/mod.rs:32-36`, `:62-68`).

Disposition: gap-ticket-needed (GAP-D09), blocked on the replication
conflict policy decision D-3 (PB-5022, `docs/parity/w4-row-mapping.md`).

### PB-2003 document-identity-generation-and-id-forms — partially-implemented

Implemented (identity ranges as a data family):

- `identities_read`/`identities_stage` allocate monotonic identity ranges
  from reserved records (`src/families/store.rs:773-822`), with typed
  `IdentitySeedInvalid` (backwards seeding requires force) and
  `IdentityExhausted` (`src/families/mod.rs:495-498`); proven by
  `identity_allocation_is_monotonic_and_backwards_seeding_requires_force`
  (`src/families/tests.rs:1957`) and
  `identity_allocation_exhaustion_is_typed` (`:3142`).
- String document IDs are caller-chosen validated identifiers
  (`DocumentId`, `src/types.rs:71`, validation `:24-38`).

Missing: the session-side generation contract — `Document::new` requires
an explicit `DocumentId` (`src/types.rs:161-163`), so "store without an
explicit ID" does not exist; no convention-based identity extraction, no
client HiLo generator mapping onto the family identity ranges, and no
ULID-specific support (a ULID is only a string; no format validation or
ULID parity tests exist anywhere in `src/` or `tests/`).

Disposition: gap-ticket-needed (GAP-D05).

### PB-2004 document-metadata-and-last-modified — partially-implemented

- Implemented: `DocumentMetadata` carries exactly the published version
  (`src/types.rs:130-145`), assigned at publication
  (`Document::set_version`, `src/types.rs:185-187`; observable in
  `src/engine/tests.rs:1449-1452`). `Document` reports id, collection,
  value, and metadata (`src/types.rs:147-188`).
- Missing: change vector, last-modified, new-document marker,
  ignore-changes marker, metadata dictionary, and every advanced-session
  accessor (`GetMetadataFor`/`GetLastModifiedFor`/`GetChangeVectorFor`)
  — no session exists (PB-2000).

Disposition: gap-ticket-needed (GAP-D02). The change-vector representation
is an open replication decision (PB-5021, W4-REPLICATION) and blocks the
mapping.

### PB-2005 load-by-id-and-batch — partially-implemented

- Implemented: single-ID snapshot reads — `Transaction::read`
  (`src/engine/mod.rs:898-917`) over `StoragePort::read_document_at`
  (`src/storage/mod.rs:54-60`; file implementation
  `src/storage/file.rs:714`), returning `None` for a missing document.
  Snapshot immutability under later commits is proven
  (`read_snapshot_remains_immutable_after_a_later_commit`,
  `src/engine/tests.rs:974`), tenant isolation
  (`snapshot_documents_returns_only_the_snapshot_tenants_documents`,
  `:1514`) and foreign-seam rejection (`:1589`).
- Missing: multi-ID batch load with dictionary result shape, session
  identity-map reuse, known-missing tracking, and the async/cancellation
  form (the typed `DocumentReadRequest` constructor is `unimplemented!`,
  `src/operations.rs:70-75`).

Disposition: gap-ticket-needed (GAP-D01 identity-map semantics; GAP-D04
typed read path).

### PB-2006 exists-and-head — not-implemented

No existence or HEAD operation exists. Existence can only be inferred by
materializing the full document through `read_document_at`
(`src/storage/mod.rs:54-60`); there is no change vector to return, no
conditional expected-vector head check, and no deleted/conflicted
distinction (see PB-2002). Nothing in `src/engine/` or `src/operations.rs`
models an `Exists`/head shape.

Disposition: gap-ticket-needed (GAP-D03), blocked on GAP-D02 (change
vector) for the HEAD half.

### PB-2007 conditional-load-and-change-vector — not-implemented

The read path has no conditional form: no API compares a supplied change
vector on load and returns entity+vector, null-when-unchanged, or an
invalid-input failure. The version primitive that a V2 mapping would use
exists and is enforced on writes — `ExpectedVersion::{Any, New, Exact}`
(`src/types.rs:381-390`), checked at commit against snapshot and published
state (`src/storage/file.rs:264-286`), proven by
`expected_version_any_new_and_exact_are_enforced`
(`src/engine/tests.rs:1384-1453`) — but that is write-path concurrency,
not conditional load. No lazy form exists (PB-2019).

Disposition: gap-ticket-needed (GAP-D03), blocked on the change-vector
representation (PB-5021) and, for its cache role, on PB-2021.

### PB-2008 store-put-and-overwrite — partially-implemented

- Implemented (engine seam): `Transaction::put(document, expected)` stages
  a put with structural-limit validation (`src/engine/mod.rs:925-935`;
  `oversized_document_is_rejected_as_invalid`, `src/engine/tests.rs:1456`),
  the explicit-ID overwrite semantics are the `Any`/`New`/`Exact` expected
  versions (proven `:1384-1453`), and publication is atomic with a typed
  receipt (`:955-993`).
- Missing: the session Store overloads — queued puts bound to entities,
  the generated-ID form (PB-2003), the async-with-cancellation form, and
  any public write path (`DocumentWriteRequest::try_new` is
  `unimplemented!`, `src/operations.rs:117-126`).

Disposition: gap-ticket-needed (GAP-D01 + GAP-D04); the engine put
primitive is implemented-with-proof at the engine seam.

### PB-2009 delete-and-expected-change-vector — partially-implemented

- Implemented (engine seam): `Transaction::delete(id, expected)`
  (`src/engine/mod.rs:943-947`) resolves the collection from the snapshot,
  stages a delete that is conflict-checked exactly like puts
  (`src/storage/file.rs:269-281`) and published atomically; the delete
  digest is snapshot-independent
  (`delete_digest_is_independent_of_snapshot_state`,
  `src/engine/tests.rs:482`). Replication replays deletes under
  `ExpectedVersion::Exact(version_at_delete)`
  (`src/replication/member.rs:1036-1056`).
- Missing: session-level deferred delete (delete sent on save), the
  entity-vs-ID overload distinction, the changed-entity guard, and
  before-delete listener behavior (PB-2027).

Disposition: gap-ticket-needed (GAP-D01); the engine delete primitive is
implemented-with-proof at the engine seam.

### PB-2010 refresh-and-eviction — not-implemented

There is no tracked-entity state to refresh or evict: no identity map, no
`Evict`/`Clear`/`IsLoaded`, no pending per-entity state. The engine-side
refresh equivalent is taking a new immutable snapshot
(`Engine::read_snapshot`, `src/engine/mod.rs:515-535`), which is a fresh
read view, not a tracked-state replacement. Session options do not exist.

Disposition: gap-ticket-needed (GAP-D01).

### PB-2011 session-tracking-and-change-detection — not-implemented

Transactions stage explicit writes; nothing detects modifications. There
is no identity map, no one-instance-per-document guarantee at a session
layer, no `HasChanges`/`HasChanged`/`GetTrackedEntities`/
`IgnoreChangesFor`, and no no-tracking mode. (Engine-side tracked
entities do not exist, so the no-tracking incompatibility rules are
vacuously satisfied and unproven.)

Disposition: gap-ticket-needed (GAP-D01).

### PB-2012 what-changed — not-implemented

No field-level change reporting exists anywhere in `src/`. The only
comparison artifact is the opaque write-set digest that binds an
operation identity to its write set (`write_set_digest`,
`src/storage/mod.rs:259-260`), which is deliberately not invertible and
not a report. Array add/remove diffing, old/new values, and idempotent
re-inspection have no counterpart.

Disposition: gap-ticket-needed (GAP-D06).

### PB-2013 session-command-defer-and-batching — partially-implemented

- Implemented: typed non-document commands already batch with document
  writes in one atomic transaction — `FamilyStore::stage` stages a family
  write into the caller's `Transaction` after validating operation
  identity, tenant, and engine binding
  (`src/families/mod.rs:640-655` with the error contract at `:622-639`;
  family tests stage and commit through the engine transaction,
  `src/families/tests.rs:111-131`). Document puts/deletes in the same
  transaction commit atomically with them
  (`src/engine/tests.rs:887`).
- Missing: the session `Defer` queue, ordering guarantees of deferred
  commands relative to puts/deletes at a client contract level, the
  no-reply batch flag, and save-time command validation. Private
  control-plane commands are out of scope by contract and by construction
  (the closed `PublicOperation` registry carries no such variant,
  `src/operations.rs:283-316`).

Disposition: gap-ticket-needed (GAP-D08).

### PB-2014 document-patch-operations — not-implemented

There is no path-addressed patch. Documents are replaced wholesale by
`Transaction::put`; no typed patch, no JSON-patch form, no add-or-patch,
no add-or-increment on document fields, no patch-on-missing behavior, no
patch error taxonomy. The nearest existing primitive is the counters
family's signed-delta increment for counter values
(`CounterOperation::Increment`, `src/families/payloads.rs:456-463`),
which is a family payload, not a document field patch.

Disposition: gap-ticket-needed (GAP-D07). A patch-language decision is
required first: V1's JavaScript patch cannot carry over (charter
PD-2026-08-26-08 bans arbitrary code execution; typed expression paths
only).

### PB-2015 bulk-insert — not-implemented

No bulk-insert path exists. Multi-put transactions are the closest engine
primitive (`one_transaction_publishes_all_writes_atomically`,
`src/engine/tests.rs:887`) and bounded document windows exist on the read
side (`Engine::snapshot_documents_from`, `src/engine/mod.rs:692-736`),
but there is no streaming ingestion writer, no abort/cancellation
protocol for a partially accepted stream, no per-document protocol
validation (e.g. the V1 rejected-id-suffix rules), and no unavailable-node
failure surface. The host that would carry the public bulk-insert stream
is a README stub tracked by #11 (SE-08, `host/README.md`).

Disposition: gap-ticket-needed (GAP-D10); the public streaming path is
blocked on #11 (TBEP host).

### PB-2016 single-node-transactions — partially-implemented

- Implemented and proven (single-member core): begin validates engine
  identity, tenant scope, and reserves the operation identity
  (`src/engine/mod.rs:552-582`); commit publishes the whole write set
  atomically on one member (`:955-993`; test `src/engine/tests.rs:887`);
  isolation is snapshot-based — the snapshot table vs published table
  conflict check (`src/storage/file.rs:256-286`) plus snapshot
  immutability (`:974`); rejected commits never partially apply
  (`:847`); distinct local durability points exist
  (`LocalDurability::{Buffered, Written, Synced}`, `src/types.rs:370-379`;
  `buffered_written_and_synced_have_distinct_loss_windows`,
  `src/engine/tests.rs:913`).
- Missing: a `TransactionMode` contract (SingleNode vs ClusterWide
  selection and its observability — V1's is a session option), and the
  sharded single-bucket / non-transactional multi-bucket variants, which
  are sharding-wave work: `docs/sharding/dispositions.md:121` routes
  cross-shard transaction behavior to SH-05 (#51).

Disposition: gap-ticket-needed (GAP-D01 carries the mode-selection
contract) with the sharded variants cross-referenced to #51. The
single-node atomicity core is implemented-with-proof at the engine seam.

### PB-2017 cluster-wide-transactions — not-implemented; wave-owned

No cluster-wide transaction protocol exists: no multi-member
SaveChanges, no leader/index wait ordering, no cluster-mode restrictions
for counters/attachments/time-series, no transition-back-to-SingleNode
semantics. What exists are the replication facts such a protocol would
build on: `AcknowledgementLevel`/`AcknowledgementReport` and `Convergence`
(`src/replication/mod.rs:196-346`), `TopologyView` with ownership epochs
(`src/replication/mod.rs:147`; builder `src/replication/topology.rs:32-253`),
idempotent member replay and catch-up (`src/replication/member.rs`;
tests `duplicate_delivery_applies_each_operation_once`
`src/replication/tests.rs:444`, `member_rejoin_checks_identity_and_catches_up_without_duplicates`
`:488`, `acknowledgement_report_names_the_regions_and_members_that_contributed`
`:1092`, `two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility`
`:1301`). Cluster-wide transactions across shards are explicitly SH-05
scope (`docs/sharding/dispositions.md:121`), and the underlying
commit/apply algorithm is decision D-1/D-2 territory (PB-5016, PB-5042 in
`docs/parity/w4-row-mapping.md`).

Disposition: cross-referenced-to #51 (SH-05). The single-member
transaction core that local commits rest on is proven (PB-2016 evidence).

### PB-2018 compare-exchange — partially-implemented

Implemented as a data family (single-key, index-guarded):

- Read returns value, index, and expiry (`compare_exchange_read`,
  `src/families/store.rs:705-725`; result shape
  `CompareExchangeValueResult`); requests carry deadline/cancellation and
  writes carry `ExpectedVersion`/`OperationId` in the family request
  registry (`src/families/mod.rs:152-153`, `:273-274`).
- Writes stage `PutIfIndex`/`PutIfMissing`/`DeleteIfIndex` conditions,
  incrementing the index on success and failing
  `FamilyError::CompareExchangeIndexStale` on a stale index or a live
  value (`compare_exchange_stage`, `src/families/store.rs:727-778`;
  error `src/families/mod.rs:493-494`). Proven by
  `compare_exchange_conditions_require_the_current_index`
  (`src/families/tests.rs:1761`) and
  `compare_exchange_expiry_hides_expired_values` (`:1903`).

Missing: integration with a cluster transaction (a stale index must fail
the encompassing transaction — today only the family write fails inside
the local transaction, and no cluster transaction exists, PB-2017),
get-many, prefix paging, lazy and async forms, and key-ordered result
shapes.

Disposition: gap-ticket-needed (GAP-D13), with the cluster-transaction
integration blocked on #51 and the acknowledgement level on D-2.

### PB-2019 lazy-and-deferred-reads — not-implemented

No lazy or deferred read exists. The protocol adapter forwards exactly
one closed `PublicOperation` per call (`ProtocolAdapter::call`,
`src/adapters/protocol/mod.rs:530-547`); there is no pending-lazy set, no
`ExecuteAllPendingLazyOperations` equivalent, no request combination into
batched loads, and no response-timing report. The engine's batch-shaped
read (`documents_at`, `src/storage/mod.rs:62-65`) is a full-tenant scan
for internal consumers, not a lazy-load batching contract.

Disposition: gap-ticket-needed (GAP-D14).

### PB-2020 document-includes — not-implemented

No include concept exists on loads or queries: no include path language
(scalar, collection, nested, dictionary key/value), no include builder,
no same-request secondary materialization into a session, and no
missing-include behavior. The only filter-by-relationship primitive is
`ChangeFilter` for subscriptions (kinds and collections only,
`src/streaming/mod.rs:832-881`), which is not a load-time include. The
query-side include obligation is tracked by PS-02 as GAP-Q7 (PB-4016);
the two must be designed together.

Disposition: gap-ticket-needed (GAP-D11).

### PB-2021 request-cache-and-aggressive-cache — not-implemented

Nothing cache-shaped exists in the engine tree: a case-insensitive search
for "cache" over `src/` returns zero matches. There is no response cache,
no NoCaching/aggressive-caching scope, no cache duration policy, no
change-driven eviction, and no conditional revalidation (which would need
PB-2007). The inputs an eviction path would consume do exist —
publication events (`PublicationRegistry`, `src/engine/mod.rs:68-130`)
and ordered `ChangeEvent` delivery (`src/streaming/mod.rs:741-824`) — but
no consumer builds a cache on them.

Disposition: gap-ticket-needed (GAP-D12), blocked on the SE-03 backbone
(#5) for change-driven eviction and on GAP-D03 for conditional
validation. No stale-read claim is made or implied.

### PB-2022 topology-failover-and-retry — not-implemented; wave-owned

The owning seam (client request executor over a topology) does not exist:
`host/` and `cli/` are README stubs tracked by #11 (SE-08) and #12
(SE-13). In-tree facts that the future executor will consume:

- `TopologyView` with regions, members, failure domains, per-range
  ownership epochs, and servability validation
  (`src/replication/mod.rs:141-160`; `src/replication/topology.rs:32-253`).
- Engine retry policy as a closed, stable mapping — validation/conflict
  want a new identity, identity reuse never retries, closure retries the
  same identity, storage failure waits for recovery
  (`CommitError::retry_disposition`, `src/engine/mod.rs:332-350`;
  `RetryDisposition`, `src/types.rs:392-403`).
- Member failover/rejoin lanes: `partitioned_link_blocks_catch_up_until_healed`
  (`src/replication/tests.rs:862`), `member_rejoin_checks_identity_and_catches_up_without_duplicates`
  (`:488`), region-partition convergence
  (`src/replication/lane_tests.rs:426`).

Missing: node selection per request, session node affinity, topology
updates from server responses, request timeouts, and the
all-nodes-down typed failure. No duplicate-write risk is introduced today
because there is no retrying executor.

Disposition: cross-referenced-to #11 (SE-08 host / SE wave), with the
engine retry facts above recorded as evidence.

### PB-2023 document-session-stream-and-projection-boundary — partially-implemented

The streaming seam is implemented; the document-session surface is not:

- Implemented: `StreamStore` with one bounded batch contract — at most
  the caller's item limit, cursor resume, exactly-once terminal
  completion (`src/streaming/mod.rs:78-256`); document streams in
  canonical key order with id/prefix/match/exclusion selectors
  (`DocumentStreamSelector`, `src/streaming/mod.rs:304-340`; bound
  `MAX_EXCLUDED_IDS` `:58-59`); typed errors for missing documents and
  invalid selectors. Proven by
  `document_stream_preserves_key_order_and_result_shape`
  (`src/streaming/tests.rs:224`),
  `document_stream_selectors_support_id_prefix_match_and_exclusion`
  (`:264`),
  `document_stream_missing_documents_and_invalid_selectors_return_typed_errors`
  (`:333`), and prompt cancellation (`:920`).
- The projection boundary stays separate, as the row requires: query
  streams are their own source with generation reporting
  (`StreamSource::Query`, `src/streaming/mod.rs:293-296`; test `:417`).
- Missing: the V1 session `Stream`/`StreamAsync` methods with their
  stream-result metadata and the session `LoadStartingWith` form (the
  prefix/match selectors live at the stream seam instead), and
  start-after paging at a session API.

Disposition: gap-ticket-needed (GAP-D15, thin session surface over the
existing seam); engine seam evidence owned by W4-STREAM (PB-6007, PB-6008
in `docs/parity/w4-row-mapping.md`), backbone delivery by #5.

### PB-2024 document-change-notifications — partially-implemented; wave-owned

The engine backbone exists and is proven; the V1 observer surface does
not:

- Implemented: `ChangeEvent` carrying kind, tenant scope, collection,
  document id, operation id, and storage sequence, observable only after
  publication (`src/streaming/mod.rs:741-807`); the closed kind set
  (`:809-824`); `ChangeFilter` by kinds and collections (`:826-881`);
  subscriptions with durable checkpoints, acknowledgement-gated advance,
  resend after reconnect, and explicit terminal status
  (`SubscriptionSpec`/`SubscriptionBatch`/`SubscriptionCheckpoint`/`
  SubscriptionStatus`, `:883-1066`). Proven by
  `changes_subscription_observes_only_committed_publication_events`
  (`src/streaming/tests.rs:979`),
  `changes_events_carry_operation_identity_and_storage_sequence_in_order`
  (`:1025`),
  `subscription_checkpoint_does_not_advance_before_acknowledgement`
  (`:1153`),
  `unacknowledged_subscription_items_are_resent_after_reconnect` (`:1218`),
  `subscription_reconnect_preserves_the_durable_checkpoint` (`:1318`),
  `subscription_close_reports_an_explicit_terminal_status` (`:1369`).
- Missing: per-document and prefix observers (`ChangeFilter` has neither
  dimension), connection-state transitions for database-missing/fault/
  disposed (the `SubscriptionStatus` set is delivery state, not the V1
  connection state machine), node-specific subscriptions, and
  topology-aware reconnect (single-engine today).

Disposition: cross-referenced-to #5 (SE-03 backbone), which owns the
journal-backed public surface; the filter-extension work should ride that
ticket.

### PB-2025 document-error-cancellation-and-idempotency — partially-implemented; wave-owned

Implemented (engine-side primitives):

- Typed, non-collapsed error taxonomy: `EngineError` with distinct
  Closed/Conflict/InvalidDocument/Deadline/Cancelled/StorageFailure
  (`src/engine/mod.rs:234-260`), `CommitError` and `CommitOutcome::
  Unknown` with reasons (`:318-404`), `FamilyError` with per-family cases
  (`src/families/mod.rs:450-503`), plus `StreamError`, `QueryError`, and
  `ProtocolError`.
- Idempotency by operation identity with durable resolution of unknown
  outcomes — the direct answer to "an unknown write outcome must not
  become a blind retry": `Engine::resolve_commit` (`src/engine/mod.rs:
  610-635`), retry dispositions (`:332-350`), proofs at
  `src/engine/tests.rs:680`, `:713`, `:1308`, and replay-once under
  duplicate delivery (`src/replication/tests.rs:444`).
- Cancellation/deadline enforced at the family, streaming, and query
  seams before work runs (`src/families/mod.rs:608-613`;
  `src/streaming/tests.rs:920`; `src/query/execute.rs:49-50`), and the
  protocol adapter preserves operation identity across deadline/cancel
  refusals (`src/adapters/protocol/tests.rs:510`).

Missing: the document path itself — `DocumentReadRequest::try_new` and
`DocumentWriteRequest::try_new` are `unimplemented!`
(`src/operations.rs:74`, `:125`) and Platform dispatch is `unimplemented!`
pending W5 (`src/adapters/platform/mod.rs:26-33`), so no client error
contract for document operations can exist yet; bulk-insert abort errors
depend on PB-2015; the SDK-facing vocabulary is SE-wave work.

Disposition: cross-referenced-to #11 (SE wave host) plus gap-ticket GAP-D04
for the engine-side document validation packet.

### PB-2026 document-revisions — partially-implemented

Implemented as a data family:

- `RevisionRead`/`RevisionWrite` with paging, policy (minimum/maximum),
  and commands including revert (`src/families/payloads.rs:1040-1330`);
  `revisions_read`/`revisions_stage` (`src/families/store.rs:637/:660`);
  reserved `Revisions` records (`src/families/mod.rs:61-62`,
  `src/families/state.rs:539`). Proven by
  `revision_page_and_revert_keep_snapshot_identity`
  (`src/families/tests.rs:857`),
  `revision_policy_trims_the_oldest_revisions` (`:2070`),
  `revision_revert_rejects_unknown_references` (`:2155`).
- Migration carries revisions as a first-class category with loss-matrix
  disclosure (`src/migration/mod.rs:32-36`, `:62-68`).

Missing: session revision access, revision includes (there is no change
vector to include by), conflict status visibility in revisions, and
revision behavior in cluster transactions (no cluster transactions). The
row's own ownership question — engine document contract vs data-family
owner — is open; today revisions live entirely in the family seam.

Disposition: gap-ticket-needed (GAP-D17, ownership adjudication plus the
session mapping); replication-side revision obligations are PB-5030
(W4-REPLICATION).

### PB-2027 document-session-event-and-conversion-hooks — not-implemented

No session events or conversion hooks exist: no before/after store,
delete, query, or save events, no conversion-to-document/entity hooks, no
request success/failure or topology-update events, no session-disposing
event. The only observable lifecycle point is the engine's publication
sink registry — commit-time, synchronous, bounded at 64 subscribers
(`PublicationRegistry` and `MAX_PUBLICATION_SINKS`,
`src/engine/mod.rs:41-159`; `Engine::subscribe_publications` `:842-844`;
emission proven at `src/engine/tests.rs:1043`, `:1073`, `:1107`) — which
is a post-commit notification seam, not the V1 hook set. Conversion hooks
have no analog because typed documents are not serialized through a
session.

Disposition: gap-ticket-needed (GAP-D16, depends on GAP-D01).

### PB-2028 server-wide-document-maintenance-ownership — not-implemented (by design; ownership unresolved)

The row itself states no implementation is implied until ownership is
recorded, and the tree honors that: there is no maintenance executor, no
statistics surface, no server-wide operation set, and no admin variant
anywhere. The seams stay clean — the public registry is closed with only
document read/write, query, and family operations
(`PublicOperation`, `src/operations.rs:283-316`), the protocol adapter is
documented and tested to refuse generic routes, bodies, and private
control-plane operations (`src/adapters/protocol/mod.rs:1-16`; tests
`src/adapters/protocol/tests.rs:387`, `:491`), and Platform dispatch is
`unimplemented!` pending W5 (`src/adapters/platform/mod.rs:26-33`).

Disposition: gap-ticket-needed (GAP-D18) — an ownership adjudication
ticket, not an engine implementation ticket: split V1's maintenance
inventory into engine-owned database statistics/document maintenance
versus Platform control-plane operations per charter PD-2026-08-26-11 and
the workspace invariants.

## Proposed gap tickets

| ID | Title | One-line scope | Suggested blockers |
| --- | --- | --- | --- |
| GAP-D01 | V2 document session and unit-of-work contract | Define and implement the session-shaped layer over the engine seam: deferred operations until save, commit boundary and committed-state clearing, retry semantics, identity map, refresh/evict/clear/IsLoaded, no-tracking mode, transaction-mode selection | Session-layer ownership decision (engine vs host/SDK via #11); none technical — the engine commit core (PB-2001/2016 evidence) already exists |
| GAP-D02 | Document metadata and change-vector contract | Map V1 change vector / last-modified / new-marker / ignore-changes / metadata dictionary onto V2's version+sequence facts | Change-vector representation decision (PB-5021, W4-REPLICATION D-2) |
| GAP-D03 | Exists/HEAD and conditional-load reads | Typed existence and conditional-load operations (entity+vector when changed, V1 null/default otherwise, invalid-input failure) | GAP-D02 |
| GAP-D04 | Document request validation packet | Implement `DocumentReadRequest`/`DocumentWriteRequest::try_new` and wire DocumentRead/Write through Platform→Engine dispatch | W5 Platform authorization (`src/adapters/platform/mod.rs:26-33` is `unimplemented!`) |
| GAP-D05 | Identity generation conventions | No-explicit-ID store path, convention extraction, HiLo↔family-identity-range mapping, ULID string-ID parity tests | GAP-D01; SDK owner decision |
| GAP-D06 | WhatChanged field-level reporting | Field/property old-new-change-type reports per entity, array add/remove, idempotent re-inspection | GAP-D01 (tracked entities); typed comparison rules |
| GAP-D07 | Typed patch operations | Path-addressed patch, add-or-patch, add-or-increment, missing-document behavior, typed errors — typed paths only, no script execution | Patch-language decision (charter PD-2026-08-26-08: no arbitrary code execution) |
| GAP-D08 | Session Defer and batch commands | Deferred typed command queue with ordering relative to puts/deletes and save-time validation (family staging already commits atomically with documents) | GAP-D01 |
| GAP-D09 | Conflict and tombstone client visibility | Client-visible conflict metadata and typed conflict results, deleted-vs-missing read distinction, V1 conflict-artifact migration disposition | Replication conflict policy D-3 (PB-5022); logical migration mapping (BK-03) |
| GAP-D10 | Bulk-insert ingestion stream | Streaming multi-document writer with generated/explicit IDs, abort/cancellation without false success, per-document validation | #11 (SE-08 host) for the public path; engine multi-put core exists |
| GAP-D11 | Document includes | Same-request secondary materialization for scalar/collection/nested/dictionary include paths with V1 missing-include behavior | GAP-D01; coordinate with PS-02 GAP-Q7 (PB-4016) |
| GAP-D12 | Request cache and aggressive caching | Freshness-validating reads, aggressive-cache duration scope, change-driven eviction, database/tenant cache scoping | #5 (SE-03 backbone) for eviction; GAP-D03 for conditional validation |
| GAP-D13 | Compare-exchange completion | Get-many, prefix paging, lazy/async forms, key-ordered results, and integration of stale-index failure into the encompassing transaction | #51 (cluster transactions); D-2 acknowledgement level |
| GAP-D14 | Lazy and deferred reads | Lazy operation wrappers, pending-set eager execution, request combination into batched loads | GAP-D01; GAP-D08 (batch shape) |
| GAP-D15 | Document-session stream surface | Session Stream/StreamAsync with stream-result metadata and LoadStartingWith paging over the existing W4-STREAM seam | #5 (backbone delivery); GAP-D01 |
| GAP-D16 | Session event and conversion hooks | V1-observable lifecycle and conversion hook points with failure/ordering contract that cannot partially commit | GAP-D01 |
| GAP-D17 | Revisions ownership and session access | Adjudicate document-contract vs data-family ownership for revisions; add session revision reads and revision includes if owned | Parent ownership decision; PB-5030 (replication revisions) |
| GAP-D18 | Maintenance ownership adjudication | Split V1's maintenance/operations inventory into engine-owned statistics vs Platform control-plane, recording the authorization boundary | Parent decision; charter PD-2026-08-26-11; W5 Platform Adapter |

Wave-owned rows needing no new engine ticket now: PB-2017 (#51, plus
D-1/D-2), PB-2022 (#11), PB-2024 (#5), PB-2025 (#11 plus GAP-D04), and
PB-2016's sharded variants (#51).

## Checks

None of the repository check commands (`cargo fmt`/`check`/`clippy`/
`test`/`doc`, `scripts/validate_parity.py`, citation audit) were run for
this sweep: it is a documentation-only change against a working tree that
carries parallel in-flight edits from other sweeps, so any check result
would describe their tree state, not this note. Verification was by
direct source inspection; every citation above was resolved against the
file contents at commit `d8569bccef28e4f3a9b118ab2b586b1d10f4e31a` with
unstaged parallel edits present but unused as evidence. The one tool
executed was a row count over the contract (`29 items`), matching the
contiguous PB-2000..PB-2028 range and contradicting the brief's "26
rows" — the contract is authoritative and all 29 rows are covered.

This note claims no V1/V2 parity, no qualification, and no change to any
row's `unresolved` status or Proof ID list.
