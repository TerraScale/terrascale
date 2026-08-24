# W3-1 engine record: transactions, commit outcomes, and durable resolution

Status: implementation record for the W3-1 Storage plus Engine packet,
2026-08-24. Source identity: the W3-1 worktree branch
`fleet/w3-storage-engine`, based on commit `2775b24` (W3-0 preparation
`c21a85f` included). This record describes focused-test evidence only. It is
not a parity qualification and makes no V1 comparison.

## Scope and ownership

Same ownership as the storage record: `src/storage/**`, `src/engine/**`,
`tests/storage/**`, `tests/engine/**`, and this documentation path. No shared
file was edited; `tests/public_exports.rs` needed no change because it matches
no commit-outcome variants.

## Public seam as implemented

`Engine::open(EngineConfig)` is the only production constructor.
`EngineConfig::durable` requires a real directory location: it creates an
absent directory and rejects an empty location
(`EngineConfigError::EmptyLocation`) or a location that cannot become a
directory (`EngineConfigError::NonDurableLocation`). There is no public
in-memory or ephemeral constructor; the absence of `new_in_memory`,
`from_memory`, and `ephemeral` is enforced by compile-fail doc tests on
`Engine` and `EngineConfig`, and `production_api_has_no_ephemeral_durable_engine_constructor`
proves the runtime half.

`Engine::queries` and `Engine::families` remain `unimplemented!` and belong to
W3-2 and W3-F; both still compile and use the existing `pub(crate)
from_engine` constructors so the borrowed-store lifetimes stay intact.

## Engine identity

Each `Engine::open` mints a fresh identity from the process id, a monotonic
clock reading, and a per-process counter, so an identity is never reused
across opens. `ReadSnapshot` and `TenantScope` carry that identity, which is
what makes cross-Engine snapshots and scopes rejectable
(`begin_rejects_snapshot_from_another_engine`,
`begin_rejects_scope_different_from_snapshot`). This runtime identity is
separate from the persistent storage-format identity on disk; a location whose
format bytes do not verify opens as `EngineOpenError::FormatMismatch`.

Because `ReadSnapshot` keeps its storage sequence private to `src/types.rs`,
which this packet does not own, the Engine records the sequence of every
snapshot it issues and recovers it by value equality when a transaction
begins. A `pub(crate) fn sequence()` accessor on `ReadSnapshot` in
`src/types.rs` would remove that registry; that exact collision action is
proposed to the parent at the end of this record.

## Transaction semantics

`Engine::begin` validates, in order and before creating a transaction: the
snapshot's Engine identity, the requested scope's Engine identity, the
snapshot tenant against the scope tenant, and the operation identity. The
operation-identity check rejects an identity already in use by another live
transaction of the same Engine with `EngineError::OperationIdReuse`;
`OperationId` is validated at construction, so "invalid at begin" can only
mean "already bound to another live request". Committing or dropping the
transaction releases that reservation; the durable operation ledger remains
the binding for any committed identity.

`Transaction` stages `put` and `delete` operations. Reads inside the
transaction observe the staged writes and otherwise the transaction's
immutable snapshot. Expected versions are enforced twice: against the
snapshot view the caller reasoned about, and against the published view at
commit time, so a writer that committed after the snapshot cannot be
overwritten unnoticed (`transaction_conflict_returns_rejected_without_partial_writes`,
`expected_version_any_new_and_exact_are_enforced`). One commit publishes the
whole write set atomically under one storage sequence
(`one_transaction_publishes_all_writes_atomically`). The tenant scope is
captured at `begin` and has no mutation path
(`tenant_scope_cannot_change_during_a_transaction`).

An empty write set and duplicate targets are validation rejections; documents
beyond one megabyte or sixty-four levels of nesting are rejected as
`EngineError::InvalidDocument`.

## Commit outcomes and retry policy

`CommitError` is the frozen five-variant set with an exhaustive, stable
`retry_disposition`:

| `CommitError` | `RetryDisposition` |
| --- | --- |
| `Validation` | `NewOperation` |
| `Conflict` | `NewOperation` |
| `OperationIdReuse` | `Never` |
| `EngineClosed` | `SameOperation` |
| `StorageFailure` | `AfterRecovery` |

`UnknownReason` is the frozen six-variant set. W3-1 produces
`ResponseInterrupted` and the three post-publication causes;
`Deadline` and `Cancellation` exist for the contract and are not produced
because the frozen transaction signatures carry no deadline or cancellation.
`DurableResolution` is exactly `Committed(CommitReceipt)`, `Rejected`, and
`Pending`, with no unknown variant. `CommitReceipt` carries only the operation
identity, the storage sequence, and the achieved `LocalDurability`.

`CommitError::EngineClosed` and `EngineError::Closed` are not produced in
W3-1: `shutdown` consumes the Engine while the borrow checker keeps borrowed
stores and transactions alive, so no closure race exists to report. The
variants remain for the frozen contract.

## Durable resolution

`Engine::resolve_commit(scope, operation)` validates the scope and asks the
durable operation ledger. The resolution rules, the same ones storage applies
to same-identity retries, are listed in the storage record. The two mandated
post-publication fault tests
(`post_publication_pointer_sync_failure_returns_unknown_and_reopens_deterministically`
and the directory-sync twin) inject failures through the private test-only
fault control at the public commit seam and assert: `Unknown` with the
original identity, live-read visibility of the published root, no `Synced`
claim anywhere, deterministic reopen resolution, same-identity retry
returning the same resolution, and a different digest rejected as
`OperationIdReuse`. No test inspects raw pages, WAL bytes, or lock state.

## Publication events

The Engine holds one crate-private sink and emits an immutable
`CommitPublicationEvent` (operation, root identity, storage sequence) only
for committed publications, after the storage commit call returns and its
locks release. `commit_publication_event_is_emitted_after_storage_locks_release`
proves the ordering with a non-reentrant lock probe inside the sink. The
event carries no WAL, path, or byte fact, and index publication remains a
W3-2 consumer that may lag.

## Deviations corrected against the declaration tree

The W3-0 `src/engine/mod.rs` declarations disagreed with the frozen design
contract. This packet corrected them; the design document won in every case:

| Declaration (W3-0) | Corrected to (design contract) |
| --- | --- |
| `CommitError { Conflict, InvalidRequest, StorageFailure, Deadline, Cancelled }` | `CommitError { Validation, Conflict, OperationIdReuse, EngineClosed, StorageFailure }` with `retry_disposition` |
| `UnknownReason { PostPublicationSync, StorageUncertain, ResponseLost }` | `UnknownReason { Deadline, Cancellation, ResponseInterrupted, PostPublicationPointerSync, PostPublicationDirectorySync, PublishedLedgerSync }` |
| `DurableResolution::Unknown(UnknownCommit)` variant | Removed; resolution is `Committed`, `Rejected`, or `Pending` only |
| `EngineConfig::durable` and all `Engine` methods as `unimplemented!` | Implemented |

`tests/public_exports.rs` compiles unchanged: it names types only and matches
no commit-outcome variants, so the shared-file exception was not used.

## Assumptions

- The interpretation of `begin_rejects_invalid_operation_id` is "an operation
  identity already in use by another live transaction of this Engine", because
  `OperationId::try_new` rejects every structurally invalid identity at
  construction.
- An identity with no durable ledger entry resolves as `Pending`: absence of
  a record cannot prove rejection, and the caller must not retry under a new
  identity.
- Same-identity, same-digest retries replay the recorded resolution,
  including a recorded rejection, so retries are deterministic.
- In-crate tests use `pub(crate) TenantScope::from_parts`, mirroring
  Platform's production role of creating a validated scope. Integration tests
  cover only public construction paths.

## Limits

- The snapshot registry and root history grow without bound; there is no
  garbage collection.
- Conflict detection is first-committer-wins through expected versions; a
  write staged with `ExpectedVersion::Any` deliberately overwrites.
- `EnginePort` remains an unimplemented declaration owned by the Platform
  Adapter packets.
- No query, index, family, replication, or backup behavior exists here.

## What is not claimed

- No V1 parity claim, no V1 comparison, and no change to any parity overlay
  row: every PB item remains unresolved.
- No durability claim beyond the focused tests: `Synced` describes the
  declared local fault model only, and no Recovery Receipt exists.
- No multi-region, acknowledgement-level, convergence, member, or region
  claim; `CommitReceipt` is strictly local.
- No concurrency, throughput, or latency claim; no benchmark ran.

## Proposed collision action for the parent

`src/types.rs` (integrator-owned) would ideally add
`impl ReadSnapshot { pub(crate) fn sequence(&self) -> StorageSequence }` so
the Engine can bind a snapshot to its storage sequence directly and the
equality-keyed snapshot registry can be removed. This packet did not edit that
file; the registry is a documented in-scope workaround. The parent should
assign that exact one-line collision action to the integrator if accepted.
