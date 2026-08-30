# The LSM storage engine (P1, first landing)

Status: implemented behind the `StoragePort` seam as the default backend
for new locations. Owner: P1. Parents: ADR 0016 (design), ADR 0015
(execution), charter PD-2026-08-28-05/06.

This record names the format, the publication order, the contracts the
engine keeps, and the honest limits of this first landing. The tier
manifest, mover, and cold-read fallback remain open P1 work recorded
below.

## Why

The W3-1 root-file engine rewrites a full tenant root per commit: a
recorded 512-write commissioning window appended 154,246,326 durable
bytes for ~300-byte documents, about 300 KB per write. That cost is
unbounded by the write set. The LSM engine bounds durable bytes per write by
the write set itself: one checksummed WAL frame carries the whole ordered
commit batch. The focused amplification test pins the bound below 3 KiB per
~300-byte document. That is two orders of magnitude under the baseline by
construction.

## Layout

A location is one directory. Format 1 stores the byte-exact `TBV2LSM1`
marker in `format`. Coordinated format-2 activation preserves that prefix
and appends a domain-separated BLAKE3-checked `TBLSMACT` record. The record
binds the store identity, frontier feature and version, activation identity,
and external-writer fence. An exact-marker N-1 reader therefore rejects an
activated location.

- `wal` holds append-only commit frames. One frame carries one ordered commit
  batch: document entries (puts and tombstones), the operation-ledger
  record, the publication-history record, and accepted-mutation usage.
  Its length-prefixed envelope retains the FNV-1a checksum used by format
  1 for frame parsing.
- `wal.frontier` has two alternating 128-byte slots. Each slot has its own
  domain-separated BLAKE3 checksum and binds the store identity, slot
  generation, WAL epoch, byte length, frame count, and a BLAKE3 chain over
  every raw WAL frame. Format 1 maintains this as a disposable activation
  shadow. Format 2 treats it as authoritative.
- `seg-<id>.lsm` files are immutable segments with a 20-byte header, sorted records,
  a sparse index (one anchor per 32 records), and a verifying footer
  whose key lengths trail their bytes so it parses backwards from the
  crc.
- `MANIFEST` is the byte-exact `TBLSMMF1` format-1 authority. It names the
  segment list, next segment identity, and WAL checkpoint.
- `MANIFEST.active` is the BLAKE3-checked `TBLSMMF2` format-2 authority. It
  also binds the live WAL epoch and, during checkpoint recovery, the exact
  prior WAL anchor. Keeping this state in a sidecar leaves `MANIFEST`
  unchanged until the extended `format` marker commits activation.
- `retained` and `retained.frontier` hold the GAP-S1 retained-evidence stream
  and its two-slot BLAKE3 anchor. The retained frontier is disposable in
  format 1 and authoritative in format 2.

Manifest and format publication use a temporary file, file sync, atomic
rename, and directory sync. Activation first syncs the WAL and retained
stream, rebuilds both frontier files from their verified bytes, publishes
`MANIFEST.active`, then publishes the extended `format` marker. The format
publication is the compatibility boundary. Before the atomic format
replacement, format 1 opens the untouched `MANIFEST`. Once recovery selects
format 2, it requires the active manifest and frontiers. Activation is
explicit, rejects a stale frontier observation, and is idempotent only for
the same external fence record.

Four keyspaces pack one ordered key space: documents (order-preserving
NUL-separated `tenant\0collection\0document` keys, so byte order equals
the engine's canonical key order), ledger (`tenant\0operation`),
history (big-endian sequence), and meta (labels). Internal keys order
by user key ascending then sequence descending, so the newest version
of a key is always first in a merged scan.

## Publication order per commit

validate → resolve identity (idempotent replay / `OperationIdReuse`) →
conflict-check against snapshot and published views → allocate sequence
(past everything the location ever named; holes are legal, reuse never)
→ build batch → WAL append → sync when the selected contract requires it →
format-2 frontier-slot sync → `Synced` ledger-flip append, sync, and anchor →
memtable apply → receipt. Non-`Synced` requests skip the ledger-flip step.

The root identity becomes a chain: `fnv(previous head ‖ sequence ‖
digest)`, 32 canonical hex characters. It is verifiable by replay and
never rewrites a table.

Durability levels:

- `Synced` fsyncs the batch and ledger flip frames separately. The flip is
  the analogue of the root store's post-publication `Published` record. If
  it is absent after a crash, recovery reports an honest `Written`
  resolution.
- `Written` in format 1 appends the batch without requesting fsync. Format 2
  syncs every result-bearing WAL append before advancing its authoritative
  frontier, which may provide stronger physical persistence than the
  requested receipt level.
- An accepted `Buffered` mutation omits document-keyspace and usage entries
  from its WAL batch but keeps its ledger and reconstructable history
  entries. Verified bytes therefore resolve the operation after reopen
  instead of reporting `Pending`. A conflict is different: its rejection
  ledger record is always appended, WAL-synced, frontier-bound in format 2,
  and inserted into the memtable before the caller receives
  `Rejected(Conflict)`. This rule also applies when the rejected request
  asked for `Buffered`, so a checkpoint cannot erase the resolution.

Fault mapping follows the root store's replayable-marker split.
`WalAppend`, `CommitMarkerApplication`, and `PointerPublish` fail before a
complete WAL append, so they return a definite storage rejection without
applying the batch. Once a complete replayable frame exists, an interrupted
Ready transition returns `Unknown(ResponseInterrupted)`. Pointer,
directory, and Published-ledger failures retain their more specific Unknown
causes. A format-2 WAL or frontier witness failure poisons the handle
immediately; commits, flushes, compaction, and mover work refuse until
recovery reopens and verifies the streams. `JournalAppend` remains the test
hook that omits a history entry and exposes a high-water gap.

## Recovery

Open scans the retained store, reads the manifest for the selected format,
verifies every named segment against its manifest metadata, and replays the
verified WAL tail idempotently by internal key. It then restores the
sequence watermark and re-derives the history floor over the rebuilt index.

Recovery does not rebuild a published manifest from whatever segment files
happen to remain. A format-2 location requires its WAL, retained stream,
valid active manifest, both frontier files, and every named segment. New
locations keep that authority in `MANIFEST.active`; recovery also accepts the
short-lived pre-sidecar `TBLSMMF2` encoding in `MANIFEST`. A missing or corrupt
authority, missing or corrupt named segment, foreign store identity,
incompatible schema, or impossible frontier generation or epoch fails closed
with `FormatMismatch`. A format-1 location may omit `MANIFEST` only before it
has segment artifacts; damage or a missing authority beside segments also
fails closed.

WAL damage classification matches the root store's rule: a damaged
frame with bytes behind its declared end is mid-file corruption and the
open fails closed (`FormatMismatch`). Only a trailing incomplete frame is a
torn append and truncates. Its complete header must start with the frame
magic because appends write the magic first.

After activation, the BLAKE3 frontier adds proof the per-frame FNV checksum
cannot provide. It detects exact final-frame loss, legal-length corruption,
and same-length FNV-valid substitution. One damaged frontier slot repairs
from the verified stream; two unusable slots fail closed. The retained stream
uses the same rule for exact fact or bound loss.

`Engine::open` selects the backend from the location's format marker. A
`TBV2FORMAT` location opens on the root-file engine; everything else reaches
the LSM engine, which is the default for new locations. Restored root-format
locations therefore keep opening. No public API changed.

## Compaction

`compact` freezes the live memtable and syncs level-0 segment files before
publishing the manifest that names them. An activated checkpoint then:

1. Publishes and directory-syncs `MANIFEST.active`, bound to the exact old
   WAL epoch, byte length, frame count, and BLAKE3 chain head.
2. Writes and syncs the first empty slot for the next WAL epoch.
3. Writes and syncs the second empty slot for that epoch.
4. Truncates the old WAL.
5. Syncs the WAL truncation.

Only then may compaction merge level 0 past the threshold of 4 into level 1
with overlapping level-1 segments. A prepublication refusal leaves the old
checkpoint authority in force. Once the new active manifest is published, a
later checkpoint failure poisons the handle. Recovery completes or verifies
the transition without losing acknowledged bytes. Format 1 uses its legacy
manifest and resets its disposable WAL-frontier shadow only after the WAL
truncation has synced. If that sync fails, the same shadow reset still makes
a same-handle retry safe; reopen rebuilds any unusable disposable shadow.

Under `ReclaimHistory`, compaction appends deletion evidence and the bound to
`retained`, syncs the stream, advances the authoritative retained frontier,
then publishes the history-floor WAL meta entry through the normal
WAL-sync/frontier-sync path. The in-memory floor advances last.
`servable_history_start` reports the floor once declared, otherwise the
oldest retained record. Outside the journal fault hook, the history record
rides the same atomic frame as the documents and ledger record, so
`journal_divergence` is structurally zero.

## Honest limits (recorded, not hidden)

- Merges are inline and caller-driven through `compact`, plus
  rotation-triggered flushes when two frozen tables queue. No
  background maintenance thread yet.
- Segment readers keep sparse indexes in memory and read record ranges
  on demand; `collect_range` materializes the merged view of the
  requested range per read. This matches the root store's in-memory
  tables; it is not a streaming merge iterator.
- MVCC keeps every version until a compaction declares a bound;
  snapshot reads below a declared floor are not served (the floor
  contract), but no snapshot-pin registry exists yet.
- No encryption-at-rest integration (PD-2026-08-27-01 lands separately),
  no compression, no Bloom or fence indexes, no multipart range reuse,
  and no block cache. ADR 0016 D-2 defers each choice to measured-workload
  receipts.
- The LSM-format backup/restore path (the backup module still stages
  through the root-file engine; restored locations open through format
  auto-detection) remains open work.
- The object-store tier's first landing
  (`src/storage/lsm/{object_store,tier}.rs`) proves the machinery: the
  checksum-addressed write-once `ObjectStore` contract with a local
  implementation, the `TierManifest` (epoch-chained, checksummed,
  temp/rename publication), the versioned explainable `TierMovePolicy`
  and `TierMoveReceipt`, a bounded mover pass (demote the coldest
  unpinned segment under budget pressure; dry runs decide without
  transferring; pins refuse), and the frozen fallback read order's local
  legs with a typed exhaustion error. Honest limits: manifest acceptance
  is local-durable until P2 wires `RegionalQuorum` member votes; the
  mover's fairness forecasting (per-tenant working sets, weighted
  queues) and the S3 REST client land with P2/P4; promotion-back and
  mixed-temperature splitting are follow-ups; the real S3 endpoint
  class is `unrun` on this host.
- The two-frame `Synced` protocol adds one small frame per synced commit.
  Format 2 also syncs one alternating frontier slot for each result-bearing
  append. The amplification bound test accounts for the flip frame.

## Evidence

- `src/storage/lsm/tests.rs` covers amplification, reopen recovery,
  idempotent replay, delete semantics, MVCC isolation, rotation and flush,
  conflict recording, compaction and history reclaim, torn-tail repair,
  format activation, every activation publication boundary, WAL and retained
  frontier loss or corruption, every activated checkpoint boundary,
  fail-closed manifest and segment loss, and engine integration.
- The focused storage and engine gates are
  `cargo test --lib --locked storage::` and
  `cargo test --lib --locked engine::tests::`; the complete
  library and integration suites remain the release gate. Root-store
  publication-mechanics suites pin `StorageBackend::RootFiles` explicitly
  because they verify that engine's format artifacts; the LSM has its own
  fault-order and recovery coverage.
- The old store remains fully functional and selected by its marker;
  no location is orphaned.
