# PS-03 families parity sweep: PB-3000..PB-3045

Status: row-by-row verification note for Forgejo ticket TerraScale/TerraBaseV2#55
(PS-03), recorded 2026-08-26 against the current `develop` tree (head
`d8569bc`, with parallel-wave uncommitted edits under `src/streaming/`,
`tests/streaming/`, `parity/v1-test-gaps.json`, and untracked
`docs/sharding/`, `docs/adr/0011-sharding-model.md`,
`docs/parity/ps-02-query-sweep.md`, `spike/`, `studio/` from sibling waves;
none of those were touched by this sweep). Documentation only: no parity
status, disposition, Proof ID, overlay, `parity/contracts/`, `CONTEXT.md`,
or code change was made. All 46 rows remain `unresolved` with empty Proof ID
lists (`parity/overlays/PB-3000.json`..`PB-3045.json`); this sweep changes
no row's status.

Authoritative row source: `parity/contracts/families.json` (46 items,
PB-3000..PB-3045) at the pinned V1 source identity
`fa99b9257776dbe625e6a2388c74c9ce916637ee`. Primary V2 evidence: the
accepted W3-F family seam under `src/families/` (`mod.rs`, `payloads.rs`,
`state.rs`, `store.rs`, `tests.rs`), its implementation record
`docs/families/w3-families.md`, and the adjacent seams that carry family
evidence today: `src/streaming/` (W4-STREAM), `src/replication/`
(W4-REPLICATION), `src/backup/` and `src/migration/` (declaration-only
W4-BACKUP feed), `src/query/` + `src/index/` (W3-2, no family sources), and
`src/adapters/` (protocol forwarding; Platform stub).

## Verification vocabulary

- **seam-implemented**: the row's engine-side observable semantics are
  implemented and focused-tested at the family seam in V2 form. Residual
  deltas against the V1 contract are enumerated in the row section. Never a
  V1 parity claim — W3-F itself states all rows stay unresolved
  (`docs/families/w3-families.md:334-352`).
- **seam-partial**: a material subset of the row's semantics exists at the
  family seam (usually typed state/config plus part of the behavior); the
  execution, scale-out, or payload half is missing.
- **cross-seam-partial**: an adjacent seam (streaming, generic document
  replication, the backup/migration feed) carries partial or inherited
  evidence; the row's own family-specific contract is mostly unmet.
- **absent**: no engine-side implementation or test evidence in this tree.
  Absence is a fact about the tree, not a disposition: wave-owned rows are
  absent because their wave has not landed or is `proposed`.
- **platform-owned**: the engine intentionally holds no such behavior by
  charter decision and the row belongs to the TerraScale Platform.

## Wave ownership cross-reference (checked before flagging gaps)

The first-owner mapping is `docs/design/w3-core-contract.md:1901-1918`:

| Wave / packet | Tickets | Family rows |
| --- | --- | --- |
| W3-F Families (accepted) | — | PB-3000, 3001, 3003, 3005, 3009, 3015, 3016, 3019, 3024, 3025, 3026, 3029, 3030, 3031, 3033, 3036, 3038, 3039 |
| W3-2 Query/Index (landed, no family sources) | — | PB-3010, 3020, 3040 |
| W4-STREAM (landed) | — | PB-3002, 3014, 3017, 3023, 3041, 3043 |
| W4-REPLICATION (landed, generic documents only) | — | PB-3004, 3006, 3011, 3012, 3018, 3021, 3027, 3028, 3032, 3034, 3035, 3037, 3044 |
| W4-BACKUP (not landed; declaration-only `src/backup/`, `src/migration/`) | BK wave #42-#45 | PB-3007, 3013, 3022, 3045 |
| SH sharding | SH wave #47-#51 | PB-3006, 3012, 3044 (routing halves); ADR 0011 `proposed`, no code |
| SM schema/migration | SM wave #26-#28 | PB-3036 (residual enforcement), 3037 |
| W5 adapters / Platform | — | PB-3008 (remote storage), PB-3042 (Platform-owned per PD-2026-08-26-11) |

Cross-sweep reference: `docs/parity/ps-02-query-sweep.md` already routes the
query-side family rows (its GAP-Q19 covers counter/time-series index source
types, PB-4058) and PB-4016 (family includes). This sweep cross-references
rather than duplicates those gaps.

## Summary table

Verdict describes the current tree; disposition proposes routing. All rows
stay `unresolved`.

| Row | Name | Class | Verdict | Disposition (proposed) |
| --- | --- | --- | --- | --- |
| PB-3000 | attachments-crud | optional | seam-partial | GAP-F1 (content plane) + keep W3-F CRUD evidence |
| PB-3001 | counters-crud | optional | seam-partial | GAP-F7 (change vectors/metadata flags) + W3-F evidence |
| PB-3002 | attachments-range-stream | optional | cross-seam-partial | GAP-F1 (attachment bytes/ranges) |
| PB-3003 | attachments-batch-session | optional | seam-partial | GAP-F1; composition evidence kept |
| PB-3004 | attachments-replication-conflict | optional | cross-seam-partial | GAP-F7 (family replication semantics) |
| PB-3005 | attachments-revisions | legacy | absent | GAP-F5 (attachment revision snapshots) |
| PB-3006 | attachments-sharding | optional | absent | SH wave #47-#51 (ADR 0011 proposed) |
| PB-3007 | attachments-backup-smuggler | optional | cross-seam-partial | BK wave #42-#45 + GAP-F1 (bytes) + GAP-F8 |
| PB-3008 | attachments-remote-storage | optional | absent | W5 adapter wave; blocked on secret-handling contract |
| PB-3009 | counters-batch-transaction | optional | seam-implemented | Keep W3-F evidence; GAP-F7 for cluster tx |
| PB-3010 | counters-query-index | optional | absent | GAP-F3 / PS-02 GAP-Q19; TQL+IX waves |
| PB-3011 | counters-replication-conflict | optional | cross-seam-partial | GAP-F7 (counter merge vs record LWW) |
| PB-3012 | counters-sharding-tombstones | optional | cross-seam-partial | SH wave #47-#51 + GAP-F7 (tombstone lifecycle) |
| PB-3013 | counters-backup-smuggler | optional | cross-seam-partial | BK wave #42-#45 + GAP-F8 |
| PB-3014 | counters-changes-subscriptions | optional | cross-seam-partial | GAP-F2 (family change payloads) |
| PB-3015 | counters-errors-overflow | optional | seam-implemented | Keep W3-F evidence; adapter error mapping note |
| PB-3016 | time-series-append-delete | optional | seam-implemented | Keep W3-F evidence |
| PB-3017 | time-series-range-stream | optional | seam-partial | GAP-F4 (paging/offsets) + streaming evidence |
| PB-3018 | time-series-incremental | optional | absent | GAP-F4 (incremental series) |
| PB-3019 | time-series-rollup-retention-config | optional | seam-partial | GAP-F4 (generation + cleanup execution) |
| PB-3020 | time-series-query-index | optional | absent | GAP-F3 / PS-02 GAP-Q19; TQL+IX waves |
| PB-3021 | time-series-replication-tombstone | optional | cross-seam-partial | GAP-F7 (segments/deleted ranges) |
| PB-3022 | time-series-backup-smuggler | optional | cross-seam-partial | BK wave #42-#45 + GAP-F8 |
| PB-3023 | time-series-changes-subscriptions | optional | cross-seam-partial | GAP-F2 (include-ranges) |
| PB-3024 | time-series-errors | optional | seam-implemented | Keep W3-F evidence; adapter error mapping note |
| PB-3025 | revisions-crud-range | legacy | seam-partial | GAP-F5 (revisions completeness) |
| PB-3026 | revisions-config-bin-retention | legacy | seam-partial | GAP-F5 (collection policies, bin cleaner) |
| PB-3027 | revisions-conflicts-tombstones | legacy | absent | GAP-F7 (conflict states) |
| PB-3028 | revisions-replication-transfer | legacy | absent | GAP-F7 + replication receipts |
| PB-3029 | compare-exchange-conditional | admin | seam-partial | GAP-F10 (scope decision) + W3-F evidence |
| PB-3030 | compare-exchange-batch-expiration | admin | seam-partial | GAP-F10 (batch/cleanup) |
| PB-3031 | identities-hi-lo-seeding | optional | seam-partial | GAP-F9 (Hi-Lo distribution) |
| PB-3032 | identities-sharding-recovery | optional | absent | GAP-F9 + SH wave |
| PB-3033 | expiration-and-refresh | optional | seam-partial | GAP-F6 (expiry/refresh execution) |
| PB-3034 | data-archival | optional | seam-partial | GAP-F6 (archival execution) |
| PB-3035 | data-archival-interactions | optional | absent | GAP-F6 (interaction contracts) |
| PB-3036 | schema-validation-config-errors | optional | seam-implemented | Keep W3-F evidence; SM wave #26-#28 + GAP-F12 |
| PB-3037 | schema-validation-index-sharding | optional | seam-partial | SM wave #26-#28 + SH wave + GAP-F12 |
| PB-3038 | family-conflicts-tombstones | legacy | seam-partial | GAP-F7 (cleanup + automatic recording) |
| PB-3039 | family-transactions-batch | core | seam-implemented | Keep W3-F evidence; GAP-F7 for replication marker |
| PB-3040 | family-query-index | core | absent | GAP-F3 / PS-02 GAP-Q19; TQL+IX waves |
| PB-3041 | family-changes-subscriptions | core | cross-seam-partial | GAP-F2 (per-family payloads) |
| PB-3042 | family-authorization | protocol | platform-owned | PD-2026-08-26-11: Platform-only; GAP-F11 records disposition |
| PB-3043 | family-size-stream-range | core | cross-seam-partial | GAP-F1 + GAP-F4 (limits/positioning) |
| PB-3044 | family-replication-sharding | core | cross-seam-partial | SH wave #47-#51 + GAP-F7 |
| PB-3045 | family-backup-migration-errors | core | cross-seam-partial | BK wave #42-#45 + GAP-F8 |

Count: 46 rows. Verdicts: 6 seam-implemented, 14 seam-partial,
14 cross-seam-partial, 11 absent, 1 platform-owned. Every row's
authoritative status stays `unresolved`.

---

## Row sections

Paths are repository-relative. Line numbers were verified against the
working tree on 2026-08-26.

### PB-3000 attachments-crud — seam-partial

Implemented at the family seam as logical metadata:

- Closed mutation set `AttachmentMutation::{Store{replace}, Delete, Rename,
  Copy, Move}` (`src/families/payloads.rs:154-214`); staging validates
  names/content type/digest at construction (`src/families/payloads.rs:238-261`)
  and duplicate names without `replace` are the typed
  `AttachmentNameDuplicate` (`src/families/store.rs:388-391`, rename target
  `408-411`); rename re-keys in place (`src/families/store.rs:404-412`);
  copy/move handle same-document merge as one record write
  (`src/families/store.rs:419-431`) and cross-document staging with a second
  guarded record (`src/families/store.rs:432-458`).
- Reads enumerate metadata per document in name order
  (`src/families/store.rs:349-374`; `AttachmentMetadataResult::entries`
  ordered by name, `src/families/payloads.rs:360-383`).
- Focused tests: `attachment_metadata_round_trip_is_tenant_scoped`
  (`src/families/tests.rs:413`),
  `attachment_rename_copy_and_duplicate_names_are_typed`
  (`src/families/tests.rs:2444`),
  `same_document_attachment_copy_and_move_commit_as_one_record`
  (`src/families/tests.rs:2832`); W3-F per-row note
  (`docs/families/w3-families.md:215`).

Deltas against the row contract: metadata carries name, content type, size,
hash, and document identity only — no change vector, no attachment-level
version, no shared-stream/duplicate-stream observation; attachment content
is explicitly metadata-only with no bytes, streams, or chunked transport
(`docs/families/w3-families.md:314-315`). Entity-based (session) attachment
binding is a V1 client concept absent here.

Disposition: keep the W3-F CRUD evidence as the base; route the content
plane to GAP-F1. Not resolvable while attachment bytes do not exist.

### PB-3001 counters-crud — seam-partial

- Signed 64-bit values, increment-by-delta creating from zero, delete, and
  one/many/whole-group reads with missing counters reported as `None`
  (`src/families/store.rs:468-493`; `CounterOperation`
  `src/families/payloads.rs:455-479`; `CounterEntry::value -> Option<i64>`
  `src/families/payloads.rs:566-590`).
- Counter mutations never create revisions (only
  `RevisionCommand::ForceCreate` pushes revisions,
  `src/families/store.rs:670-675`).
- Focused test: `counter_crud_preserves_signed_values_and_missing_errors`
  (`src/families/tests.rs:484`); tenant scoping
  `family_scope_rejects_cross_tenant_reads_and_writes`
  (`src/families/tests.rs:1553`).

Deltas: no change vectors returned with values; no document
metadata-flag observation (V1's `@counters` metadata flag has no V2
counterpart); counter-name casing is exact-key in a `BTreeMap` with no
V1-style casing normalization claim.

Disposition: W3-F evidence is the CRUD base; change-vector/metadata-flag
parity rides the replication/payload work in GAP-F7.

### PB-3002 attachments-range-stream — cross-seam-partial

The streaming seam provides a generic family stream: one
`FamilyStreamSelector` wraps a single `FamilyReadRequest`
(`src/streaming/mod.rs:533-571`) and the cursor records "the single family
read has been served" (`src/streaming/sources.rs:64-66`) — one bounded read,
no multi-batch position. For attachments that read returns metadata only
(`src/families/store.rs:349-374`), so the row's core observables —
whole-attachment byte reads, bounded byte ranges, async reads, ordered
multi-attachment enumeration, skip/partial consumption, disposal errors —
do not exist. `docs/streaming/w4-stream.md:71` cites PB-3002 for the family
stream source generally but claims no attachment byte behavior; W3-F states
no bytes/streams exist (`docs/families/w3-families.md:314-315`).

Disposition: GAP-F1 (attachment content plane: bytes, ranges, bounded
readers, disposal). The existing family-stream plumbing is reusable
transport but is not row evidence.

### PB-3003 attachments-batch-session — seam-partial

- Attachment mutations compose with document and counter writes in one
  transaction marker (`src/families/tests.rs:2573-2643`), abort hides all
  staged family changes (`src/families/tests.rs:1386`), and a rejected
  request stages nothing (guard-order proof
  `src/families/store.rs:185-219`).
- Batch atomicity across attachment record writes: a same-document
  copy/move is merged into one staged write because the Engine rejects
  staging the same document identifier twice
  (`src/families/store.rs:419-431`).

Deltas: no deferred session/bulk-insert execution model, no bulk-insert
attachment stream, no input-stream validation (non-readable,
non-zero-position, lengthless streams) because no streams exist; one family
mutation per record per transaction is a recorded W3-F assumption
(`docs/families/w3-families.md`, Assumptions). W3-F maps this row to the
composition tests (`docs/families/w3-families.md:217`).

Disposition: keep composition evidence; route stream/bulk forms to GAP-F1.

### PB-3004 attachments-replication-conflict — cross-seam-partial

Family attachment records are ordinary documents of the reserved
collection (`src/families/state.rs:31,37-44,106-182`), so they ride the
Engine's generic commit and the accepted W4-REPLICATION machinery, which
applies arbitrary `PublishedWrite::Put` documents with no collection
filtering (`src/replication/member.rs:1011-1080`). But nothing tests or
receipts family records on the lane (the harness writes only a `users`
collection, `src/replication/lane.rs:171-198`), and no attachment-specific
replication item, stream transfer, conflict rule, or
same-name-different-stream resolution exists; the replication records make
no family claim (no family mention in `docs/replication/w4-replication-r1..r3.md`).

Disposition: GAP-F7 (family replication semantics: payload items, byte
transfer, conflict rules, scenario receipts). Generic inheritance is not
evidence.

### PB-3005 attachments-revisions — absent

Revision entries store a document value, revision number, and time only
(`src/families/payloads.rs:1259-1289`; push in
`src/families/state.rs:637-643`); revision records carry no attachment
snapshot, reference, or absence marker, and nothing distinguishes current
from historical attachment content. W3-F itself records "historical
attachment-content reads remain a W4 handoff"
(`docs/families/w3-families.md:219`).

Disposition: GAP-F5 (attachment revision snapshots and historical reads).
Blocks on GAP-F1 for content bytes.

### PB-3006 attachments-sharding — absent (SH wave #47-#51)

No sharding code exists in `src/`. The SH-01 model proposes the bucket as
the stable routing identity "for documents, attachments, tombstones, and
per-bucket storage accounting"
(`docs/sharding/model.md:30-32`) on the ADR-0004 replication plane, with
ADR 0011 still `proposed` (`docs/adr/0011-sharding-model.md:1-8`). No
overlay disposition exists for this row yet.

Disposition: SH wave #47-#51 owns; this sweep adds no second proposal.

### PB-3007 attachments-backup-smuggler — cross-seam-partial

The only landed piece is the feed: `FamilyStore::snapshot_family_records`
enumerates reserved-collection records at a snapshot into the crate-private
`FamilyRecordInput` (`src/families/mod.rs:558-586`;
`src/backup/mod.rs:130-181`), with committed-only, foreign-snapshot, and
collision tests (`src/families/tests.rs:3276,3370,3411`). `src/backup/` is
otherwise declaration-only ("No backup behavior exists here ... implemented
by the W4-BACKUP packet", `src/backup/mod.rs:1-11`); there is no export,
import, restore, checksum, count reporting, or attachment byte transfer
(bytes do not exist, PB-3002).

Disposition: BK wave #42-#45 owns the transfer machinery; GAP-F1 supplies
the bytes; GAP-F8 tracks family-specific restore/error behavior.

### PB-3008 attachments-remote-storage — absent

No remote destination, remote flag, schema update, or transfer-error code
exists anywhere in `src/` (no remote/blob/S3 identifiers). The contract
itself defers: "Remote storage remains unresolved until an approved adapter
and secret-handling contract exists" (`parity/contracts/families.json`,
PB-3008 compatibility). First owner is the W5 adapter wave per
`docs/design/w3-core-contract.md:1917`.

Disposition: W5 adapters; blocked on the approved adapter and
secret-handling contract. No engine gap ticket proposed beyond routing.

### PB-3009 counters-batch-transaction — seam-implemented

- One batch stages increment(s) and delete(s) atomically; a failed batch
  leaves no partial state (`src/families/store.rs:495-521`, guard
  `185-219`; overflow aborts before any staging, `508-512`).
- Focused test `counter_batch_is_atomic_across_increment_delete_and_recreate`
  (`src/families/tests.rs:583`); transaction-boundary behavior proven by
  `family_transaction_abort_hides_all_staged_family_changes`
  (`src/families/tests.rs:1386`).

Deltas: no V1 session/cluster transaction surface and no change vectors;
multi-member cluster transactions are replication-owned.

Disposition: keep as the engine-side base; cluster aspects route to GAP-F7.

### PB-3010 counters-query-index — absent

The W3-2 query seam compiles and executes document queries only; there is
no counter query source, projection, include, or counter map/map-reduce
index (`src/query/mod.rs:1-14` "index-less full scan"; no family reference
in `src/query/**` or `src/index/**`). Family values are reachable only
through the family seam, and the query seam mirrors the reserved
collection to exclude it (`src/streaming/sources.rs:43-49` notes the same
mirror in `src/query/spec.rs`).

Disposition: GAP-F3, coordinated with PS-02's GAP-Q19 (PB-4058
counter/time-series index source types) and the TQL/IX waves.

### PB-3011 counters-replication-conflict — cross-seam-partial

Same inherited-document situation as PB-3004 (`src/replication/member.rs:1011-1080`),
with an added semantic hazard: replication is last-writer-wins per document
key (ADR-0004 D-3), so two members' concurrent counter batches on the same
counter record would clobber rather than merge — V1 merges concurrent
increments. No counter-group replication item, no delta merge, no conflict
outcome for counters, no test.

Disposition: GAP-F7 — this is the clearest correctness gap in the
replication set (record-level LWW vs V1 counter merge); needs a decision
(delta-merge records, per-counter CRDT-like encoding, or accepted
difference with receipts).

### PB-3012 counters-sharding-tombstones — cross-seam-partial

The engine-local building block exists: typed family tombstones per
document with deletion version and time
(`src/families/payloads.rs:2522-2538`, `2609-2640`; staging
`src/families/store.rs:1025-1044`), and a recorded tombstone newer than the
writer's observed version rejects the write before any other answer
(`src/families/store.rs:193-203`; test
`family_tombstone_blocks_stale_local_resurrection`, `src/families/tests.rs:1242`).
The row's actual contract — sharded routing of counter groups and counter
tombstones, cluster totals, cleanup safe for all members — is absent: no
sharding code (SH wave), no tombstone cleaner or consumer-wait rule, and
tombstones are never recorded automatically (see PB-3038).

Disposition: SH wave #47-#51 for routing; GAP-F7 for tombstone lifecycle
(automatic recording, replication, consumer-safe cleanup).

### PB-3013 counters-backup-smuggler — cross-seam-partial

Same feed-only status as PB-3007 (`src/families/mod.rs:558-586`);
declaration-only `src/backup/` and `src/migration/`. The row additionally
requires that restored stores replicate without changing totals and that
incomplete counter payloads fail with a reason — both need the replication
merge semantics of GAP-F7 and the BK transfer machinery.

Disposition: BK wave #42-#45 + GAP-F8, sequenced after the GAP-F7 counter
merge decision.

### PB-3014 counters-changes-subscriptions — cross-seam-partial

Counter family writes surface in the change stream as
`ChangeKind::Counter`: reserved-collection records with the
`family:counters/` prefix map to the counter kind
(`src/streaming/subscription.rs:669-680`), events carry kind, scope,
operation identity, storage sequence, and subject key
(`src/streaming/mod.rs:785-807`), and the bounded/acknowledged/checkpointed
subscription machinery is tested (`src/streaming/tests.rs:1101,1153,1218`).
`docs/streaming/w4-stream.md:252` records exactly this coverage and its
limits.

Deltas: no counter payload (delta, name, value) in the event — the subject
is a key, not a family value; no counter-bearing-document include in
subscription batches; no V1 connection/throttling surface.

Disposition: GAP-F2 (typed family change payloads and includes).

### PB-3015 counters-errors-overflow — seam-implemented

- `current.checked_add(delta)` failure is the typed
  `FamilyError::CounterOverflow` with no wraparound and no partial state
  (`src/families/store.rs:508-512`; `FamilyError::CounterOverflow`
  `src/families/mod.rs:475-476`).
- Focused test `counter_overflow_rejects_without_partial_state`
  (`src/families/tests.rs:667`); malformed names rejected at construction
  (`src/families/payloads.rs:41-58`).

Delta: V1's `CounterOverflowException` identity/errno mapping is an
adapter concern; the engine case is typed and distinct.

Disposition: keep as engine evidence; note the adapter mapping in the
adapters wave.

### PB-3016 time-series-append-delete — seam-implemented

- Appends with out-of-order timestamps sort by timestamp after staging; an
  append at an existing timestamp replaces the point (retain-then-push,
  `src/families/store.rs:613-627`); point and inclusive-range deletes
  (`src/families/store.rs:600-609`; `TimeSeriesDelete`
  `src/families/payloads.rs:754-768`); bounded range reads with optional
  inclusive range (`src/families/store.rs:552-566`).
- Focused test `time_series_append_and_delete_preserve_timestamp_order`
  (`src/families/tests.rs:773`).

Deltas: no segment model (single record per series), pages bounded by the
record's document size (`docs/families/w3-families.md:316-317`), no
rollup/incremental forms (PB-3018/3019).

Disposition: keep as the engine base; segment/scale questions ride GAP-F4.

### PB-3017 time-series-range-stream — seam-partial

- Inclusive range type with start<=end validation
  (`src/families/payloads.rs:622-653`); range-filtered reads with the
  retention-horizon rule (a fully pre-horizon range is
  `TimeSeriesRangeInvalid`; a full read returns the retained view)
  (`src/families/store.rs:539-566`; test
  `time_series_full_reads_respect_the_retention_horizon`,
  `src/families/tests.rs:3214`).
- Streaming: `StreamSource::TimeSeries` serves timestamp-ordered batches
  with an offset cursor (`src/streaming/mod.rs:288-296`;
  `src/streaming/sources.rs:63-64`), proven by
  `time_series_stream_preserves_timestamp_tag_and_value_order`
  (`src/streaming/tests.rs:534`). `docs/streaming/w4-stream.md:253`
  records that totals, includes, and offsets are not implemented.

Disposition: keep the reader/stream evidence; GAP-F4 for paging totals,
offsets, and include semantics.

### PB-3018 time-series-incremental — absent

No incremental arithmetic exists: appends carry absolute value vectors
(`src/families/payloads.rs:720-751`), there is no add/subtract-at-timestamp
operation, no same-timestamp merge rule, and no full/raw result
distinction. Rollup generation also does not run
(`docs/families/w3-families.md:323-324`). First owner per the W3 mapping is
the W4 replication/qualification packet
(`docs/design/w3-core-contract.md:1910`).

Disposition: GAP-F4; the incremental series model needs a parent decision
(record encoding for mergeable increments) before implementation.

### PB-3019 time-series-rollup-retention-config — seam-partial

Config half implemented exactly as the row's name promises:

- `TimeSeriesPolicy { retention_ms, rollups }` with named
  `RollupPolicy { aggregation, window_ms }` over a closed
  `Aggregation::{Min,Max,Sum,Mean,Count}` set
  (`src/families/payloads.rs:770-858`).
- Semantic validation at staging: negative retention, duplicate rollup
  names, and non-positive windows are
  `TimeSeriesPolicyInvalid` (`src/families/store.rs:570-583`).
- The policy is reported on every page (`TimeSeriesPage::policy`,
  `src/families/payloads.rs:1013-1037`) and read-time retention is enforced
  symmetrically (`src/families/store.rs:539-551`).
- Focused tests `time_series_policy_reports_retention_bounds`
  (`src/families/tests.rs:2293`), `time_series_arity_and_policy_rules_are_typed`
  (`src/families/tests.rs:2184`), retention horizon
  (`src/families/tests.rs:3214`); W3-F note
  (`docs/families/w3-families.md:225`).

Execution half absent: rollup generation does not run and retention cleanup
does not run — pre-horizon entries stay stored
(`docs/families/w3-families.md:318-324`; `src/families/payloads.rs:819-824`
defers both to the streaming qualification packet).

Disposition: keep config evidence; GAP-F4 owns generation and cleanup
execution.

### PB-3020 time-series-query-index — absent

Same absence as PB-3010: no time-series query source, aggregation, tag or
value filter, or index support in `src/query/**`/`src/index/**`
(`src/query/mod.rs:1-14`). Disposition: GAP-F3 + PS-02 GAP-Q19; TQL/IX
waves.

### PB-3021 time-series-replication-tombstone — cross-seam-partial

Generic document replication would carry the series record whole
(`src/replication/member.rs:1011-1080`), which loses V1's segment
granularity, out-of-order merge, deleted-range precedence, and dead-segment
semantics; no time-series replication item or tombstone exists and no test
covers series records on the lane. Disposition: GAP-F7.

### PB-3022 time-series-backup-smuggler — cross-seam-partial

Feed only (`src/families/mod.rs:558-586`); `src/backup/` and
`src/migration/` are declaration-only. Raw/incremental/rollup/deleted-range
state preservation additionally depends on PB-3018/3019 execution.
Disposition: BK wave #42-#45 + GAP-F8, sequenced after GAP-F4.

### PB-3023 time-series-changes-subscriptions — cross-seam-partial

`family:time-series/` record writes map to `ChangeKind::TimeSeries`
(`src/streaming/subscription.rs:672-678`) under the same checkpoint rules;
`docs/streaming/w4-stream.md:254` records that include-ranges in document
batches are not implemented. Same payload gap as PB-3014. Disposition:
GAP-F2.

### PB-3024 time-series-errors — seam-implemented

Distinct typed cases that never collapse: `TimeSeriesArityInvalid`
(empty values or arity mismatch, `src/families/store.rs:613-619`),
`TimeSeriesRangeInvalid` (inverted delete range `604-606`; pre-horizon read
`547-551`), `TimeSeriesPolicyInvalid` (`570-583`); series names reject the
record-segment separator so one series cannot alias another document's
series (`src/families/payloads.rs:67-73`; test
`time_series_names_reject_the_record_segment_separator`,
`src/families/tests.rs:3076`); missing document distinct from missing series
(`src/families/store.rs:531-532`). Enum declaration
`src/families/mod.rs:477-483`. Tests `src/families/tests.rs:2184,2293`.

Delta: adapter-side mapping of V1 exception identities.

### PB-3025 revisions-crud-range — seam-partial

- Paged reads with validated offset/limit and a total count
  (`src/families/payloads.rs:1044-1074`; `src/families/store.rs:637-658`),
  forced creation snapshotting the current value
  (`src/families/store.rs:671-675`), and revert-by-revision that restores
  the stored value as a new revision and rewrites the user document
  (`src/families/store.rs:676-687`).
- Deleted-or-missing documents return an empty page with `total() == 0`
  (deliberate revision semantics, `docs/families/w3-families.md:301-307`).
- Focused tests `revision_page_and_revert_keep_snapshot_identity`
  (`src/families/tests.rs:857`),
  `revision_revert_rejects_unknown_references` (`src/families/tests.rs:2155`).

Deltas: revert is by V2 revision number, not V1 change vector; no
single-revision get, no revision includes, no collection statistics, no
orphan/deleted-revision distinction beyond the empty page.

Disposition: GAP-F5.

### PB-3026 revisions-config-bin-retention — seam-partial

Implemented subset: a per-document `RevisionPolicy { minimum_revisions,
maximum_revisions }` validated at construction (non-zero minimum, maximum
>= minimum, `src/families/payloads.rs:1130-1167`), applied by
`RevisionCommand::Configure` with immediate trimming that never drops below
the minimum (`src/families/store.rs:688-691`; `src/families/state.rs:645-658`;
test `revision_policy_trims_the_oldest_revisions`,
`src/families/tests.rs:2070`).

Absent: default and per-collection policies, conflict-revision
configuration, the revisions bin and bin cleaner, retention by age,
start-from-etag, and the "cleanup cannot remove revisions needed by
conflict resolution, replication, migration, or subscription cursors"
guarantee (no cleanup exists at all). Disposition: GAP-F5 (policy model
needs a parent decision on collection-scope config and bin semantics).

### PB-3027 revisions-conflicts-tombstones — absent

No conflict state exists for revisions: no resolved flags, conflict
vectors, deleted-revision states, or conflict audit metadata. The only
adjacent pieces are the generic family tombstone (PB-3038) and the
streaming `ChangeSubject::Conflict(ConflictRecord)` observation shape for
replication conflicts generally (`src/streaming/mod.rs:797-807`), which is
not a revision conflict record. First owner: W4 replication/qualification
(`docs/design/w3-core-contract.md:1910`). Disposition: GAP-F7.

### PB-3028 revisions-replication-transfer — absent

No revision-specific transfer: revisions replicate only as whole reserved
documents under generic LWW (unproven, `src/replication/member.rs:1011-1080`),
with no start-from-etag, cleanup barriers, orphan restore, or
convergence acknowledgement that distinguishes current data from required
history. Disposition: GAP-F7 + Replication Receipts.

### PB-3029 compare-exchange-conditional — seam-partial

Engine-local conditional semantics implemented:

- `PutIfIndex`, `PutIfMissing`, `DeleteIfIndex` conditions
  (`src/families/payloads.rs:1381-1404`); the stored index is monotonic
  (saturating increment on every successful condition,
  `src/families/store.rs:738-765`); a stale index is the typed
  `CompareExchangeIndexStale`; reads hide deleted values
  (`src/families/store.rs:705-725`).
- Keys are validated names bounded to 255 bytes without control characters
  (`src/families/payloads.rs:27,41-58,1350-1359`).
- Focused tests `compare_exchange_conditions_require_the_current_index`
  (`src/families/tests.rs:1761`), tenant scope
  (`src/families/tests.rs:1553`).

Deltas: the row's cluster scope ("cluster-wide key/value reads ...
Raft-backed ordering") is explicitly not claimed — the index is monotonic
per key and tenant within one Engine only
(`docs/families/w3-families.md:325-326`). The contract itself still holds
the ownership question open ("Ownership remains unresolved between engine
and control plane"). Disposition: keep engine evidence; GAP-F10 for the
scope/ownership decision.

### PB-3030 compare-exchange-batch-expiration — seam-partial

- Expiry implemented: `expires_at` on both put conditions
  (`src/families/payloads.rs:1389-1397`), validated non-negative at
  construction (`1436-1444`), enforced at read (`is_live`,
  `src/families/store.rs:711-724`) and at staging
  (`747-764`); an expired value is not live, accepts a fresh `PutIfMissing`,
  and the retained index still rejects stale writers
  (test `compare_exchange_expiry_hides_expired_values`,
  `src/families/tests.rs:1903`; W3-F note
  `docs/families/w3-families.md:227`).
- "Expiry does not remove a value that a concurrent conditional mutation has
  advanced" holds structurally: expiry never decrements the index and every
  successful condition increments it.

Absent: batch commands (one key per write request), expiry cleanup,
smuggler transfer, tombstone cleanup, schema-upgrade compatibility.
Disposition: GAP-F10.

### PB-3031 identities-hi-lo-seeding — seam-partial

- Monotonic allocation via `checked_add` with the typed
  `IdentityExhausted` at the representable ceiling
  (`src/families/store.rs:799-809`; `MAX_IDENTITY_VALUE`
  `src/families/payloads.rs:29-37`); seed with explicit `force` for
  backwards moves (`src/families/store.rs:811-815`); unrepresentable seed
  targets rejected at construction (`src/families/payloads.rs:1682-1686`);
  zero-count allocations rejected (`1677-1681`); listing by names
  (`src/families/store.rs:773-787`).
- Focused tests `identity_allocation_is_monotonic_and_backwards_seeding_requires_force`
  (`src/families/tests.rs:1957`),
  `identity_allocation_exhaustion_is_typed` (`src/families/tests.rs:3142`).

Deltas: "No Hi-Lo block distribution exists"
(`docs/families/w3-families.md:327-330`) — allocation advances a single
next-value with no block handout, and `stage` returns no value by contract
so the caller cannot receive an allocated range directly; no
multi-database/entity-type generator surface; no multi-member coordination.
Disposition: GAP-F9.

### PB-3032 identities-sharding-recovery — absent

No identity behavior across sharding, cluster commands, backup/restore, or
member restart has been implemented or tested (the seam is single-Engine).
Disposition: GAP-F9 + SH wave; recovery evidence needs Recovery Receipts
per the row's compatibility note.

### PB-3033 expiration-and-refresh — seam-partial

- Configuration and state: `LifecycleCommand::{SetExpiration, SetRefresh,
  Clear}` (`src/families/payloads.rs:1824-1838`); negative values rejected
  as `LifecycleValueInvalid` (`src/families/store.rs:852-862`); reads
  report presence, both targets, and an `expired` flag
  (`src/families/store.rs:825-838`).
- Focused test `expiration_and_refresh_updates_do_not_resurrect_deleted_documents`
  (`src/families/tests.rs:1008-1106`): elapsed expiry observable, refresh
  does not clear an elapsed expiry, lifecycle writes on a deleted document
  are `DocumentMissing`, deleted documents are not resurrected.

Absent: the execution half — expired documents are never removed
(asynchronous removal, cleanup batches, tombstone creation), document reads
are not filtered by expiry, refresh never extends anything at runtime, and
no interaction with revisions/sharding/cluster transactions exists. No
background worker exists anywhere in `src/` (verified: the only timer-like
machinery is the explicitly clock-less replication transport,
`src/replication/transport.rs:221-222`).

Disposition: GAP-F6; requires a parent decision on the expiry execution
owner (streaming qualification vs a lifecycle worker) and its timing
limits.

### PB-3034 data-archival — seam-partial

- Typed state only: `ArchivalCommand::{SetPolicy{after_ms}, Archive{at},
  Restore}` (`src/families/payloads.rs:2022-2037`) with negative-value
  rejection (`src/families/store.rs:910-921`), guarded staging
  (`901-908`), and state reads that distinguish configured from absent
  (`881-899`); test `archival_state_round_trips_through_the_seam`
  (`src/families/tests.rs:2701`).
- The payload contract itself defers execution: "Archival transfer and
  restore qualification belong to the later backup and migration packets;
  W3-F records the typed state only" (`src/families/payloads.rs:1975-1977`).

Absent: archival execution (archive-at parsing, eligibility marking,
compression, batch limits, reprocessing preservation, archive storage).
Disposition: GAP-F6 (execution) with BK wave dependency for archive
storage/restore.

### PB-3035 data-archival-interactions — absent

No interaction contract exists: archived documents are not filtered from
queries, streams, subscriptions, or replication; no patch/reshard/
smuggler/revision interaction is defined or tested. Disposition: GAP-F6 —
each interaction needs an independent contract test per the row's
compatibility note.

### PB-3036 schema-validation-config-errors — seam-implemented

- Closed rule set (Required, FieldType, EnumValues, NumberRange,
  TextLength) over six logical field types
  (`src/families/payloads.rs:2172-2242`); policy per collection with `None`
  clearing (`src/families/store.rs:986-999`).
- Malformed policies rejected before staging: inverted number/length
  bounds, empty enums, duplicate field/kind rules
  (`FamilyError::ValidationPolicyMalformed`,
  `src/families/store.rs:955-984`).
- Violation computation over closed `ViolationKind`s
  (`src/families/store.rs:256-333`); NumberRange binds both integer and
  float encodings (`246-253`, test
  `number_range_binds_float_and_integer_values`,
  `src/families/tests.rs:2972`).
- Enforcement on document-bound family writes in the shared guard
  (`src/families/store.rs:204-210`) and on-demand validation reads
  (`940-952`); rejected writes stage nothing.
- Focused tests `schema_validation_rejects_invalid_writes_without_mutation`
  (`src/families/tests.rs:1109`),
  `validation_policy_rejects_malformed_definitions` (`2366`); W3-F note
  (`docs/families/w3-families.md:229`).

Deltas: a direct `Transaction::put` bypasses validation because the Engine
does not consult families (`docs/families/w3-families.md:308-313`) — the
PD-2026-08-26-04 charter says schemas are "validated at the engine seam on
write", so this is a charter-level enforcement gap, not just V1 parity; no
NaN-vs-bound ordering; no sharded validator caching.

Disposition: keep engine evidence; SM wave #26-#28 owns the
schema-family parity mapping; GAP-F12 tracks write-seam enforcement.

### PB-3037 schema-validation-index-sharding — seam-partial

Write-time validation at the family seam exists (PB-3036 evidence). The
index/query and sharding halves are absent: nothing under `src/query/**`
or `src/index/**` consults a validation policy, there is no validator
cache, no cross-shard/restart visibility contract, and no configuration
race behavior. Disposition: SM wave #26-#28 + SH wave #47-#51; GAP-F12.

### PB-3038 family-conflicts-tombstones — seam-partial

- Typed per-document tombstone records with family kind, deletion version,
  and recorded time (`src/families/payloads.rs:2522-2538`, `2609-2672`);
  record/clear commands (`src/families/store.rs:1025-1044`); reads report
  the recorded set (`1005-1023`).
- The tombstone guard outranks every other answer for document-bound
  family writes (`src/families/store.rs:193-203`); explicit clear unblocks;
  test `family_tombstone_blocks_stale_local_resurrection`
  (`src/families/tests.rs:1242`); snapshot enumeration includes tombstone
  records for backup (`src/families/mod.rs:558-586`).

Absent vs the row: family-specific tombstone *types* beyond the shared
per-family map (V1 has distinct schemas per family with etags, change
vectors, collection, flags); cleanup that waits for configured consumers;
no resurrection of unresolved conflicts (no conflict states exist);
tombstones are never recorded automatically — deleting a document does not
cascade to family records and records no tombstone
(`docs/families/w3-families.md:296-299`), so the guard only fires when a
caller stages `TombstoneCommand::Record` manually.

Disposition: GAP-F7 (automatic recording on delete, replication of
tombstones, consumer-safe cleanup); the engine-local guard is the accepted
building block.

### PB-3039 family-transactions-batch — seam-implemented

- Document writes plus any number of family writes publish exactly one
  transaction marker; there is no family-only commit path
  (`src/families/mod.rs:601-656`; proof test
  `family_writes_compose_with_documents_in_one_transaction_marker`
  `src/families/tests.rs:2573-2643`).
- Abort hides every staged family change
  (`src/families/tests.rs:1386`); rejected requests leave the transaction
  unchanged (guard order `src/families/store.rs:185-219`); a mutation
  touching one record twice still stages exactly one write
  (`src/families/store.rs:419-431`).
- Operation-identity, Engine-binding, and tenant checks precede staging
  (`src/families/mod.rs:640-656`, `668-685`).

Constraint (recorded, not a defect claim): one family mutation per record
per transaction — the Engine rejects a write set staging one document
identifier twice, so a second mutation of the same family record needs a
new transaction or a wider batch (`docs/families/w3-families.md`,
Assumptions). Replication exposing "one coherent transaction marker rather
than partial family acknowledgements" is inherited from the generic commit
path but unproven for family scenarios (GAP-F7).

Disposition: keep as engine evidence; the marker/replication half rides
GAP-F7 receipts.

### PB-3040 family-query-index — core — absent

No family query/index path exists (see PB-3010/3020). Disposition: GAP-F3;
PS-02 GAP-Q19 and PB-4058/PB-4016 already track the query-side shape; TQL
and IX waves own implementation.

### PB-3041 family-changes-subscriptions — core — cross-seam-partial

Family-record writes participate in the change stream under the bounded,
acknowledged, checkpointed rules (`src/streaming/subscription.rs:637-680`;
tests `src/streaming/tests.rs:979,1025,1060,1101,1153,1218`), and typed
kind/collection filters exist (`ChangeFilter`,
`src/streaming/mod.rs:826-870`). `docs/streaming/w4-stream.md:255` records
the limit: per-family payload inclusion is not implemented. Concretely,
only counters and time-series get a family `ChangeKind`; attachments,
revisions, compare-exchange, identities, lifecycle, archival, validation,
and tombstone records all surface as `ChangeKind::Document`
(`src/streaming/subscription.rs:669-680`), and no event carries a family
payload (subject is a key, `src/streaming/mod.rs:797-807`). Disposition:
GAP-F2.

### PB-3042 family-authorization — protocol — platform-owned

Per charter PD-2026-08-26-11 this repository is a local backend-only
surface: "there is intentionally no authentication, authorization, or
user-facing surface at this boundary. Customer auth and PB-3042 live
exclusively at the TerraScale Platform" (`AGENTS.md:24-34`). The tree
matches: the family seam enforces tenant scope only
(`src/families/mod.rs:85-139`; tests `src/families/tests.rs:1527,1553`),
the protocol adapter forwards typed family operations with bounds and
identity but no authorization (`src/adapters/protocol/mod.rs:364-391`;
`FamilyOperation` `src/operations.rs:264`), and the Platform adapter is an
explicit `unimplemented!` stub — "Platform authorization belongs to W5"
(`src/adapters/platform/mod.rs:22-38`). W3-F likewise claims no
authorization behavior (`docs/families/w3-families.md:341-343`).

Disposition proposal: record PB-3042 as Platform-owned
(accepted-difference at the engine boundary under PD-2026-08-26-11), with
two engine-side retainers: (1) tenant isolation from backend-supplied
context stays engine-enforced (it is), and (2) no private control-plane
route or generic escape hatch may appear in the family seam (it has not).
The Platform proof (typed rejections, audit, secret handling, no value or
byte leakage) belongs to the Platform's PB-3042 work. GAP-F11 records the
disposition request; no engine implementation gap.

### PB-3043 family-size-stream-range — core — cross-seam-partial

What exists: bounded batches (`MAX_STREAM_BATCH_ITEMS`,
`src/streaming/mod.rs:55-57`), the family stream source wrapping one typed
family read with cursor and completion rules (`src/streaming/mod.rs:533-571`;
`src/streaming/sources.rs:55-67`), the time-series offset cursor
(`src/streaming/sources.rs:63-64`), and cancellation/deadline liveness
(`src/families/mod.rs:608-613`, `648-653`; tests
`src/families/tests.rs:2646`, `src/streaming/tests.rs:920`).
`docs/streaming/w4-stream.md:256` states the limit: size limits and
partial-consumption positioning are the family seam's and are not extended
by streaming.

Missing: attachment byte streams and their size limits (bytes do not
exist), explicit family read size limits (time-series pages are bounded
only by the record's document size, `docs/families/w3-families.md:316-317`),
family-stream positioning (the family cursor is single-shot,
`src/streaming/sources.rs:64-66`), and over-limit typed errors.
Disposition: GAP-F1 (attachment plane) + GAP-F4 (explicit limits and
positioning).

### PB-3044 family-replication-sharding — core — cross-seam-partial

Replication half: the accepted ADR-0004 machinery replicates documents
generically and would carry reserved family records
(`src/replication/member.rs:1011-1080`), but no family-specific payload,
segment, stream, deleted-range, or tombstone behavior exists and no
family scenario has a receipt. Sharding half: no code; SH-01 proposes the
bucket-range routing model with attachments and tombstones named as routed
identities (`docs/sharding/model.md:30-32`) on ADR 0011, still `proposed`.

Disposition: SH wave #47-#51 for routing; GAP-F7 for family-specific
replication semantics and receipts. The row's own compatibility note
("V2 replication algorithm, topology, consistency, and Acknowledgement
Levels remain unresolved until parent decision and receipts") still stands.

### PB-3045 family-backup-migration-errors — core — cross-seam-partial

Feed implemented and tested: reserved-record enumeration at an immutable
snapshot into `FamilyRecordInput` (identity, family, encoded value,
published version) so a consumer can restore family state through the
Engine transaction seam without collection-layout knowledge
(`src/families/mod.rs:538-586`; `src/backup/mod.rs:130-181`; tests
`src/families/tests.rs:3276,3370,3411`). Everything else is declaration
only: `src/backup/mod.rs:1-11` and `src/migration/mod.rs:1-15` state no
behavior exists and name W4-BACKUP as owner. No export/import/restore, no
manifest/checksum/count validation, no interrupted-migration resume, and
no family-specific typed error mapping in a transfer context. The typed
family error vocabulary itself exists (`src/families/mod.rs:450-503`) and
is the mapping source.

Disposition: BK wave #42-#45 owns the machinery; GAP-F8 tracks the
family-specific obligations (per-family record fidelity, counts, duplicate
handling, typed errors, logical-migration preservation of family state).

---

## Proposed gap tickets

Gap IDs continue the PS convention (`docs/parity/ps-02-query-sweep.md`
used GAP-Q*). These are proposals for the parent to schedule; nothing was
filed in Forgejo by this sweep.

- **GAP-F1 — Attachment content plane.** Bytes, bounded readers, byte
  ranges, disposal semantics, size limits, and multi-attachment ordered
  enumeration for attachments; bulk/batch stream forms. Rows: PB-3000
  (remainder), PB-3002, PB-3003 (stream forms), PB-3043 (attachment half),
  prerequisite for PB-3005/3007/3045 content fidelity. Owners: families +
  streaming coordination.
- **GAP-F2 — Family change payloads.** Typed per-family payloads and
  includes in change events and subscription batches (counters delta/value,
  time-series ranges, attachments, revisions, archival); per-family kinds
  beyond Counter/TimeSeries. Rows: PB-3014, PB-3023, PB-3041. Owner:
  streaming + families.
- **GAP-F3 — Family query/index sources.** Counters, time-series
  (raw/rollup/tags), and family projections as typed query sources and
  index source types; cross-references PS-02 GAP-Q19 (PB-4058) and
  PB-4016. Rows: PB-3010, PB-3020, PB-3040. Owners: TQL + IX waves with a
  families contract input.
- **GAP-F4 — Time-series execution: rollups, retention cleanup, paging,
  incremental series.** Rollup generation and retention deletion
  (currently config-only), page totals/offsets/includes, and the
  incremental time-series model (merge arithmetic, record-encoding
  decision). Rows: PB-3017 (remainder), PB-3018, PB-3019 (execution half),
  PB-3043 (limits/positioning). Owners: streaming qualification packet per
  the W3-F handoff, with the W4 replication/qualification packet owning
  PB-3018 per `docs/design/w3-core-contract.md:1910`.
- **GAP-F5 — Revisions completeness.** Attachment revision snapshots and
  historical reads; collection/default policies, conflict-revision
  configuration, revisions bin and bin cleaner, retention-by-age,
  start-from-etag; revert-by-change-vector, single-revision reads,
  includes, collection statistics. Rows: PB-3005, PB-3025 (remainder),
  PB-3026 (remainder). Owner: families, sequenced after GAP-F1 for
  attachment snapshots.
- **GAP-F6 — Lifecycle and archival execution.** Expiry deletion with
  cleanup batches and tombstone creation; refresh extension semantics;
  read filtering before/after expiry; archival execution (eligibility,
  archive-at parsing, compression, batch limits, reprocessing) and the
  archival interaction contracts (query/index visibility, subscriptions,
  replication, revisions, family metadata). Rows: PB-3033 (execution),
  PB-3034 (execution), PB-3035. Owners: needs a parent decision on the
  execution owner (streaming qualification vs lifecycle worker); BK wave
  dependency for archive storage.
- **GAP-F7 — Family replication semantics.** Family-specific payload items
  and merge rules (counter delta merge vs record-level LWW is the sharpest
  correctness gap, PB-3011), time-series segments and deleted-range
  precedence, attachment streams and conflicts, revision and tombstone
  replication with consumer-safe cleanup, automatic tombstone recording on
  document delete, cluster-wide compare-exchange ordering, identity
  non-reuse across members, change-vector surface for family reads/writes,
  and family scenario receipts on the accepted lane. Rows: PB-3004, PB-3011,
  PB-3012 (tombstone half), PB-3021, PB-3027, PB-3028, PB-3038 (remainder),
  PB-3039 (marker receipts), PB-3044 (family half), PB-3001/3009
  (change-vector deltas). Owner: replication wave with a families contract
  input.
- **GAP-F8 — Family backup/migration consumption.** Restore path for
  `FamilyRecordInput`, export/import with per-family counts and duplicate
  handling, manifest/checksum validation over family records, family
  typed-error mapping in transfer, logical-migration preservation of
  family state and metadata. Rows: PB-3007, PB-3013, PB-3022, PB-3045.
  Owner: BK wave #42-#45.
- **GAP-F9 — Identity distribution.** Hi-Lo block distribution and value
  handout (today `stage` returns no value), multi-member/multi-database
  allocation without value reuse, identity recovery across backup/restore
  and rejoin. Rows: PB-3031 (remainder), PB-3032. Owners: families +
  replication; SH wave for routing.
- **GAP-F10 — Compare-exchange scope and batch.** Parent decision on
  engine vs control-plane ownership (the contract still marks ownership
  unresolved), cluster-wide ordering approach, batch commands, expiry
  cleanup, transfer compatibility. Rows: PB-3029 (scope), PB-3030
  (remainder). Owner: parent decision, then replication/adapters.
- **GAP-F11 — PB-3042 disposition record.** Request WS-PARITY to record
  PB-3042 as Platform-owned accepted-difference at the engine boundary
  under PD-2026-08-26-11, retaining engine-enforced tenant isolation and
  the no-escape-hatch rule as the engine-side obligations. No engine
  implementation work.
- **GAP-F12 — Schema-validation enforcement at the engine write seam.**
  Extend validation to direct document writes (charter PD-2026-08-26-04
  "validated at the engine seam on write" — currently only family-seam
  writes and on-demand reads validate, `docs/families/w3-families.md:308-313`),
  validator cache invalidation, and index/shard visibility of validation
  state. Rows: PB-3036 (enforcement delta), PB-3037. Owners: SM wave
  #26-#28 with engine coordination.

Two engine-side prerequisites recorded by W3-F remain unowned and gate
several gaps above (`docs/families/w3-families.md`, proposed parent
collision actions): a `pub(crate)` Engine/Transaction identity accessor to
replace the reservation-probe heuristic in `FamilyStore::stage`
(`src/families/mod.rs:668-685`), and reserved-namespace enforcement at the
Engine boundary so a direct document-seam writer cannot squat or corrupt
family records (`docs/families/w3-families.md:282-295`).

## Blockers and notes for the parent

1. **No row can leave `unresolved` from this sweep.** WS-PARITY requires
   Proof IDs from parity tests and, where applicable, Recovery/
   Replication/Benchmark Receipts. This document only proposes routing.
2. **Replication inheritance is unproven for families.** Family records
   are ordinary documents and would ride the generic path
   (`src/replication/member.rs:1011-1080`), but record-level
   last-writer-wins diverges from V1 counter merge semantics; a decision
   is needed before any family replication row is dispositioned
   (GAP-F7).
3. **Attachment bytes gate four rows.** PB-3002, the content halves of
   PB-3000/3003/3043, and the fidelity halves of PB-3007/3045 cannot be
   qualified before GAP-F1 lands.
4. **Cross-wave dependencies.** SH (ADR 0011 `proposed`), BK (packet not
   started), SM (#26-#28), TQL/IX, and the W5 adapter waves own halves of
   these rows; parallel-wave edits are present uncommitted in the working
   tree and were not touched.
5. **Engine frozen-path prerequisites** (identity accessor,
   reserved-namespace enforcement) are recorded above and in the W3-F
   record; they gate correctness hardening for PB-3038/3039 and the
   collision limits.
