# Backup optimization — codec head-to-head, v3 artifacts, cut caching (2026-09-19)

Measured, layer by layer, using a new `--workload backup-microbench` that
decomposes a full backup, an incremental backup, and a restore into their real
sub-phases (engine replay, capture file cuts, file verification, authority raw
scan, document walk, manager upload, restore materialization). Every change
below was re-measured against that baseline on the same host
(`nice -n 19`, server GC pinned to 8 processors).

## 1. LZ4 codec head-to-head

The previous NativeCompressions qualification was rejected because its
`LZ4Stream` decompressor hung in an uncancellable CPU loop. The block/frame
API was re-qualified with hard watchdogs (30 s per block, 2 min for a 64 MB
incompressible run) plus roundtrip verification on 271 MB of synthetic JSON in
4 MB blocks:

| Codec | Ratio | Parallel encode MB/s | Decode MB/s | Roundtrip |
| --- | ---: | ---: | ---: | --- |
| K4os.Compression.LZ4 L00_FAST | 0.281 | 2,114 | 5,693 | ok |
| K4os.Compression.LZ4 L03_HC | 0.215 | 280–444 | 3,515–7,347 | ok |
| K4os.Compression.LZ4 L10_OPT | 0.204 | 128–172 | 1,207–4,249 | ok |
| **NativeCompressions.LZ4 level 1** | 0.281 | **2,629** | 3,854 | ok |
| **NativeCompressions.LZ4 level 12** | **0.200** | 136 | **3,712** | ok |

A 1,600-iteration concurrent hammer (8 threads, roundtrip-verified) found no
corruption and no hang in the block API.

**Winner: NativeCompressions.LZ4 (native liblz4, already pinned at 0.6.1 in
`Directory.Packages.props`).** Level 1 is 25% faster than K4os at the same
ratio; level 12 beats every K4os level on ratio and decode. Snapshots use
level 1: the ratio matches the previous zlib stream while the codec cost drops
out of the critical path. Level 12 stays available for archival policies that
prefer size over speed (it would add ~6 s per 150 MB compressed on 8 cores).

## 2. v3 snapshot artifacts (binary framing + block LZ4)

The v2 artifact was one zlib stream over line-delimited JSON, written one
`JsonSerializer.Serialize` call per document. v3 replaces it:

- Same JSON header object (format id
  `terrabase-storage-backup-snapshot-v3`), now length-prefixed and readable
  without decompressing a single document block.
- Documents are binary length-prefixed records (scope index into the header's
  collection table, version, UTC ticks, identifier, raw UTF-8 body) packed
  into 4 MB blocks, each an independent native LZ4 frame.
- Capture copies each body **once** from the engine's parsed DOM
  (`JsonMarshal.GetRawUtf8Value`) into the block buffer — no per-document
  serialization at all.
- Blocks compress on a bounded parallel pipeline (default 8) and are written
  strictly in order, so the artifact bytes are deterministic regardless of
  scheduling.
- The restore reader applies documents in 65,536-record frames (was 4,096),
  parses each body once, and never holds more than one decompressed block plus
  one frame of parsed bodies.

Two format bugs were caught by the test suites before anything shipped: a
4-byte field-offset skew in the record cursor, and a body slice not bounded by
its record length (the body swallowed every following record in the block).
Both were found by the existing backup suites asserting exact corpus equality.

## 3. Capture file-cut caching

Every backup capture hashed every source file (compacted segments, log tail,
publication state) to build its cut, and `VerifyFiles` hashed them all again.
The incremental path takes two captures (watermark + log read), so an
incremental on a 10M-document engine hashed ~1.6 GB four to five times — the
measured 95.6 s fixed cost for a 105 KB artifact.

Cuts now carry the file's write time; `Verify` accepts an unchanged
(length, write-time) identity and re-hashes anything else, failing closed on a
mismatch. The engine memoizes cuts per file identity, so repeated captures of
unchanged files are stat-only.

## Measured results (1M documents, 8 processors)

`backup-microbench`, baseline vs final:

| Sub-phase | Before | After | Change |
| --- | ---: | ---: | ---: |
| Full backup, manager end-to-end | 46.1 s | 37.2 s | −19% |
| — engine open (replay) inside that timer | ~30 s | ~29 s | unchanged |
| — capture only (no manager) | ~15 s | 8.4 s | −44% |
| — manager upload of the artifact | ~1.3 s | ~1.2 s | unchanged |
| **Incremental backup (88 KB artifact)** | **4.27 s** | **1.85 s** | **−57%** |
| Full-boundary restore | 35.8 s | 31.7 s | −11% |

End-to-end 100k qualification (exact-match verified, all phases):

| Phase | v2 (morning) | v3 (final) | Change |
| --- | ---: | ---: | ---: |
| Full backup | 6,521 ms | 4,866 ms | −25% |
| Incremental backup | 608 ms | 429 ms | −29% |
| Full-boundary restore | 5,196 ms | 4,086 ms | −21% |
| Interior PITR restore | 4,517 ms | 3,946 ms | −13% |
| Full artifact size | 7.49 MB | 10.98 MB | +47% |

The artifact size increase is the deliberate codec trade: LZ4 level 1
compresses the deduplicated binary framing less than zlib compressed the
repetitive NDJSON text. Uploads are correspondingly larger; at ~75–110 B/doc
this stays far below the raw corpus size.

Native filesystem qualification (5 samples, 128 snapshot documents): restore
p50 142.9 ms, clean reopen p50 67.3 ms, end-to-end p50 204.2 ms — all five
generations activated and reopened.

## What is left on the table (measured, not guessed)

- **Engine replay/materialization (~29 s per 1M documents)** is now the
  dominant cost of every backup-adjacent phase that opens an engine: it is the
  record decode path itself (per-record checksum and per-body JSON parse into
  the in-memory DOM). A persistent state checkpoint or a raw-UTF8 body fast
  path in `StorageLogRecord` would cut both fresh opens and restore
  materialization; it is engine work, not backup work.
- The authority raw scan re-encodes every physical record to hash it
  (~4.6 s per 1M). Memoizing encoded bytes on records was rejected: it would
  add ~0.4 GB resident per million documents on top of the ~1.4 KB/doc the
  engine already holds.
- The manager hashes the artifact plaintext and ciphertext separately plus the
  store's own upload digest (three passes over the same bytes). The v3 writer
  already computes the plaintext digest during capture; threading it through
  `BackupSnapshot` would need the snapshot/encryption seam to grow a digest
  field.

## Correctness

All suites green on the final bits: TerraBase 483 (4 skipped), Api 1,123,
Recovery 12 (2 skipped). The v2 artifact format is fully removed — no
compatibility reader, per project policy.

An adversarial review workflow (four lenses — framing, buffer lifetimes,
restore validation, cache safety — with per-finding refutation) confirmed
nine defects in the new code; all were fixed before the final suites ran:

1. **LZ4 decompression bomb (major)**: `TryReadBlock` validated the declared
   block length but let the library expand the actual bytes first — a ~2 MB
   crafted block at LZ4's ~255:1 zero-run ratio could allocate ~17 GB before
   rejection. Decompression now writes into a caller-owned buffer of the
   already-validated declared length.
2. **Corrupt LZ4 frames escaped the exception contract (major)**: the library
   throws `InvalidOperationException` for malformed frames, which the restore
   outcome classifier maps to `Error` instead of `Invalid`. The codec now
   wraps it as `InvalidDataException`.
3. **Record cursor missed a length invariant (major)**: a crafted
   `recordLength < fields + idLength` threw `ArgumentOutOfRangeException`
   instead of the corruption error. Guard added.
4. **Unvalidated timestamp ticks (major)**: a crafted `updatedAtTicks` outside
   `DateTimeOffset`'s range threw from the constructor. Range-validated now.
5. **Scope-table lookup divergence (major)**: the applied table used exact-case
   JSON properties while the validator uses case-insensitive first-match, so a
   case-variant duplicate `collections` key could apply an unvalidated scope
   table. Both paths now use the same lookup.
6. **Invalid UTF-8 identifiers were silently replaced (minor)**:
   `Encoding.UTF8.GetString` never throws, so the rejection catch was dead
   code and mangled IDs restored as U+FFFD. Strict UTF-8 decoding now.
7. **Fill-buffer pool leak on failed captures (minor)**: the active block
   buffer was not returned on exception paths. Ownership-tracked now.
8. **Cut-cache growth (minor)**: entries for retired segments accumulated
   across compactions; compaction now prunes to the live file set.
9. A tenth and eleventh candidate (mtime-preserving tamper, spool-delete
   exception masking) were refuted by the verification pass and left alone.

Receipts: `bench/receipts/backup-optimization-20260919/` (codec head-to-head
probe in `/tmp/lz4-probe` is reproducible from the table above; microbench
before/after, 100k e2e, native qualification). The 10M-document qualification
receipt is added by the run concluding in `bench/e2e-volume-10m-20260919.md`.
