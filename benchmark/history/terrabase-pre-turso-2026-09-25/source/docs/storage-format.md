# Storage format and recovery contract

Status: initial design and implementation brief, 2026-09-24. The first executable
increment is one local shard, local-durable acknowledgment, byte-key point/range
reads, and small atomic batches. Replicated-durable and object-durable writes,
cross-shard transactions, remote publication, advanced indexes, and online
physical-layout changes remain unimplemented until their stated barriers exist.
An API must reject an unsupported durability profile rather than weaken it.

## Authority and visibility

Each shard has one local commit sequencer and one logical ordered keyspace. A
`CommitSeq` is a shard-local, monotonically increasing `u64`; it is neither a
ULID, a wall clock, nor a cross-region causal order. A committed batch contains
all its key and metadata mutations. Its sequence becomes visible atomically
after the selected durability barrier. Failed admission consumes no sequence;
after an uncertain disk failure the shard is poisoned and must recover before
accepting another commit. The first increment does not promise multi-shard
atomicity or concurrent leaderless mutation semantics.

For a snapshot at sequence `S`, the value for key `K` is the record with the
greatest committed sequence `<= S`, including a deletion marker. A deletion
marker hides all older values. Point reads, scans, checkpoints, and eventually
all physical layouts use this same rule. Equal `(K, sequence)` records arising
from replay or migration must have identical bytes and type; disagreement is
corruption. Ordered range scans merge source iterators by key, select one
visible version per key, and return each key once. They must never expose only
part of an atomic batch. A snapshot pins its visibility cut and the generations
that can answer it; count, age, and bytes retained for snapshots are budgeted.
Expiry is an explicit error, not a silent move to a later cut.

Authoritative local state is the selected checksummed manifest generation, its
referenced immutable runs, and committed WAL records above its checkpoint
frontier. A synced WAL can temporarily be the only durable copy of a record.
The active mutable ordered index, decoded documents, hot records, mini-pages,
block cache, SSD object cache, filters, and derived indexes are replaceable
accelerators. A derived index cannot determine correctness without a verified
freshness frontier or a source fallback. Flush and tier transfer change where
versions reside, not their logical visibility. Do not keep an unbounded RAM
mirror of all keys or materialize an entire WAL during recovery.

## Versioned byte encodings

All persisted integers have specified little-endian encoding, all lengths are
checked before allocation or offset arithmetic, and no Rust struct layout or
pointer value is persisted. Unknown major versions and unknown mandatory flags
fail open. Every format has a magic, major/minor version, declared length, and
checksum. Readers enforce configured maxima before reading/decompressing;
writers never publish a format they cannot subsequently read. Format changes
need upgrade and rollback tests against actual files.

The first increment stores opaque byte keys and values. The later document
codec owns schema IDs, field typing, nested paths, numeric/collation semantics,
and canonical 16-byte ULIDs. It must be versioned independently from the
storage envelope so these semantics are shared by scans, indexes, and wire
contracts. Bytewise key order is the first increment's only order.

### Local WAL, version 1

The WAL is a sequence of bounded *complete batch* frames. One frame contains
one shard-local sequence and all operations in that batch, so a record is
atomic at the frame boundary. The v1 frame has an 8-byte magic, `u16` major,
`u16` flags, `u32` total frame length, `u64` sequence, 24-byte
`TransactionId` (`expires_at_unix_ms: u64` little-endian, then a 16-byte
nonce), `u32` operation count,
payload, a fixed 4-byte commit marker `TBOK`, and trailing `u32` CRC32C of
every preceding frame byte (including the marker). `total frame length`
includes all fields; its minimum is 60 bytes. The marker and CRC must both
validate. An operation
is `u8` put/delete tag, `u32` key length, `u32` value length, then key and value
bytes; delete requires zero value length. Duplicate keys within one batch are
rejected. Empty batches are rejected. The parser checks exact frame
consumption, positive key length, configured key/value/batch and operation
limits, contiguous sequence numbers after the checkpoint frontier, and CRC
before applying any operation. A future codec flag must identify its algorithm
and carry an uncompressed-size bound; v1 WAL frames are uncompressed.
The initial hard ceilings are 4,096 key bytes, 1 MiB value bytes, 1,024
operations, and 2 MiB complete encoded frame bytes; deployments may configure lower
limits. A mutation's key is opaque bytes. The byte tag is `0` for put and `1`
for delete.

Admission reserves the complete encoded batch, post-commit mutable-index
space, WAL/disk capacity, and a pending-write slot before append. One serialized
writer appends the frame and calls `sync_data` (or a documented stronger
equivalent). Only after sync succeeds does it publish the entire batch to the
mutable index and advance the visible/durable frontier and return a local
durable receipt. If apply fails after sync, the shard stops writes and recovers;
it must not report an ordinary abort for a possibly committed batch. Group
commit may sync several full frames together and release receipts only after
that sync. No acknowledgment depends on a background async flush.

At open, a valid final frame is replayed in sequence by bounded streaming
decode into the mutable index, flushing to new immutable runs whenever its
admitted budget fills. An incomplete final header or payload is an uncommitted
tail and can be truncated after recovery identifies the last valid offset. A
complete frame with a bad checksum, invalid length, or sequence gap is reported
as corruption; recovery must not silently discard a possibly acknowledged
commit. Corruption in the middle of a log is also fatal. A crash immediately
after WAL sync but before caller receipt may yield a committed, unacknowledged
batch; callers need an idempotency or receipt reconciliation contract above
this layer. Local durability assumes the SSD honors sync and the filesystem's
documented guarantees; a destroyed device is outside this profile.
Before any acknowledgment using a newly created or rotated WAL file, sync the
file, sync its parent directory, and durably publish the authority that names
or discovers it. `sync_data` on a newly created file alone does not guarantee
its directory entry survives a crash. The first local implementation uses a
stable WAL pathname, synced on initial creation, and does not infer authority
from an unsynced directory listing. Empty-open and rollover crash campaigns
must exercise this boundary.

The caller supplies a stable `TransactionId` for each attempted batch. The
identity scope is `(database, shard, TransactionId)`. Repeating the same ID
with the same canonical operation bytes returns the original receipt;
repeating it with different bytes returns `IdempotencyConflict`. The deadline
must be after the checked current time and no more than a configured maximum
window ahead. Checkpoint and WAL pruning preserve a durable, pageable
resolution index of `(ID, sequence, canonical payload digest)` for live IDs.
Before pruning expired entries, a checkpoint durably advances an
`expired_floor`; any ID whose deadline is at/below that floor, or has already
expired according to checked current time, returns `IdempotencyExpired`
without executing. A backward clock jump cannot resurrect a pruned ID;
a forward jump may expire a token early and must be observable.

`CommittedBatchCursor::next(after_seq, max_bytes, permit)` is the replication
handoff: it emits only complete synced WAL batches with sequence, transaction
ID, canonical operations, and digest. Cursor state and output are bounded and
budgeted. WAL segments remain pinned until readers have advanced or the
same committed bytes are copied and synced into an authoritative durable
outbox. A cursor cannot infer committed work from merely appended bytes.

### Immutable sorted run, version 1

A run is written to a new file and never modified after publication. Records
are sorted by `(key ascending, sequence descending)` and include tombstones.
The 36-byte v1 header is magic `TBRUN001` (8), major `u16=1`, flags `u16=0`,
block count `u32`, directory offset `u64`, directory length `u64`, and CRC32C
`u32` over the preceding 32 bytes. The build may rewrite this header while
the file is still unpublished. Each block is magic `TBB1` (4), record count
`u32`, stored length `u32`, plain length `u32`, payload, then CRC32C `u32`
over block header and payload. A v1 payload is a sequence of complete records:
key length `u32`, value length `u32`, sequence `u64`, kind `u8` (`0` value,
`1` tombstone), key bytes, value bytes. A tombstone has zero value length.
Blocks do not split a record; several blocks may contain versions of one key.
The plain block ceiling is 2 MiB, subject to lower configuration.

The directory is magic `TBDIR001` (8), block count `u32`, descriptors, and
trailing CRC32C `u32`. Each descriptor is min-key length `u32`, max-key
length `u32`, those key bytes, block offset `u64`, total block length `u32`,
and block CRC32C `u32`. The 24-byte footer is magic `TBEND001` (8), total run
bytes `u64`, directory CRC32C `u32`, and footer CRC32C `u32` over the preceding
20 bytes. All integers are little-endian. A reader validates file, directory,
descriptor, and block bounds before decoding; a point search starts at the
first block whose max key is at least the sought key. The directory is read
through bounded pages, not assumed resident in memory. For S3 range reads,
a trusted manifest
must also anchor a bounded immutable descriptor sidecar containing SHA-256
digests for each remotely fetched block. Verify the sidecar SHA-256 against the
manifest and each fetched range SHA-256 against the sidecar; local CRC32C alone
does not authenticate an untrusted partial response. A whole-object hash
alone does not verify a partial fetch. This remote authentication mechanism is
a required implementation gate, not a claim of the first increment.

The first increment uses uncompressed blocks; any later LZ4 block has an
explicit codec version and bounded plain length. Block and directory sizes are
bounded independently. If a run directory exceeds its resident budget, it is
paged from SSD and searched through a bounded upper-level index. A run does not require
all key offsets, values, or metadata to reside in RAM. Empty key ranges can
be skipped using trusted min/max bounds. Filters are optional hints only.

### Manifest and local publication, version 1

A manifest records generation, shard/ownership epoch, committed checkpoint
frontier, ordered run references with sizes and identities, WAL segment IDs
retained above the frontier, format versions, and required schema/index and
placement metadata as those features enter the engine. Each manifest is
checksummed; its references are immutable. Local publication writes and syncs
all new run files, writes and syncs a new manifest candidate, atomically
replaces a single local pointer or alternate-slot manifest, then syncs the
parent directory. Old runs and WAL files remain until the new manifest is
durable and no snapshot/backup/recovery reader needs them. Recovery selects
only a fully valid generation whose referenced runs exist and verify; it never
guesses the newest generation from directory listing order. An invalid newer
candidate does not erase the prior valid generation.

A checkpoint freezes an admitted mutable range through sequence `C`, streams
it to run files, verifies them, and publishes a manifest with frontier `C`.
Writes after `C` continue into another bounded active buffer and WAL segment.
Runs and retained WAL overlap safely because replay starts strictly above
the selected frontier. A crash before manifest publication leaves orphan runs
and the old authority; after publication it leaves a new authority and old
files that can be reclaimed later. WAL truncation/deletion occurs only after
that barrier and after snapshot and recovery retention checks. Compaction is
the same build-verify-publish-reclaim sequence, preserving every version needed
by an admitted snapshot or declared replication/backup frontier. If maintenance
cannot keep up within memory and SSD budgets, admission applies backpressure
or a capacity error before committed data is endangered.
The first local source increment retains its original WAL rather than pruning
it; this allows checkpoint generations to fall back safely after a crash and
preserves retry resolution during early development. Its configured SSD cap
may therefore reject further writes. WAL rotation, archival, and reclaim must
pass the stated durable-authority and crash tests before this cap is relaxed.
Before WAL append, local admission bounds the eventual run directory using at
most one descriptor per mutable version (16 fixed bytes plus 24 and two copies
of the key per version). It checkpoints before that bound exceeds the configured
directory cap; a single batch that cannot fit is rejected without a receipt.
The SSD headroom estimate includes the prospective run directory, run framing,
and pending manifest and expiry-floor files. Checkpoint rechecks both scratch
allowances before publication. These conservative bounds can trigger an earlier
checkpoint or capacity rejection than actual segment packing would require.

The retry-expiry high-water mark has a separate local authority: two fixed-size,
versioned, checksummed `floor.0` and `floor.1` slots. Both are durably created
before the first WAL acknowledgment. An advance writes and syncs `floor.pending`,
renames it over the older slot, and syncs the parent directory before publishing
the next manifest. Recovery validates both slots and their consecutive sequence
and nondecreasing floors. An interrupted first initialization may complete a
missing `floor.1` only when the locked directory contains exactly `shard.lock`
and a valid initial `floor.0` (sequence and floor both zero), before any WAL or
other shard file exists. The new slot and directory are synced before WAL
creation. A torn or corrupt first slot is not repaired. All other missing or
corrupt slots fail closed, as does a selected manifest whose floor exceeds this
authority. When recovery falls back to an older valid manifest/run generation,
it uses the authority's newer floor for retry rejection and WAL idempotency
reconstruction. A legacy
nonempty shard without these slots requires explicit migration; open will not
silently reset its floor.

On Unix, floor slots and `shard.lock` are opened without following a final
symlink and accepted only as single-link regular files; live hard-linked
authority files are rejected because they cannot provide independent copies.
This final-component check does not sandbox parent directory path components.

With `tb-storage/test-hooks` enabled in a test subprocess, setting
`TB_STORAGE_CRASH_POINT` to `after_wal_sync_before_apply`,
`after_run_sync_before_manifest`, or `after_manifest_sync_before_apply` exits
with status 86 at the named durability boundary without running destructors.

## Object tier target contract

The production object tier uses immutable segment, index, sealed WAL, snapshot,
and manifest objects. Object names carry content identity and format/generation
metadata. A remote manifest is authoritative only after every referenced
object is uploaded, read back or otherwise verified against TerraBase's own
checksums, and its pointer is conditionally published under a fenced ownership
epoch. The publication primitive must be verified against each provider and
RustFS; listing order, rename, ETag-as-hash, or last-writer-wins PUT is not a
substitute. If a provider lacks the necessary conditional operation, a proven
fenced metadata service is required before enabling this profile.

The published remote frontier must identify a complete checkpoint plus sealed
WAL tail and required schema, index, membership, and transaction recovery
metadata. Object-durable ACK waits until that frontier includes its batch.
Replicated-durable ACK waits for durable records on the configured named
failure domains. Received, local-durable, replica-durable, applied, and
object-archived frontiers remain distinct. Failure or uncertainty in upload or
pointer publication cannot be reported as an object-durable success. After
SSD loss, cold open reads the remote pointer/manifest, validates identity and
checksums, and hydrates only needed metadata and blocks. Orphan uploads and
old objects are collected only after durable reachability and grace/retention
proofs. The initial source increment deliberately rejects these profiles.

## Bounded resource contract

Set hard limits for batch operations/bytes, key/value bytes, mutable index,
frozen buffers, WAL replay buffer, run/block/index buffers, open iterators and
snapshots, concurrent I/O, SSD scratch, and total local bytes. Reserve before
materialization and count both active and waiting work. A scan yields a bounded
stream/page and releases its block buffers as it advances; it cannot allocate
all matching keys or values. Reads across `N` runs need at most bounded
per-run cursor state plus block buffers; cap `N` and trigger bounded merge or
backpressure. Dirty mutable entries cannot be evicted until included in a
synced WAL, and WAL cannot be reclaimed until a durable checkpoint covers it.
Track allocator bytes, RSS/PSS, filesystem cache, and bytes pinned by snapshots
separately. Measure actual headroom under the 4-vCPU/8-GiB Firecracker gate.

## First source increment and verification brief

`tb-storage` owns the shard-local WAL, ordered mutable index, immutable run
writer/reader, local manifest, recovery, snapshot selection, and a bounded
point/range/atomic-batch API. It uses `tb-core` IDs, sequence, snapshot,
receipt, acknowledgment profile, and errors. Its caller must select
`LocalDurable`; unsupported profiles return typed errors before a mutation.
The API takes explicit memory and SSD limits and exposes observable frontiers,
pressure, recovery, and corruption counters for Prometheus-compatible metrics.
No SQLite backend is involved.

Acceptance for this increment: process-level caller writes and restarts a
batch, point reads and short ordered scans agree at snapshot cuts, deletion
and overwrite choose one version, batches never tear across a crash at append,
sync, run flush, manifest publication, or WAL reclamation, and replay stays
within configured memory on a dataset larger than the mutable buffer. Reject
bad checksums, oversized lengths, unsupported versions/profiles, disk
capacity exhaustion, and excessive snapshots predictably. Run targeted Rust
tests with low-priority compilation and record source hashes. Benchmark
drivers/results belong only in `../ts-benchmark`; the full Firecracker,
RustFS, SQLite, and physical-strategy qualification remains a later gate.

## Research basis and limits

- Bf-Tree: Hao and Chandramouli, PVLDB 17(11), 2024,
  <https://www.vldb.org/pvldb/vol17/p3442-hao.pdf>; source revision
  `ca6130715eeba872682747852b5da2cde32838aa` from
  <https://github.com/microsoft/bf-tree>. Mini-pages selectively cache hot
  records and range gaps and buffer updates. The paper/source target modern
  SSDs and often small records. Its source documents optional WAL and an
  ABI/pointer-based snapshot; TerraBase does not adopt that persistence format.
- ZoneTree source revision `13ee11e19007301fdea72b9210de62f6257f4929`
  from <https://github.com/ZoneTree/ZoneTree>, especially
  `docs/concepts/segments.md`, `docs/storage/disk-segments.md`, and
  `docs/durability/wal-modes.md`: mutable/frozen/disk segments, ordered
  iterators, multipart disk organization, sparse indexes, cache, and merge
  scheduling inform the initial sorted-run design. Its async WAL mode does
  not qualify TerraBase's local-durable ACK. Neither project's performance
  results transfer to TerraBase; compare the physical strategies under equal
  durability and budgets in `../ts-benchmark`.
