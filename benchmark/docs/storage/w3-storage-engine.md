# W3-1 storage record: WAL, publication order, and recovery

Status: implementation record for the W3-1 Storage plus Engine packet,
2026-08-24. Source identity: the W3-1 worktree branch
`fleet/w3-storage-engine`, based on commit `2775b24` (W3-0 preparation
`c21a85f` included). Repair history pinned on this record: loop 1 (a3)
applied at commit `1898653`; loop 2 (a4) applies on top of `1898653`, and the
parent records the resulting tip hash in the packet report (a commit cannot
embed its own hash). This record describes focused-test evidence only. It is
not a Recovery Receipt, not a parity qualification, and not a performance
claim.

## Scope and ownership

This packet owns `src/storage/**`, `src/engine/**`, `tests/storage/**`,
`tests/engine/**`, and this documentation path. It changed nothing under
`src/lib.rs`, `src/types.rs`, `src/operations.rs`, `src/adapters/**`,
`src/families/**`, `src/query/**`, `src/index/**`, `Cargo.toml`,
`Cargo.lock`, or `docs/design/**`.

## Implementation design

The storage module is one private adapter behind one crate-private port.

| Piece | File | Responsibility |
| --- | --- | --- |
| `StoragePort` | `src/storage/mod.rs` | The only seam the Engine sees: commit, resolve, read at a snapshot, published state, flush. |
| `CommitPublicationEvent` | `src/storage/mod.rs` | Immutable crate-private event emitted after storage locks release. |
| codec | `src/storage/codec.rs` | Storage keys, value and document encoding, the FNV-1a checksum, the logical write-set digest, structural limits. |
| intents | `src/storage/intent.rs` | `CommitIntent`, `StagedWrite`, receipts, rejections, unknown causes, durable resolutions. |
| WAL | `src/storage/wal.rs` | Checksummed prepare/data/commit-marker frames, verified replay, corruption classification. |
| operation ledger | `src/storage/ledger.rs` | Durable `ReadyToPublish`, `Published`, and `Rejected` records plus the in-memory resolution index. |
| roots and manifest | `src/storage/root.rs` | Immutable `RootTable`s, root files, the manifest pointer, file and directory syncs. |
| file adapter | `src/storage/file.rs` | `FileStorage`: open, recovery, the publication order, resolution. |
| faults | `src/storage/faults.rs` | One-shot fault points inside the publication order, armable only from `cfg(test)` code in this crate. |

File layout inside a configured location (names are private to the adapter):

- `format` — storage-format identity: magic plus one version word. A location
  whose `format` bytes are absent is initialized; bytes that do not verify
  open as `EngineOpenError::FormatMismatch`.
- `wal` — append-only checksummed frames.
- `ledger` — append-only checksummed operation-ledger records.
- `manifest.tmp` and `manifest` — the temporary and atomically published root
  pointer.
- `roots/<identity>.root` — immutable root files named by sequence and content
  checksum.

Every checksum is a private 64-bit FNV-1a. It detects corruption; it is not a
cryptographic digest and never crosses a public seam.

## Publication order proof

`FileStorage::commit_intent` implements the ten mandated steps in order. The
code path is linear, so the order is readable directly:

1. Validate the write set, the operation identity against the durable ledger,
   and every expected version against both the transaction's snapshot table
   and the published table. Nothing is written, and the published root does
   not move. `Validation` and `OperationIdReuse` are returned without any
   durable record; only a conflict rejection is recorded durably.
2. Encode the write set into private frames through the codec. Raw bytes exist
   only from here into the private writers.
3. Append the prepare record (tenant, operation, digest, sequence, write
   count) and one data frame per staged write. `Buffered` keeps these frames
   process-local only.
4. Apply the write set to a new private root, compute the root identity, and
   append the commit marker naming the root. The published root stays readable
   throughout.
5. Apply the requested durability action. `Synced` syncs the WAL, then writes
   and syncs the root file and the temporary manifest. `Written` writes both
   without syncing. `Buffered` performs no file writes.
6. Append and durably sync the `ReadyToPublish` ledger record. This sync
   happens for every durability level: it persists resolution metadata, not
   data bytes.
7. Publish the root pointer. `Written` and `Synced` rename the temporary
   manifest; `Buffered` swaps the in-memory published root only, which is the
   whole difference its loss window declares.
8. For `Synced`, sync the published pointer file and then the containing
   directory. A failure of either returns `Unknown` with the matching reason
   and never claims `Synced`.
9. Append and durably sync the `Published` ledger record, recording whether
   the post-publication syncs were confirmed. A failure here returns
   `Unknown(PublishedLedgerSync)`.
10. Return the receipt. Storage locks release when the commit call returns,
    and only then does the Engine emit the immutable `CommitPublicationEvent`.

The test `ready_to_publish_ledger_record_is_durable_before_pointer_replacement`
proves the step 6 to step 7 ordering behaviorally in both directions: with the
ledger sync fault armed, the pointer is never replaced; with the pointer
publication fault armed, the durable record still answers resolution by
identity after a reopen.

## Recovery

Open verifies the format identity, scans the ledger, and scans the WAL:

- A prepare group without a structurally complete, checksum-valid commit
  marker is discarded. It never publishes anything
  (`recovery_discards_prepare_frames_without_a_valid_commit_marker`).
- A torn or checksum-failing frame in the **trailing** position is the
  interrupted-append case: recovery treats it as absent, truncates the log to
  the verified prefix, and opens on that prefix. The commit before the torn
  tail remains readable
  (`torn_trailing_wal_frame_is_truncated_at_recovery_and_data_survives`).
- The same damage at any **non-trailing** position is mid-file corruption and
  fails the open with `FormatMismatch`
  (`recovery_rejects_bad_checksum_and_truncated_frame`). This is deliberately
  fail-closed: the design mandates rejection of frames that do not verify, and
  replaying around a hole in the middle of the log cannot be justified.
- `WalWriter::append` rolls back to the last-good offset whenever a write
  cannot complete, so a partially written frame never persists
  (`failed_wal_append_rolls_back_to_the_last_good_offset`). If the
  recovery-time truncation of a torn tail itself fails, the open fails with
  the typed unavailable error rather than returning a writer whose append
  offset still spans the torn bytes
  (`failed_torn_tail_truncation_fails_the_open`).
- Recovery roll-forward appends a `Published` record for every verified WAL
  commit, and that record carries the operation's write-set digest taken from
  the WAL commit. A commit interrupted after its WAL group was durable but
  before its `ReadyToPublish` record therefore keeps its identity bound, so a
  same-identity same-write-set retry replays the recorded resolution instead
  of colliding as identity reuse
  (`roll_forward_digest_lets_a_same_identity_retry_replay_after_reopen`).
- A manifest whose checksum or root identity does not verify is ignored, and
  the published state is rebuilt from the verified WAL commits
  (`manifests_that_do_not_verify_are_ignored_during_recovery`).
- Every verified WAL commit newer than the published root is rolled forward:
  its write set is applied, its root file and manifest are written, and a
  `Published` record with `post_publish_synced = false` is appended. Roll
  forward never claims a post-publication sync it did not perform
  (`recovery_replays_a_verified_commit_that_was_never_published`).
- The ledger scan ignores one torn trailing record: an appended record that
  was never synced is not durable state (`ledger_scan_ignores_a_torn_trailing_record`).

## Durable resolution rules

Resolution is computed from the ledger index and the published root identity:

| Durable state | Resolution |
| --- | --- |
| `Published` with confirmed post-publication syncs | `Committed(min(requested, Synced))` |
| `Published` without confirmed syncs | `Committed(min(requested, Written))` |
| `Ready` and the published root identity matches the recorded root | `Committed(min(requested, Written))` |
| `Ready` and no matching published root | `Pending`, never a silent rejection |
| `Rejected` record | `Rejected` with the recorded reason |
| No record for the identity | `Pending`, because absence cannot prove rejection |

A same-identity retry replays the recorded resolution instead of publishing a
second commit; a different digest under the same identity is
`OperationIdReuse`.

## Invariants held

- Raw bytes, frames, checksums, manifests, paths, and lock state never leave
  `src/storage/**`. Public results carry identifiers, sequences, durability
  levels, and typed outcomes only.
- The commit of one transaction publishes its whole write set under one
  sequence or nothing at all.
- The resolution ledger is tenant-scoped: records are keyed by tenant and
  operation identity, so one tenant cannot resolve or bind another tenant's
  operation.
- Every durability level follows the same marker, pointer, ledger, lock
  release, and event order; only the byte-level actions differ.
- Storage sequences are allocated past a watermark that starts above every
  sequence the manifest, the WAL, and the operation ledger name, and advances
  as soon as a commit allocates its sequence — including a commit that later
  fails. A sequence is therefore never reused inside one location, so WAL
  replay can order groups by sequence without a failed group shadowing a
  later committed one
  (`mid_publication_failure_never_reuses_a_storage_sequence`).
- The write-set digest binds an operation identity to the logical request: a
  delete contributes its identifier and expected version, not the collection
  the delete happened to resolve against in the caller's snapshot
  (`delete_digest_is_independent_of_snapshot_state`).

## Deviations corrected against the declaration tree

The W3-0 declaration file `src/storage/mod.rs` contained placeholder private
types and an empty `StoragePort`. This packet replaced them with the real
implementation; no public type moved, because the module had no public items.

## Assumptions

- A storage location is owned by one open Engine at a time. There is no lock
  file in W3-1; concurrent Engines on one location are undefined.
- Document identifiers are unique within a tenant plus collection, the key the
  adapter stores. `Transaction::read`, whose frozen signature names only a
  `DocumentId`, returns the first match in canonical key order when an
  identifier repeats across collections.
- The FNV-1a checksum and the u32 frame lengths are bounded by the codec's
  one-megabyte document limit and by write-set sizes no caller can push past
  a u32 length.
- Recovery reads whole WAL, ledger, and root files into memory. Segmenting,
  compaction, and incremental scans are later work.
- The write-set digest spelling changed in this packet (a delete no longer
  encodes its snapshot-resolved collection), and the private `Published`
  operation-ledger record layout changed with it (the digest field was
  added). Locations written by earlier W3-1 revisions would bind
  pre-existing identities under the old spellings and misparse old ledger
  records; W3-1 has no released storage format, so no compatibility path
  is kept.

## Limits

- Roots are full tables: every commit rewrites the complete document set of
  the tenant's root. There is no delta or segment format; the per-commit
  write-amplification half of this limit stands. The *accumulation* half is
  closed by GAP-S1 compaction (ticket #81,
  `docs/storage/gaps1-storage-compaction.md`): superseded root files, the
  write-ahead log a checkpoint replaces, and — under the retained-evidence
  gates — publication history below a durable bound are reclaimable by a
  caller-driven pass.
- The root history grows without garbage collection on disk (GAP-S1 closes
  the durable half; the in-memory root-history map is still process-lifetime,
  per the GAP-S1 record's Limits).
- `Buffered` publication is in-memory only, so its data is lost on process
  exit by design; only its resolution metadata survives.
- **Explicit operational limit: a `Buffered` commit interrupted between the
  `ReadyToPublish` record and the `Published` record resolves `Pending`
  forever.** A `Buffered` commit writes no WAL group, so there is nothing for
  recovery to roll forward: its durable `Ready` record names a root identity
  that never reaches disk, and the reopened published root can never match it.
  `resolve_commit` therefore returns `Pending` (never a silent rejection, per
  contract), and a same-identity retry keeps returning
  `Unknown(ResponseInterrupted)`. The escape is a new operation identity: the
  data never persisted, so re-issuing the logical write under a new identity
  succeeds. `Written` and `Synced` commits do not have this window, because
  their durable WAL group is rolled forward at reopen and a `Published`
  record is appended for it. This is the "retain `Pending` with an explicit
  operational limit" clause of the durable-resolution contract.
  *Narrowed by GAP-S1* (ticket #81): a storage compaction checkpoint makes
  the published table — `Buffered` tail included — durable, so a
  checkpointed `Buffered` commit's `Ready` root identity becomes the
  published root and it resolves `Committed` after the reopen (at the
  requested level; resolution caps every claim at what the operation asked
  for). Stores that never compact keep the full limit above.
- No lock file, no concurrent-Engine protocol, no encryption, and no backup
  or restore. (Compaction landed with GAP-S1, ticket #81; backup/restore with
  the BK wave.)
- The torn-tail rule classifies damage by position: a corrupted length field
  inside an early frame can make the remainder of the log look like one
  trailing torn frame, in which case recovery truncates at that frame and the
  commits behind it are dropped. Verified-prefix recovery cannot distinguish
  that case from an interrupted append; the fail-closed mid-file rule covers
  every damage shape that leaves readable bytes behind the damaged frame.
- **Accepted identity-binding limit (parent-adjudicated deferral).** The
  operation-identity binding and the root identity are both derived from the
  private 64-bit FNV-1a checksum, not from a collision-resistant digest. W3-1
  claims no collision resistance: within one tenant, two distinct write sets
  deliberately constructed to collide on this checksum would share one
  operation-identity binding or one root identity. Replacing it with a
  stronger std-only digest is deferred to a later packet.

## What is not claimed

- No V1 parity: no PB-5000 through PB-5041 row is implemented or qualified by
  this packet, and no parity overlay status changed.
- No Recovery Receipt: formal recovery qualification belongs to the exclusive
  recovery qualification lane after crash, restart, restore, migration, and
  fault scenarios run with the required redaction.
- No multi-region, replication, acknowledgement-level, convergence, or member
  visibility claim. `LocalDurability` is local only.
- No performance claim of any kind; no benchmark ran.
- No power-loss simulation: `Written` and `Synced` are observed through
  process exit and reopen only. The declared loss windows are contract
  statements, not measured results.
