# Family change payloads decision record (GAP-F02, ticket #72)

Status: **implemented** — awaiting inspector/warden gates and parent
integration. Direction was parent-adjudicated (ticket #72: SE-03 delivered
the Changes-API selector parity and journal replay but not payloads; this
packet adds the payload half on the W4-STREAM backbone). The vocabulary,
bounds, versioning rules, and per-kind dispositions below are this
packet's choices inside that direction. If the parent rejects a choice,
every changed path is inside this packet's write scope and reversible.

Owner: GAP-F02 (#72). Owns `src/streaming/payloads.rs`, the GAP-F02 test
section of `src/streaming/tests.rs`, and this record. Minimal additive
edits to shared files (recorded as collision actions): the `mod payloads;`
declaration and `pub use` list in `src/streaming/mod.rs`, the
`ChangeEvent` payload field/accessor and the mint call in
`src/streaming/subscription.rs`, and the `pub(crate) use` of the family
record decoders in `src/families/mod.rs`.

## Problem

The W4-STREAM/SE-03 change backbone delivers events that name *what*
changed and nothing more: a [`ChangeEvent`] carries the kind, tenant
scope, operation identity, storage sequence, and the changed key, and its
contract note says content-bearing payloads are fetched separately. V1's
Changes API is richer for the family kinds: a counter change carries the
counter's name and value
([v1:src/TerraBase.Client/Documents/Changes/ChangeNotification.cs::CounterChange],
ChangeNotification.cs:167-233 — `Name`, `Value`, `DocumentId`,
`CollectionName`, `ChangeVector`, `Type`), and a time-series change
carries the series name and the touched time range
([v1:src/TerraBase.Client/Documents/Changes/ChangeNotification.cs::TimeSeriesChange],
ChangeNotification.cs:244-326 — `Name`, `From`, `To`, `DocumentId`,
`ChangeVector`, `Type`). Attachment changes ride V1 document changes
(V1's closed change set has no attachment case —
[v1:src/TerraBase.Client/Documents/Changes/ChangeNotification.cs::DocumentChangeTypes],
ChangeNotification.cs:115-129), and the metadata shape V1 attaches to
that plane is content-addressed: `Name`, `Hash`, `ContentType`, `Size`
([v1:src/TerraBase.Client/Documents/Operations/Attachments/AttachmentName.cs::AttachmentName],
AttachmentName.cs:13-33, with the change vector and document identity in
[v1:src/TerraBase.Client/Documents/Operations/Attachments/AttachmentDetails.cs::AttachmentDetails],
AttachmentDetails.cs:10-24).

V2 family records already ride the journal as published documents of the
reserved collection, so the post-change record value is reconstructable
at event-expansion time — the same fact that makes journal replay
stateless. GAP-F02 derives bounded, typed payloads from that published
value. What it does **not** do is widen the change plane into a data
plane: no document body, no time-series values, no attachment bytes ever
ride an event.

## D1 — Vocabulary: one payload per family record write

The payload enum is closed and keyed by what the record *is*, not by
`FamilyKind` alone, because the reserved collection holds records of all
ten kinds plus the attachment content plane:

```rust
pub enum FamilyChangePayload {
    Counters(CounterChangePayload),      // family:counters/<document>
    TimeSeries(TimeSeriesChangePayload), // family:time-series/<document>/<name>
    Attachments(AttachmentChangePayload),// family:attachments/<document>
}
```

Each variant carries the family record's published document version
(`record_version`, D3), a `PayloadShape` (`Put` or `Delete`, mirroring
the journal write shape), and its typed fields:

- **Counters** — one entry per live counter name, ascending name:
  `{name, total}`. The `total` is the record's live value
  `sum(p) − sum(n)` over the per-writer buckets, the same derivation the
  family read performs (store.rs `counters_read`). This is V1
  `CounterChange.Name` + `CounterChange.Value` parity: V1 mints the
  post-change value at increment time
  ([v1:src/TerraBase.Server/Documents/CountersStorage.cs::PutOrIncrementCounter],
  CountersStorage.cs:439-447 — `Value = value`, the post-change total;
  `Type = exists ? Increment : Put`). Tombstoned names are not live and
  do not appear (the family read hides them the same way, D2 of the
  merge-encoding record).
- **TimeSeries** — a *segment summary*, not the values: `{name, arity,
  from, to, entry_count}` where `from`/`to` are the first and last entry
  timestamps of the published record and `entry_count` its entry count.
  This is V1 `TimeSeriesChange.Name` + `From`/`To` parity: V1's range
  names the span the write touched; V2's journal-derived payload names
  the span the *published record covers*. A `Delete` payload (the record
  was cleared) carries the parsed series name and no range.
- **Attachments** — one entry per stored attachment name, ascending name:
  `{name, content_type, size, hash}`. The `hash` is the content address
  (hex SHA-256, exactly 64 lowercase hex characters — the same shape the
  GAP-F01 content plane addresses bytes by), so a consumer can fetch the
  bytes through the content plane without any metadata lookup. This is
  V1 `AttachmentName` parity (AttachmentName.cs:13-33).

Every field is typed scalar or bounded text; there is no variant, field,
or constructor through which a `Document`, `DocumentValue`, raw byte
slice, or content segment can enter a payload. The restriction is
structural: the mint function (D4) is the only crate-internal producer
and it reads only the published record's typed fields.

### Per-kind disposition table

| Family kind | Record key | Payload | Ground |
| --- | --- | --- | --- |
| Counters | `family:counters/<doc>` | `Counters` | V1 `CounterChange` carries name + value |
| TimeSeries | `family:time-series/<doc>/<name>` | `TimeSeries` | V1 `TimeSeriesChange` carries name + range |
| Attachments (metadata) | `family:attachments/<doc>` | `Attachments` | V1 `AttachmentName` shape, content-addressed |
| Attachments (content plane) | `family:attachment-content/<digest>[/<i>]` | none — bytes never ride events | GAP-F01 plane is byte storage |
| Revisions | `family:revisions/<doc>` | none | V1 closed change set has no revisions case |
| CompareExchange | `family:compare-exchange/<key>` | none | same — no V1 change-payload counterpart |
| Identities | `family:identities/<name>` | none | same |
| ExpirationRefresh | `family:lifecycle/<doc>` | none | same |
| Archival | `family:archival/<doc>` | none | same |
| SchemaValidation | `family:validation/<collection>` | none | same |
| ConflictTombstones | `family:tombstones/<doc>` | none | same |

The seven payload-less kinds still produce events of their mapped
`ChangeKind` (today `Document`): the event is the notification, the
payload is the optional typed body, and a payload-less family event is
exactly the pre-GAP-F02 behavior. V1 parity ground for the payload-less
dispositions: the V1 change type set is exactly document, index, counter,
time-series, operation, database, and conflict
([v1:src/TerraBase.Client/Documents/Changes/ChangeNotification.cs::DocumentChangeTypes],
ChangeNotification.cs:115-156 — the `DocumentChangeTypes`/`IndexChangeTypes`
surface of that file); revisions, compare-exchange, identities, and the
remaining kinds have no V1 change-notification shape to mirror.

## D2 — Content restrictions and byte bounds

The change plane is a notification plane. The restrictions:

1. **No document bodies.** A user-collection write mints no payload
   (`Document`-kind events stay content-free — unchanged from SE-03).
   Family-record writes mint only the typed scalars and bounded text of
   D1. No `DocumentValue` crosses.
2. **No attachment bytes.** Content-plane records mint no payload at all;
   metadata payloads carry the digest, never the content.
3. **No time-series values.** The segment summary is fixed-size scalars;
   an unbounded series is summarized by count and span, not copied.
4. **Bounded entry lists.** At most
   [`MAX_CHANGE_PAYLOAD_ENTRIES`](D2-bounds) = 64 entries ride one
   payload. A record with more live names than the bound delivers the
   alphabetically-first 64, reports `complete = false`, and counts the
   omitted names in `omitted_entries`. Truncation is reported, never
   silent, and never refuses delivery: an over-wide record still gets its
   event.
5. **Bounded text.** Names and content types are at most 255 bytes,
   non-empty, free of control characters; a digest is exactly 64
   lowercase hex characters. The standalone constructors
   (`CounterChangePayload::try_new`, `TimeSeriesChangePayload::try_new`,
   `AttachmentChangePayload::try_new`) refuse an over-bound payload with
   `RequestValidationError::InvalidFamilyPayload` — the same typed
   refusal vocabulary the family payload constructors use — so an
   oversize payload cannot be constructed by any caller, only derived
   bounded by the mint.

Worst-case byte bound of one payload, stated arithmetically:

- Counter payload: 64 entries × (255-byte name + 8-byte `i64` total)
  + scalars ≈ **16.4 KiB** [integrator rider: 64 × 263 B = 16,832 B — gate NOTE corrected the decimal-kB mislabel].
- Attachment payload: 64 entries × (255 name + 255 content type + 8 size
  + 64 digest) + scalars ≈ **36.4 KiB** [integrator rider: 64 × 582 B = 37,248 B — gate NOTE corrected the decimal-kB mislabel].
- Time-series payload: fixed ≈ **0.6 KiB** (no per-entry data).

So one event's payload is bounded below **40 KiB**, and a maximal
1024-event batch carries below **40 MiB** of payload bytes in the worst
case — a consumer-facing memory fact recorded here because the batch
bound counts events, not bytes.

## D3 — Versioning story

Three layers, one per timescale:

1. **Event identity** (unchanged, frozen): operation identity + storage
   sequence identify a change; delivery is at-least-once and duplicates
   are identifiable. This is the replay layer.
2. **Record version**: every payload carries `record_version` — the
   published document version of the family record for a `Put`, and the
   version that existed at the deleted key for a `Delete` (`0` when
   nothing existed). This is V1's `ChangeVector` slot on a change
   notification (ChangeNotification.cs:78, :192, :269 carry it for
   document, counter, and time-series changes respectively), expressed
   as V2's per-record monotone version. A consumer applying payloads
   optimistically compares `record_version` exactly as it compares
   `ExpectedVersion` at the write seam, and successive writes to one
   record observe strictly increasing versions.
3. **Vocabulary compatibility**: the enum is closed and grows only
   additively — a new family payload variant or a new payload field is a
   new shape; existing field meanings never change; payloads stay
   bounded by D2's constants, which may only tighten for new fields.
   Unknown or absent payload is always expressible (`payload()` is
   `Option`), so an older consumer ignores a newer variant without
   dropping the event.

## D4 — Mint: derived at expansion, identical on both paths

`payloads::derive(&PublishedWrite)` is called from
`subscription::expand_commit_for` — the one expansion both the live
subscription scan and the journal replay share — so a replayed batch and
a live batch carry identical payloads for the same journal position.
The mint rules:

- Only writes to the reserved collection are considered; everything else
  mints `None`.
- A `Put` decodes the published record value through the family state
  decoders (`CounterRecord`, `TimeSeriesRecord`, `AttachmentRecord` —
  the same decoders the family read path uses) and projects the typed
  fields of D1. A record that fails to decode mints `None`: an
  undecodable reserved record still delivers its event (payload-less),
  failing open to the pre-GAP-F02 behavior rather than failing delivery.
  A decoded record with an entry whose fields would violate D2's text
  rules mints `None` for the same reason — the payload is derived, and a
  derived payload is either valid or absent, never sanitized.
- A `Delete` mints the payload kind's `Delete` shape with
  `record_version = version_at_delete` and no per-name entries (the
  journal's delete write carries the removed key, not the removed
  value, so per-name entries are not recoverable — recorded below).

## D5 — Includes–payload interaction

The includes contract (GAP-Q07, `docs/query/gapq07-includes-contract.md`)
and this payload vocabulary are two disjoint halves of one family-data
surface, and the boundary is by construction:

- An include is a **query-result** materialization: full family values
  (counter entries with totals, time-series entries with values) ride an
  [`IncludeSet`] beside query rows, bounded by the include constants.
- A change payload is a **notification** body: bounded summaries ride
  change events, bounded by D2, and never include document bodies,
  values arrays, or bytes.
- No type crosses: a query row or include set never carries a
  `FamilyChangePayload`, and a change event never carries an
  [`IncludeSet`] or an included value. The two planes share the family
  seam's typed decoders and nothing else.
- Interaction rule for consumers: the change payload tells a consumer
  *that* a family record changed, which record version it now has, and
  the bounded summary; the full values are fetched through the family
  seam or an include — exactly the V1 discipline, where a change
  notification wakes the client and the client loads data.

## Recorded limits and accepted differences

1. **No per-write deltas.** V1's `CounterChange.Type` distinguishes
   `Put` (new counter) from `Increment` (existing) and its delete change
   names the deleted counter, because V1 mints at write time with the
   pre-state in hand (CountersStorage.cs:439-447). V2's payload is
   derived from the published post-state alone — replay stays stateless
   — so the payload carries live totals per name and a `Put`/`Delete`
   shape only. A delta is the difference of two successive payloads for
   the same record, computable by the consumer, not asserted by the
   engine.
2. **Time-series range is the record's covered span**, not the touched
   subrange (same pre-state argument); V1's `Mixed` type (a write that
   both appends and deletes) is likewise not recoverable and maps to
   `Put`.
3. **Delete payloads carry no per-name entries** (the journal delete
   write carries the key and the prior version only).
4. **Tombstoned counter names do not appear** in a counter payload; a
   consumer tracking a name's disappearance compares successive
   payloads.
5. **Oversize entry lists truncate with a report** (`complete`,
   `omitted_entries`), never a refusal of the event (D2.4); the
   standalone constructors refuse oversize, so only the mint can produce
   a bounded truncation and it always reports it.
6. **Attachment payloads ride `Document`-kind events** because the
   closed V1 change-kind set (mirrored by `ChangeKind`) has no
   attachment case; the payload is reachable on the event regardless of
   kind through `ChangeEvent::payload`.
7. **Per-batch payload bytes are bounded only per event** (D2); the
   batch bound counts events, not bytes.
8. **A counter batch that deletes every live name does not delete the
   counter record**: the GAP-F07 tombstone discipline keeps the record
   present to fence stale writers, so the event is a `Put` of a record
   with zero live names. The counter `Delete` payload shape therefore
   arrives from record-level deletes — the engine delete seam, and the
   replication-apply/restore paths that produce them — not from the
   counter family's own batch seam. The time-series family, by contrast,
   clears its record when the last entry is deleted and no policy
   remains, so its family-native clear is the `Delete` shape (verified
   by test).

## Test-first evidence

RED first, then GREEN, both recorded from this packet's worktree under
the dev profile (`cargo test --offline --locked --lib streaming`).

**RED** — recorded before any implementation existed, against the
absent module: 19 compile errors, none of the ten GAP-F02 tests
runnable:

- `error[E0432]: unresolved imports crate::streaming::{AttachmentChangeEntry,
  AttachmentChangePayload, CounterChangeEntry, CounterChangePayload,
  FamilyChangePayload, PayloadShape, TimeSeriesChangePayload,
  MAX_CHANGE_PAYLOAD_ENTRIES}` (1 occurrence, 8 names)
- `error[E0599]: no method named payload found for struct ChangeEvent`
  (10 occurrences) and `... for reference &ChangeEvent` (8
  occurrences) — every round-trip and restriction assertion names the
  missing accessor.

**GREEN** — after the implementation: `test result: ok. 51 passed; 0
failed` at the recorded run (41 pre-existing streaming tests plus the
then-ten GAP-F02 tests; the clippy-repair pass later split the
constructor pair into two rows, so the merged suite carries eleven
GAP-F02 tests and the warden's dev-profile filtered run reads 58 passed
— 41 base + 11 GAP-F02 + 3 sharding + 3 sha256 streaming):

- `counter_change_payloads_round_trip_totals_through_replay_and_live`
- `time_series_change_payloads_round_trip_segment_summaries`
- `attachment_change_payloads_round_trip_content_addressed_metadata`
- `change_payloads_refuse_document_bodies`
- `attachment_content_records_never_mint_change_payloads`
- `change_payload_constructors_refuse_oversize_entry_lists`
- `change_payload_constructors_refuse_malformed_fields`
- `wide_counter_records_truncate_the_payload_with_a_reported_omitted_count`
- `change_payloads_carry_strictly_increasing_record_versions_and_delete_shapes`
- `malformed_reserved_records_deliver_payload_less_events`
- `payload_less_family_kinds_never_mint_change_payloads`

Coverage mapping: round-trip for every payload-bearing kind on both the
live subscription and the journal replay path (tests 1-3, live/replay
payload equality asserted); per-kind disposition over all seven
payload-less kinds through real family writes (test 10); document-body
and content-plane refusal (tests 4-5); oversize refusal at construction
and reported truncation through the mint (tests 6-7); version handling
including the delete shape and the version that existed at the deleted
key (test 8); fail-open delivery of malformed reserved records (test
9).

## Non-claims

- No claim that V2 change payloads are byte- or wire-compatible with
  V1's Changes API JSON: the vocabulary is typed Rust in-crate; the
  TBEP adapter owns any wire form.
- No claim of per-write counter deltas or touched-subrange time-series
  segments (limits 1 and 2 above).
- No performance claim; the payload mint is a bounded decode on the
  consumer's path, not on the commit path — the committing thread never
  runs it (delivery resolves batches on the consumer's thread, per the
  subscription design).

[`ChangeEvent`]: ../../src/streaming/mod.rs
[`IncludeSet`]: ../../src/query/includes.rs
[`MAX_CHANGE_PAYLOAD_ENTRIES`]: ../../src/streaming/payloads.rs

## Integrator receipts (post-merge, develop)

- Focused gate receipts reproduced green by both gates (the warden's
  filtered run: 58 passed / 0 failed, plus full-crate lib 819 passed and
  workspace `check` green).
- Full `scripts/check.sh` on merged develop: recorded in the merge wave's
  integrator receipt at push time (the packet worktree ran it green at
  `7d53a52`; the merged-tree run covers the AC-3 obligation).
