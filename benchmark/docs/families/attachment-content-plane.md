# Attachment content plane decision record (GAP-F01, ticket #71)

Status: **implemented** — awaiting inspector/warden gates and parent
integration. Direction was parent-adjudicated (ticket #71: the attachment
content plane gates GAP-F5's attachment snapshots and carries the
engine-side halves of PB-3007/3045 fidelity); the encoding, limits, and
error distinctions below are this packet's choices inside that direction.
If the parent rejects a choice, every changed path is inside this packet's
write scope and reversible.

Owner: GAP-F01 (#71). Owns `src/families/attachments/**`,
`tests/families/attachments_content_public.rs`, and this record. The one
`pub mod attachments;` declaration and this paragraph's counterpart in
`src/families/mod.rs`, and the one `mod` line in
`tests/families/main.rs`, are minimal additive edits to shared files
(recorded as collision actions).

## Problem

The attachments family of the typed seam records metadata only: a
`Store` mutation writes a name, content type, declared size, and declared
hash, and nothing in the engine holds the bytes those fields describe.
PB-3007 (attachments-backup-smuggler) and PB-3045
(family-backup-migration-errors) both require the engine to hold and
serve attachment bytes — a backup that restores metadata without bytes is
silent data loss (PB-3007 risk row) — and GAP-F5's attachment snapshots
need a content plane to snapshot. GAP-F01 builds it: byte storage, bounded
readers, range reads, disposal semantics, and size limits, typed at the
family seam.

## D1 — Encoding: digest-addressed content as reserved records

Content is stored under the reserved family collection exactly like every
other family value, split into bounded records under a kind key of their
own:

```text
family:attachment-content/<hex-sha-256>            → manifest record
family:attachment-content/<hex-sha-256>/<index>    → segment record
```

- The kind key `attachment-content` is distinct from the attachments
  metadata owner space (`family:attachments/<document>`), so a content
  record identifier can never equal a metadata record identifier however a
  document is named — the keys diverge before any owner text. Reserved
  record enumeration (`kind_of_record`, W4-CA-5) classifies these records
  as attachments-family records. The original packet keyed content as
  `family:attachments/content/...` inside the metadata owner space; the
  warden verified empirically that a metadata document named
  `content/<digest>/<index>` then collided with a segment record and was
  silently destroyed by staging and deletion, which this repair closes.
- As defense in depth, every segment identifier is read before the plane
  writes or clears it: a record that exists but does not decode as a
  segment (`codec::decode_segment`) fails closed as `ReservedStateInvalid`
  and is never touched, mirroring the manifest's existing decode guard.
- The manifest is `{digest, size, lengths}`: the whole-content digest, the
  whole-content size, and one byte length per segment, in order. A
  manifest whose lengths do not sum to its size, or whose digest disagrees
  with the record identifier, fails closed as `ReservedStateInvalid`.
- Each segment is at most `MAX_SEGMENT_BYTES` (64 KiB) of content, carried
  as padded RFC 4648 base64 inside one `DocumentValue::String`. A segment
  record's base64 therefore stays around 85 KiB of text, far below the
  storage layer's 1 MiB encoded-document ceiling, and every decode is
  bounded by construction.
- Content is digest-addressed and deduplicated: the plane computes the hex
  SHA-256 of the staged bytes itself and keys every record by it — V1's
  model, where the stored stream is keyed by the content hash and a second
  attachment with the same bytes shares it
  ([v1:src/TerraBase.Server/Documents/AttachmentsStorage.cs::PutAttachmentStream]).
  Staging identical content again is idempotent; the manifest is restaged
  so the caller's transaction still carries the content identity, which
  mirrors V1 writing the attachment row even when the stream is
  deduplicated.
- The plane verifies a caller-declared digest against the computed one and
  refuses disagreement (`DigestMismatch`) — the engine-side half of
  PB-3007's "transfer checksums attachment payloads".
- Every write rides the caller's Engine transaction (manifest staged last)
  and every read observes one immutable snapshot, so content inherits
  tenant isolation, atomicity, and durability from the family seam with no
  new storage format and no family-side commit.

Rejected alternative: a bytes-carrying `DocumentValue` variant. That
changes `src/types.rs` (banned shared file) and every codec that switches
on the value enum for one family's need. Rejected alternative: whole
content in one record — unbounded record size, unbounded decode, and it
breaks the bounded-reader contract.

## D2 — Bounded readers, range reads, disposal: V1-observable semantics

`FamilyStore::open_attachment_content` opens one
`AttachmentContentReader`: a snapshot-bound, read-only reader that decodes
at most one segment record at a time, so reading content of any accepted
size never materializes the whole content. There is no whole-content
accessor and no write path on the reader — V1 attachment streams are not
writable, and the typed surface keeps that by construction
([v1:src/TerraBase.Client/Documents/Operations/Attachments/AttachmentStream.cs::ThrowNotWritableStream]).

The V1 observable behavior this pins, with its pinned-commit citations
(all at `fa99b9257776dbe625e6a2388c74c9ce916637ee`):

| V1 behavior | V1 evidence | V2 typed form |
| --- | --- | --- |
| Range with a start at/past the size is unsatisfiable (416 → `InvalidAttachmentRangeException`) | [v1:src/TerraBase.Server/Documents/Handlers/Processors/Attachments/RangeHelper.cs::NormalizeRange]; [v1:src/TerraBase.Client/Documents/Operations/Attachments/GetAttachmentOperation.cs::OnResponseFailure] | `AttachmentContentError::RangeInvalid` at open |
| A missing `to` or a `to` past the end clamps to the last byte | [v1:src/TerraBase.Server/Documents/Handlers/Processors/Attachments/RangeHelper.cs::NormalizeRange] | resolved half-open `[from, size)` |
| A suffix `-X` resolves the last `min(X, size)` bytes; `-0` is unsatisfiable | [v1:src/TerraBase.Server/Documents/Handlers/Processors/Attachments/RangeHelper.cs::NormalizeRange] | `AttachmentRange::Suffix` |
| Any range over empty content is unsatisfiable | [v1:src/TerraBase.Server/Documents/Handlers/Processors/Attachments/RangeHelper.cs::ParseRange] (`length == 0` → null range) | `RangeInvalid` |
| Reads after disposal throw `ObjectDisposedException` | [v1:src/TerraBase.Client/Documents/Operations/Attachments/LimitedStream.cs::ThrowDisposedException]; [v1:test:test/TerraBase.SlowTests/Client/Attachments/AttachmentsStreamTests.cs#ShouldThrowOnDisposedStream] | `AttachmentContentError::Disposed`; `dispose()` is idempotent, drop disposes |
| A bounded reader reads at most its declared length; hitting the inner end first throws `EndOfStreamException` | [v1:src/TerraBase.Client/Documents/Operations/Attachments/LimitedStream.cs::ThrowEndOfStreamException] | `AttachmentContentError::Truncated` when staged bytes end before the manifest size |
| Reading to the end reports zero, repeatedly (end-of-stream is not an error) | [v1:src/TerraBase.Client/Documents/Operations/Attachments/LimitedStream.cs::Read] (`actualCount == 0` → `0`) | `read` returns `Ok(0)` at end |
| Seek past the declared length throws `ArgumentOutOfRangeException` | [v1:src/TerraBase.Client/Documents/Operations/Attachments/LimitedStream.cs::Seek] | `AttachmentContentError::PositionInvalid`, distinct from an unsatisfiable open-time range |
| Multi-attachment enumeration is ordered; skip and partial consumption are legal | [v1:test:test/TerraBase.SlowTests/Client/Attachments/AttachmentsStreamTests.cs#CanGetListOfAttachmentsAndSkip]; [v1:test:test/TerraBase.SlowTests/Client/Attachments/AttachmentsStreamTests.cs#CanGetListOfAttachmentsAndReadPartially] | independent readers per digest; readers may be dropped unread |

V1 HTTP-header behaviors that do not map to this seam are out of scope by
construction: the typed API has no raw `Range` header, so V1's
"ignore a multi-range or unparseable header and serve full content"
behavior ([v1:src/TerraBase.Server/Documents/Handlers/Processors/Attachments/RangeHelper.cs::ParseRange])
has no input to act on — a caller wanting the whole content passes no
range. This is recorded as a surface difference, not a parity claim.

Missing content is `ContentMissing`, the content-plane form of V1's
`AttachmentDoesNotExistException`
([v1:src/TerraBase.Client/Exceptions/Documents/Attachments/AttachmentDoesNotExistException.cs::ThrowFor]).

## D3 — Size limits are explicit and typed

V1 streams attachments of unbounded size through hash-addressed stream
storage; it has no per-attachment byte ceiling at the pinned commit. The
V2 plane starts bounded, per PB-3043's "over-limit requests return
explicit errors and memory remains bounded":

- `MAX_SEGMENT_BYTES = 64 KiB` — the per-record bound; a larger segment is
  rejected at payload construction (`InvalidFamilyPayload`).
- `MAX_CONTENT_BYTES = 8 MiB` — the staged-content ceiling; a larger total
  is the typed `AttachmentContentError::SizeLimitExceeded` at staging, so
  over-limit requests fail before any record is written.
- `MAX_SEGMENT_COUNT = 128 = ceil(MAX_CONTENT_BYTES / MAX_SEGMENT_BYTES)` —
  the canonical segment-count bound (warden repair). A payload with more
  segments is rejected at construction (`InvalidFamilyPayload`), so 8 MiB
  of one-byte segments cannot become millions of records and an unbounded
  reader index. Because 128 maximal segments are exactly the byte ceiling,
  every over-limit total is necessarily over the count bound too: the
  over-limit refusal an external caller observes is the construction-time
  validation failure, and the staged `SizeLimitExceeded` check remains as
  defense in depth for state the constructor cannot express.

This is an **accepted V1 difference** until the streaming packet's
transport work revisits it: raising or lifting the ceiling is a recorded
decision, never a silent one. Empty content is legal and round-trips —
V1's `ExportEmptyStream` case
([v1:test:test/TerraBase.SlowTests/Client/Attachments/AttachmentsSmuggler.cs#ExportEmptyStream]).

## D4 — Deletion and reference tracking

The plane exposes `stage_attachment_content_delete(digest)`, which removes
the manifest and segment records in the caller's transaction and reports
`ContentMissing` when nothing is staged for the digest in that view. The
plane does **not** count metadata references to a digest; the caller
(attachment metadata mutations, GAP-F5 snapshots, backup/restore) owns
reference tracking, exactly as V1's smuggler imports carry hash counts
across the boundary. Wiring reference-counted byte GC into the metadata
`Delete`/`Copy`/`Move` paths is a recorded handoff to the packets that own
those paths (`src/families/store.rs` is shared and outside this packet's
scope).

## What this record does not claim

- No PB-3007 or PB-3045 parity claim: both rows stay `unresolved` with
  empty Proof IDs; the smuggler/backup integration that would prove them
  belongs to later packets reading this plane.
- No recovery, replication, or benchmark receipts: content rides the
  existing transaction and snapshot machinery, and no
  performance-relevant claim is made. No local timing was measured.
- The base64 codec and the SHA-256 mirror are internal plumbing, not
  public surface; the SHA-256 copy exists because the two in-tree
  implementations (`src/backup/sha256.rs`,
  `src/families/migrations/digest.rs`) are private to modules this packet
  does not own, the same constraint SM-03 recorded. FIPS 180-4 known-answer
  tests pin all copies.

## Evidence

Focused tests, run red-then-green in this packet's worktree:

- Red: `cargo test --offline --locked --lib families::attachments` failed
  to compile with 18 errors (module, payloads, and store methods absent)
  before implementation.
- Green: 30 in-crate tests
  (`src/families/attachments/tests.rs` plus the digest known-answer
  tests) and 3 public construction tests
  (`tests/families/attachments_content_public.rs`) pass under the pinned
  toolchain; full check results are in the packet report.

## Continuation repairs (2026-08-27)

The finish protocol was completed by a continuation agent after the
original builder died on an infrastructure error mid-protocol. The
adoption re-verified every V1 citation above against the pinned commit
and found two defects, both fixed red-then-green:

- The base64 decoder's canonical-trailing-bits check was vacuous: its
  mask covered the padding sextet positions (always zero) instead of the
  final data sextet's unused low bits, so non-canonical encodings such
  as `QR==` and `QUC=` were silently accepted despite the fail-closed
  claim. The mask now covers bits `6*p .. 8*p` of the group for `p`
  padding characters; `non_canonical_base64_trailing_bits_fail_closed`
  pins it. No engine data was affected — the plane's own encoder is
  canonical by construction.
- Staging a write whose every segment is empty (an empty
  `AttachmentSegment` is structurally legal) wrote a manifest with
  `size = 0` and a non-empty `lengths` list, which the decoder's
  canonical-manifest invariant rejects — state the plane itself could
  not read back, and which broke a later empty-content stage through the
  dedup path. Staging now drops empty segments before building the
  manifest, so empty-segment writes stage as the canonical empty content;
  `empty_segments_stage_as_canonical_content` and
  `empty_segments_among_bytes_do_not_change_content` pin it.

## Warden repair (2026-08-27)

The warden loop verified the packet's findings empirically and every
repair below was made red-then-green:

- **Content/metadata record-id collision (MAJOR).** Content records were
  keyed `family:attachments/content/<digest>[/<index>]` inside the
  attachments metadata owner space, so metadata stored on a document named
  `content/<digest>/<index>` shared a record identifier with a segment:
  staging blindly overwrote the metadata record (`ExpectedVersion::Any`
  put) and content deletion blindly cleared it. Repaired twice over —
  content records moved to the distinct kind key
  `family:attachment-content/...` (see D1), and every segment identifier
  is now read and decode-guarded before any write or clear, failing
  closed as `ReservedStateInvalid` on foreign values.
  `metadata_on_a_content_shaped_document_survives_content_operations`
  reproduces the warden's probe and
  `foreign_values_on_segment_ids_fail_staging_and_deletion_closed` pins
  the decode guard;
  `content_records_classify_for_snapshot_enumeration` pins the
  `kind_of_record`/`snapshot_family_records` coordination the kind-key
  move requires.
- **No segment-count bound (MINOR).** 8 MiB of 1-byte segments could
  stage up to 8,388,608 records with a 64 MiB reader index.
  `MAX_SEGMENT_COUNT` (D3) caps the accepted count at 128, rejected at
  payload construction; `segment_count_is_capped_at_the_canonical_maximum`
  and the public limits test pin it. The over-limit byte test moved to
  the construction-time refusal the bound makes canonical.
- **SHA-256 pad-crossing coverage (MINOR).** The known-answer vectors
  never covered final remainders of 57..=63 bytes, the one padding shape
  where the pad byte spills into an extra block.
  `sha256_pad_crossing_final_remainder_known_answers` adds lengths
  57..=64 with digests derived independently of the implementation
  (Python 3 hashlib / OpenSSL); the vectors passed on arrival — the
  implementation was already correct, the defect was the absent vectors.
- **Redundant rustdoc link (MINOR).** The `[`attachments`]` link in
  `src/families/mod.rs` carried an explicit intra-doc target rustdoc
  reports redundant; the explicit target was dropped.
