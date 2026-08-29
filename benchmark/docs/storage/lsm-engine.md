# The LSM storage engine (P1, first landing)

Status: implemented behind the `StoragePort` seam as the default backend
for new locations; the full library suite (1053 tests) and every
integration suite pass against it. Owner: P1. Parents: ADR 0016 (design),
ADR 0015 (execution), charter PD-2026-08-28-05/06.

This record names the format, the publication order, the contracts the
engine keeps, and the honest limits of this first landing. The tier
manifest, mover, and cold-read fallback remain open P1 work recorded
below.

## Why

The W3-1 root-file engine rewrites a full tenant root per commit: a
recorded 512-write commissioning window appended 154,246,326 durable
bytes for ~300-byte documents — ~300 KB per write, unbounded by the
write set. The LSM engine bounds durable bytes per write by the write
set itself: one checksummed WAL frame carries the whole ordered commit
batch. The focused amplification test pins the bound below 3 KiB per
~300-byte document — two orders of magnitude under the baseline, by
construction.

## Layout

A location is one directory with a self-describing `format` marker
(`TBV2LSM1`):

- `wal` — append-only commit frames. One frame = one whole commit batch:
  document entries (puts and tombstones), the operation-ledger record,
  and the publication-history record, checksummed (FNV-1a) with a
  length-prefixed envelope.
- `seg-<id>.lsm` — immutable segments: 20-byte header, sorted records,
  a sparse index (one anchor per 32 records), and a verifying footer
  whose key lengths trail their bytes so it parses backwards from the
  crc.
- `MANIFEST` — the segment list, next segment identity, and WAL
  checkpoint, checksummed, published by the crash-safe
  temp-file/rename/dir-sync pattern.
- `retained` — the GAP-S1 retained-evidence store, reused unchanged.

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
→ build batch → WAL append → (`Synced`: fsync, then a small flip frame
proving the sync) → memtable apply → receipt.

The root identity becomes a chain: `fnv(previous head ‖ sequence ‖
digest)`, 32 canonical hex characters. It is verifiable by replay and
never rewrites a table.

Durability levels:

- `Synced` — frame appended and fsynced; a second small frame then
  upgrades the ledger record's proven durability. The flip frame is the
  analogue of the root store's post-publication `Published` record: its
  absence after a crash leaves an honest `Written` resolution.
- `Written` — frame appended, no fsync.
- `Buffered` — no document bytes; the ledger and history records still
  ride a durable frame (the old store's rule: resolution metadata
  persists for every level, so a reopened Buffered commit resolves
  instead of reporting `Pending`).

Fault mapping mirrors the root store's publication-order split:
`WalAppend`, `CommitMarkerApplication`, and `PointerPublish` fail before
the durable append (definite rejection, nothing applied);
`ReadyLedgerSync` fails the resolution-record sync; `PointerSync` and
`DirectorySync` interrupt the response of an already-durable commit
with their distinct `UnknownReason`s; `JournalAppend` skips the history
record, leaving the high-water gap consumers must observe.

## Recovery

Open scans the retained store (mid-file evidence damage fails closed),
reads the manifest, verifies its segments (a manifest-named segment
that does not verify rebuilds the segment set from the verifying files
on disk), replays the WAL above that state (idempotent by internal
key), restores the sequence watermark, and re-derives the history floor
— enforced once, over the fully rebuilt index.

WAL damage classification matches the root store's rule: a damaged
frame with bytes behind its declared end is mid-file corruption and the
open fails closed (`FormatMismatch`); only a trailing incomplete frame
— which must start with the frame magic, because appends write the
magic first — is the torn tail of an interrupted append and truncates.

Backend selection: `Engine::open` reads the location's format marker —
a `TBV2FORMAT` location opens on the root-file engine, everything else
on the LSM engine (the default for new locations). Restored root-format
locations therefore keep opening; no public API changed.

## Compaction

`compact` freezes the live memtable, flushes frozen tables into
level-0 segments, publishes the manifest, truncates the WAL the
checkpoint replaced, merges level-0 past the threshold (4) into level 1
with overlapping level-1 segments, and — under `ReclaimHistory` —
extracts the window's deletion evidence into the retained store,
declares the bound durably (a WAL meta entry), and advances the history
floor. `servable_history_start` reports the floor once declared,
otherwise the oldest retained record. `journal_divergence` is
structurally zero: the history record rides the same atomic frame as
the documents and ledger record.

## Honest limits (recorded, not hidden)

- Merges are inline and caller-driven through `compact`, plus
  rotation-triggered flushes when two frozen tables queue. No
  background maintenance thread yet.
- Segment readers keep sparse indexes in memory and read record ranges
  on demand; `collect_range` materializes the merged view of the
  requested range per read — parity with the root store's in-memory
  tables, not a streaming merge iterator.
- MVCC keeps every version until a compaction declares a bound;
  snapshot reads below a declared floor are not served (the floor
  contract), but no snapshot-pin registry exists yet.
- No encryption-at-rest integration (PD-2026-08-27-01 lands separately),
  no compression, no Bloom or fence indexes, no multipart range reuse,
  no block cache — each is a measured-workload decision ADR 0016 D-2
  defers to receipts.
- The tier manifest, Storage Tier Mover, object-store tier, cold-read
  fallback, and the LSM-format backup/restore path (the backup module
  still stages through the root-file engine; restored locations open
  through format auto-detection) remain open P1 work.
- The two-frame `Synced` protocol adds one small frame per synced
  commit; the amplification bound test accounts for it.

## Evidence

- `src/storage/lsm/tests.rs` — 30 focused tests: amplification bound,
  reopen recovery, idempotent replay, delete semantics, MVCC snapshot
  isolation, rotation/flush/manifest recovery, conflict recording,
  compaction checkpoint and history reclaim, torn-tail truncation,
  fail-closed foreign directories, engine integration.
- The full suite (1053 lib + 16 integration binaries) passes with the
  LSM engine as the default backend. Root-store publication-mechanics
  suites (`engine::tests` fault-order set, backup restore/schedule,
  sharding journal-coupled) pin `StorageBackend::RootFiles` explicitly:
  they verify that engine's format artifacts; the LSM proves its own.
- The old store remains fully functional and selected by its marker —
  no location is orphaned.
