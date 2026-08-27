# SUPERSEDED IN PART (counter records): see counter-merge-encoding.md (accepted 2026-08-26) for the counter-record encoding; the statements below stand as W3-F history.

# W3-F families record: typed Data Family seam

Status: implementation record for the W3-F data-family packet, 2026-08-24.
Worktree branch `fleet/w3-families`, based on `6a178dc` (W3-1 storage and
engine accepted at `dd1069c`, plus the integrator wiring of
`Engine::families` to `FamilyStore::from_engine`). This record describes
focused-test evidence only. It is not a parity qualification: all 46 family
parity rows remain `unresolved` with empty Proof ID lists, and this packet
adds no Proof ID to any row.

Repair history pinned on this record: loop a1 landed at `36ded73`
(implementation) and `bef8278`/`aefbc2a` (this record); loop a2 — the
inspector and warden gate findings — lands on top of `aefbc2a` and covers
same-document attachment Copy/Move staging, the reserved-collection
collision claim, the `NumberRange` float bound, the series-name record
separator, identity exhaustion, retention-horizon symmetry, and the
revision-read asymmetry note.

## Scope and ownership

Owned paths only: `src/families/**`, `tests/families/**`, and this document.
No file outside that set was edited. `src/engine/**`, `src/storage/**`,
`src/types.rs`, `src/operations.rs`, `src/lib.rs`, `tests/public_exports.rs`,
and `parity/**` are untouched; `tests/public_exports.rs` still compiles
unchanged because it names types only.

## Design as implemented

### Typed registry

The public interface is the closed registry from the W3 core contract:
`FamilyKind` (ten V1-named families, unchanged by PD-2026-08-24-01 D4),
`FamilyReadContext`, `FamilyReadRequest`/`FamilyWriteRequest`/
`FamilyReadResult` with their ten payload pairs and named constructors, and
`FamilyStore::read`/`stage` with the frozen signatures. The `PhantomData`
placeholders were replaced by real typed fields:

- Every read payload carries its typed fields plus a deadline and
  cancellation; every write payload additionally carries one validated
  `TenantScope` and the operation identity, and document-bound writes carry an
  `ExpectedVersion`.
- Every payload is built through `try_new`, which rejects structurally
  invalid fields with `RequestValidationError` (`InvalidFamilyPayload`,
  `InvalidIdentifier` indirectly through the shared identifier types,
  `InvalidRange`, `InvalidDeadline`, `InvalidCancellation`), and is observed
  only through read-only accessors. There is no public unchecked field
  constructor, and no payload type exposes a public constructor at all beyond
  `try_new`.
- Rules that need stored family state (series arity, retention ranges,
  compare-exchange indexes, revision references, lifecycle values, malformed
  policy definitions, schema violations, tombstone staleness, identity
  seeding) are validated by the store and surface as distinct `FamilyError`
  cases. `FamilyError` keeps every declared variant's meaning and adds the
  family-specific cases the contract requires, plus `DeadlineExceeded`,
  `StorageFailure`, `DocumentMissing`, and `ReservedStateInvalid`.

### Storage mapping

Family state is durable document state, not a new storage format. One
reserved collection `terrabase.families` holds one record document per family
scope, addressed by reserved identifiers of the form
`family:<kind>/<owner...>`:

| Family | Record identifier | Encoded value |
| --- | --- | --- |
| Attachments | `family:attachments/<document>` | name → content type, size, digest |
| Counters | `family:counters/<document>` | name → signed value |
| Time series | `family:time-series/<document>/<name>` | arity, ordered entries, policy |
| Revisions | `family:revisions/<document>` | next revision, entries, policy |
| Compare exchange | `family:compare-exchange/<key>` | index, value, expiry, deleted flag |
| Identities | `family:identities/<name>` | next value |
| Expiration/refresh | `family:lifecycle/<document>` | expiry and refresh targets |
| Archival | `family:archival/<document>` | archived flag, time, delay policy |
| Schema validation | `family:validation/<collection>` | closed rule list |
| Tombstones | `family:tombstones/<document>` | family key → deletion version, time |

The Engine already keys documents by tenant and publishes one marker per
commit, so tenant isolation, atomicity, and durability are inherited rather
than reimplemented. Family records are structured `DocumentValue`s; families
never inspect or emit pages, frames, codec buffers, or raw bytes.

Record identifiers join variable segments with `/`, so exactly one segment
may be a free-form family-local name that shares an alphabet with
`DocumentId` (which accepts `/`): the time-series name. Series names
therefore reject `/` at payload construction — (document `a`, series `b/c`)
and (document `a/b`, series `c`) would otherwise address the same record.
Rejecting the separator in the one ambiguous segment, rather than escaping
every segment in the record codec, keeps the identifier readable and the
rule local to the two time-series payloads.

### Reads

`FamilyStore::read` validates the context (tenant equals snapshot tenant,
scope Engine equals snapshot Engine), the deadline, and the cancellation
before any family data access, then begins one never-committed read
transaction with the caller's scope and snapshot and a freshly minted
internal operation identity (`family-read-<pid>-<counter>`). All document
reads observe that immutable snapshot. A snapshot from another Engine
rejects with `SnapshotEngineMismatch` from `Engine::begin` before any record
read.

### Staged writes

`FamilyStore::stage` checks, in order: the write's operation identity against
the transaction's (`TransactionIdentityMismatch`), deadline and cancellation,
the transaction's Engine and tenant binding, and then every family rule. Each
family stage computes the full new record value in memory and stages at most
one document write per touched record, so a rejected request leaves the
caller's transaction exactly as it was and an abort hides every staged family
change. A same-document attachment Copy or Move is one such single-record
case: the source mutation and the target write merge into one staged write,
because the Engine rejects a write set that stages the same document
identifier twice; a cross-document Copy or Move stages two records.

Document-bound writes share one guard order: recorded family tombstone (a
tombstone whose deletion version is newer than the writer's observed document
version rejects first, which also outranks the missing-document answer),
document presence, the collection's schema-validation policy, then the
expected document version.

### Transaction binding check

The Engine exposes no transaction-to-Engine identity accessor and
`src/engine/**` is frozen for this packet, so `stage` proves the binding with
the Engine's own operation-reservation oracle: `Engine::read_snapshot`
rejects a scope that belongs to another Engine
(`TransactionEngineMismatch`), and `Engine::begin` with the request's scope
and the transaction's operation identity reports `OperationIdReuse` exactly
when that transaction is live on this Engine under that tenant scope. A
scope that passes the first check but not the second reports
`TenantMismatch`. The probe stages no write, commits nothing, and releases
its reservation immediately.

## Invariants held

- One validated `TenantScope` rides every read (context) and every staged
  write (payload plus transaction); no family value, metadata, or error
  crosses tenants.
- Document writes plus any number of family writes publish exactly one
  transaction marker; there is no family-only commit path and no partial
  family result (`family_writes_compose_with_documents_in_one_transaction_marker`,
  `counter_batch_is_atomic_across_increment_delete_and_recreate`,
  `family_transaction_abort_hides_all_staged_family_changes`).
- Family reads are immutable-snapshot reads
  (`family_read_snapshot_remains_immutable_after_engine_commit`).
- The store borrows its Engine and cannot outlive it; the compile-fail doc
  tests on `FamilyStore` plus
  `family_store_borrow_prevents_engine_shutdown_while_live` cover the
  shutdown-while-live direction.
- Attachment content stays logical: metadata, size, and digest bookkeeping
  only. No content bytes, streams, chunks, or cursors exist in W3-F.

## Focused-test evidence

All commands were run from the worktree root with the pinned toolchain. Full
check results, including exit codes, are in the packet report; the focused
family tests are:

- In-crate (`src/families/tests.rs`), 38 tests: the 18 contract-list names
  `engine_families_returns_store_bound_to_durable_engine`,
  `family_store_borrow_prevents_engine_shutdown_while_live`,
  `family_stage_rejects_transaction_from_another_engine`,
  `family_stage_rejects_transaction_with_a_different_operation_identity`,
  `attachment_metadata_round_trip_is_tenant_scoped`,
  `counter_crud_preserves_signed_values_and_missing_errors`,
  `counter_batch_is_atomic_across_increment_delete_and_recreate`,
  `counter_overflow_rejects_without_partial_state`,
  `time_series_append_and_delete_preserve_timestamp_order`,
  `revision_page_and_revert_keep_snapshot_identity`,
  `expiration_and_refresh_updates_do_not_resurrect_deleted_documents`,
  `schema_validation_rejects_invalid_writes_without_mutation`,
  `family_tombstone_blocks_stale_local_resurrection`,
  `family_transaction_abort_hides_all_staged_family_changes`,
  `family_read_snapshot_remains_immutable_after_engine_commit`,
  `family_read_context_rejects_tenant_mismatch_without_data_access`,
  `family_scope_rejects_cross_tenant_reads_and_writes`,
  `family_payload_constructors_validate_typed_fields_before_access`, plus
  the focused extras
  `compare_exchange_conditions_require_the_current_index`,
  `compare_exchange_expiry_hides_expired_values`,
  `identity_allocation_is_monotonic_and_backwards_seeding_requires_force`,
  `revision_policy_trims_the_oldest_revisions`,
  `revision_revert_rejects_unknown_references`,
  `time_series_arity_and_policy_rules_are_typed`,
  `time_series_policy_reports_retention_bounds`,
  `validation_policy_rejects_malformed_definitions`,
  `attachment_rename_copy_and_duplicate_names_are_typed`,
  `family_writes_compose_with_documents_in_one_transaction_marker`,
  `family_requests_reject_cancelled_signals_and_elapsed_deadlines`,
  `archival_state_round_trips_through_the_seam`,
  `family_reads_reject_snapshots_from_another_engine`,
  `expected_version_conflicts_are_typed_at_stage_time`, plus the repair-loop
  a2 tests `same_document_attachment_copy_and_move_commit_as_one_record`,
  `malformed_reserved_record_collision_is_typed`,
  `number_range_binds_float_and_integer_values`,
  `time_series_names_reject_the_record_segment_separator`,
  `identity_allocation_exhaustion_is_typed`, and
  `time_series_full_reads_respect_the_retention_horizon`.
- Integration (`tests/families/`), 5 tests covering only public construction:
  payload validation before observation, read-payload accessors, the named
  request wrappers, the closed family-kind set, and schema rule reporting.
- Doc tests: two compile-fail examples on `FamilyStore` (no store outliving
  its Engine; no independent durable or in-memory store constructor).
- Pre-existing suite: 69 tests before this packet, all still green (114
  total after repair loop a2).

## Per-row behavior coverage notes

These notes map each of the 18 W3-F rows to the focused tests that express
its observable semantics. They record coverage only: no row is resolved, no
Proof ID is added, and no parity claim is made.

| Row | Tests expressing the row's observable semantics |
| --- | --- |
| PB-3000 attachments-crud | `attachment_metadata_round_trip_is_tenant_scoped` (store/enumerate/read metadata, tenant scoping), `attachment_rename_copy_and_duplicate_names_are_typed` (replace, duplicate-name, rename, cross-document copy and move), `same_document_attachment_copy_and_move_commit_as_one_record` (same-document copy and rename commit as one record write) |
| PB-3001 counters-crud | `counter_crud_preserves_signed_values_and_missing_errors` (signed deltas, name casing, delete/recreate, missing counter and missing document), `family_scope_rejects_cross_tenant_reads_and_writes` |
| PB-3003 attachments-batch-session | `family_writes_compose_with_documents_in_one_transaction_marker` (attachment with document and counter changes in one marker), `family_transaction_abort_hides_all_staged_family_changes` |
| PB-3005 attachments-revisions | `attachment_metadata_round_trip_is_tenant_scoped` plus `revision_page_and_revert_keep_snapshot_identity` (revision log identity preserved while attachment metadata rides the same records); historical attachment-content reads remain a W4 handoff |
| PB-3009 counters-batch-transaction | `counter_batch_is_atomic_across_increment_delete_and_recreate` (one batch across increment, delete, recreate; failed batch leaves no partial state) |
| PB-3015 counters-errors-overflow | `counter_overflow_rejects_without_partial_state` (typed overflow, no wrap, no partial commit), `family_payload_constructors_validate_typed_fields_before_access` (malformed names rejected at construction) |
| PB-3016 time-series-append-delete | `time_series_append_and_delete_preserve_timestamp_order` (out-of-order appends, point and range deletes, bounded range reads) |
| PB-3019 time-series-rollup-retention-config | `time_series_policy_reports_retention_bounds` (policy configuration, reported retention window, named rollups), `time_series_arity_and_policy_rules_are_typed` (malformed policy rejected without mutation), `time_series_full_reads_respect_the_retention_horizon` (the horizon bounds full and ranged reads symmetrically) |
| PB-3024 time-series-errors | `time_series_arity_and_policy_rules_are_typed` (distinct arity, range, and policy cases), `time_series_policy_reports_retention_bounds` (typed range error), `time_series_names_reject_the_record_segment_separator` (a series name cannot alias another document's series), `counter_crud_preserves_signed_values_and_missing_errors` (missing-document distinction) |
| PB-3025 revisions-crud-range | `revision_page_and_revert_keep_snapshot_identity` (paging, ordering, revert by revision), `revision_revert_rejects_unknown_references` |
| PB-3026 revisions-config-bin-retention | `revision_policy_trims_the_oldest_revisions` (configured limits trim the oldest entries, never below the minimum) |
| PB-3029 compare-exchange-conditional | `compare_exchange_conditions_require_the_current_index` (index-keyed put/delete, stale index rejection), `family_scope_rejects_cross_tenant_reads_and_writes` (tenant scope at the seam) |
| PB-3030 compare-exchange-batch-expiration | `compare_exchange_expiry_hides_expired_values` (expired values are not live and accept a fresh put; the retained index still rejects stale writers) |
| PB-3031 identities-hi-lo-seeding | `identity_allocation_is_monotonic_and_backwards_seeding_requires_force` (monotonic allocation, explicit force for backwards seeding), `identity_allocation_exhaustion_is_typed` (allocation past the representable ceiling is typed; unrepresentable seed targets rejected at construction) |
| PB-3033 expiration-and-refresh | `expiration_and_refresh_updates_do_not_resurrect_deleted_documents` (elapsed expiry observable, refresh does not clear expiry, deleted documents reject lifecycle writes) |
| PB-3036 schema-validation-config-errors | `schema_validation_rejects_invalid_writes_without_mutation` (policy configuration, on-demand violation report, rejected write stages nothing), `validation_policy_rejects_malformed_definitions`, `number_range_binds_float_and_integer_values` (numeric bounds bind both encodings) |
| PB-3038 family-conflicts-tombstones | `family_tombstone_blocks_stale_local_resurrection` (recorded tombstone outranks a stale writer and the missing-document answer; explicit clear unblocks) |
| PB-3039 family-transactions-batch | `family_writes_compose_with_documents_in_one_transaction_marker`, `family_transaction_abort_hides_all_staged_family_changes`, `counter_batch_is_atomic_across_increment_delete_and_recreate`, `same_document_attachment_copy_and_move_commit_as_one_record` (a mutation touching one record twice still stages exactly one write) |

Rows owned by other packets (PB-3010/3020/3040 query and index, PB-3002/3014/
3017/3023/3041/3043 streaming, the replication set, the backup and migration
set, PB-3008, and PB-3042) were not implemented and remain explicit
handoffs.

## Deviations from the declaration tree

The W3-0 declaration tree was declaration-only; this packet replaced it.
Deviations beyond filling in behavior:

| Declaration (W3-0) | W3-F | Reason |
| --- | --- | --- |
| Payload and result derives included `Eq` | Requests, payloads, and results derive `Clone, Debug, PartialEq` only | Compare-exchange and revision payloads carry `DocumentValue` and time-series values carry `f64`; `Eq` on those would be unsound, matching the accepted `Document`/`DocumentValue` precedent |
| `FamilyError` had 8 cases | 8 declared cases kept with their meanings, plus 17 family-specific and supporting cases (25 total, counted on the enum) | The contract requires distinct cases for counter overflow, time-series arity and ranges, revision references, malformed validation policy, duplicate attachment names, and lifecycle values |
| No payload had real fields | Real typed fields, including a `TenantScope` and `ExpectedVersion` on document-bound writes | The contract's invariant that every staged write carries one validated `TenantScope`, and the frozen `stage` signature, which otherwise gives the store no scope to validate against |
| `FamilyStore` held `PhantomData` | Holds `&'engine Engine` | The store must reach `Engine::read_snapshot`, `Engine::begin`, and the transaction the caller passes |
| `FamilyKind` was `Clone, Copy, Debug, Eq, PartialEq` | Adds `Hash, Ord, PartialOrd` | Tombstone records and policy maps key by family kind |
| No `FamilyKind` method existed | Adds the public `FamilyKind::record_key()` returning the reserved record key of one family | The public kind set and the reserved tombstone key space are the same closed vocabulary, so callers and later packets can name a family's tombstone without a private mapping; it adds one method to the frozen type without changing any variant |

`tests/public_exports.rs` compiles unchanged, and `src/lib.rs` was not
edited: the payload types are reachable through the public `families`
module, which is how the export test already names them.

## Assumptions

- The `TenantScope` in a write payload is the scope the caller's transaction
  was begun with. This is what makes the reservation probe an exact check
  for honest callers; a caller declaring a scope the transaction was not
  begun with gets `TenantMismatch`.
- `Engine::begin`'s operation reservation is per (tenant, operation) per
  Engine, so a probe with the live transaction's identity under the request's
  tenant scope reports `OperationIdReuse` exactly when the transaction is
  bound to this store's Engine and scope.
- Operation identities are unique per logical operation across Engines (the
  Engine's own reuse rules), so the reservation probe cannot false-accept
  for a well-behaved caller.
- Read transactions mint a fresh internal identity per call, so family reads
  never collide with caller operations or with each other.
- `stage` returning `Ok(())` stages only; results (for example allocated
  identity ranges) are observed by a later read, per the frozen signature.
- One family mutation per record per transaction: the Engine rejects a write
  set that writes one document identifier twice, so a second mutation of the
  same family record must ride a new transaction or a wider batch. Different
  records (for example a counter batch plus an attachment store on the same
  document) compose freely in one transaction.
- Logical times are epoch milliseconds from `SystemTime`; no time
  qualification is claimed.

## Limits

- The Engine does not reserve the collection name or the `family:` identifier
  prefix, so a caller writing the reserved collection directly can collide
  with family state, within one tenant only — the Engine keys documents by
  tenant, so there is no cross-tenant collision path. The collision behavior
  splits by shape: a **malformed** value at a reserved record identifier
  surfaces as the typed `FamilyError::ReservedStateInvalid` when the family
  read decodes it, while a **well-formed family-encoded value** written
  directly through the document seam is silently accepted — the family read
  decodes it as family state, and a later family write silently overwrites
  the squatter document. Same-tenant integrity against a direct writer
  therefore waits for the engine-side reserved-namespace enforcement recorded
  under proposed collision actions.
- Deleting a document does not cascade to its family records: family state
  survives a document delete until it is rewritten or cleared. The tombstone
  family is the W3-F mechanism to record and guard such deletions; cascade
  and cleanup ownership belongs to the replication packet.
- Revision reads are deliberately unlike counter, attachment, time-series,
  and archival reads: a revision page for a document that does not exist (or
  no longer exists) returns an empty page instead of `DocumentMissing`,
  because a document's revision history legitimately outlives the document
  (a deleted document keeps its revision log until a tombstone or cleanup
  says otherwise). Presence-sensitive families keep the `DocumentMissing`
  error; the revision family reports absence as an empty page with `total()`
  zero.
- Schema validation is enforced at the family seam for document-bound family
  writes and by on-demand reads; a direct `Transaction::put` is not validated
  against a collection policy, because the Engine does not consult families.
  `NumberRange` binds both numeric encodings — integer and float — but does
  not order a NaN field against a bound; the type rule is what rejects a
  non-numeric field outright.
- Attachment content is metadata only: no bytes, streams, chunked transport,
  or bounded readers exist here (W4 owns them).
- Time-series pages are bounded by the record's document size, not by an
  explicit cursor or backpressure protocol (W4 owns bounded readers).
- The retention horizon derived from the newest stored point bounds both
  read paths symmetrically (a fully pre-horizon range is a typed range
  error; a full read returns the retained view only), but retention cleanup
  itself does not run, so pre-horizon entries stay stored until the
  streaming packet owns their deletion.
- Rollup policies are stored, validated, and reported; rollup generation
  does not run.
- The compare-exchange index is monotonic per key and tenant within one
  Engine; no cluster-wide ordering exists or is claimed (W4 replication).
- Identity allocation is monotonic within one Engine's record and bounded by
  the representable identity ceiling (`i64::MAX`): seeding above it is
  rejected at construction and allocating past it is the typed
  `IdentityExhausted`. No Hi-Lo block distribution exists.
- `FamilyError` is a closed unit-variant set, so violations and rejections
  carry no per-document detail; detail comes from the matching read.

## What is not claimed

- No V1 parity claim and no change to any parity overlay row: all 46 family
  rows remain `unresolved` with empty Proof ID lists.
- No PB-3042 or any authorization claim: W3-F implements no authorization,
  confirmation, or audit behavior. Tenant-scope isolation at the family seam
  is the only isolation behavior here.
- No recovery, durability-beyond-focused-tests, replication,
  acknowledgement-level, convergence, member, region, sharding, backup,
  import, migration, or streaming claim; no Recovery, Replication, or
  Benchmark Receipt exists.
- No query or index claim: families expose no projection inputs yet and
  touch nothing under `src/query/**` or `src/index/**`.
- No performance, concurrency, or scale claim; no benchmark ran.

## Proposed parent collision actions

None required for `src/lib.rs`: the frozen re-exports still compile, and the
payload types are public through the `families` module. If the parent later
wants root-level re-exports of the payload vocabulary (for example
`pub use families::{AttachmentRead, CounterOperation, ...}`), that is a
one-line-per-name integrator edit to `src/lib.rs`, listed here rather than
made.

Two engine-side gaps are recorded for the parent to schedule with the engine
owner, since `src/engine/**` is frozen for this packet:

1. A `pub(crate)` Engine (or `Transaction`) identity accessor would replace
   the reservation-probe heuristic with a direct identity comparison in
   `FamilyStore::stage`.
2. Reserved-namespace enforcement (a reserved collection or identifier
   prefix at the Engine boundary) would close the same-tenant collision
   limit recorded above: it would stop a direct document-seam writer from
   squatting a reserved record with a well-formed family-encoded value
   (silently accepted today) and from forcing a family write to overwrite a
   squatter document, and it would let the typed `ReservedStateInvalid`
   cover every collision shape rather than only malformed values.
