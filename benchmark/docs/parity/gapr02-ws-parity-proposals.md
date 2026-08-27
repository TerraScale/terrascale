# GAP-R02 WS-PARITY proposals (ticket #79)

Proposals only, per the PS-wave rule that builders never flip overlays: the
dispositions below are for WS-PARITY (SE-12 close-out, #14) to record from
this packet's evidence. No overlay was mutated; the parity tree is untouched
by this packet.

Evidence base: `evidence/replication/gapr02-tombstone-retention-lane-receipt.md`,
decision record `docs/replication/tombstone-retention-watermark.md`, and the
focused suites it cites.

## PB-5023 `replication-tombstones` — propose `in-progress` (was partial)

The PS-04 sweep's finding was that deletes replay with recorded key/version
binding but "there is no retention watermark that holds tombstones for
offline members, backup chains, or migrations beyond
`servable_history_start`". That watermark now exists and is enforced:

- `TombstoneRetention` (retention floor, retained evidence set, consumer
  floors, declared history bound) with the fail-closed advance rules
  (`ConsumerBelowFloor`, `FloorRegression`, `FloorExceededCap`,
  `BoundRegression`, `EvidenceNotDurable`).
- Offline-member catch-up across a declared bound: the retained evidence is
  served, the deleted document does not resurrect, and the follower resumes
  incremental service at the bound; a missed put below the bound still
  fails closed with the typed `GapUnservable`.
- Backup chains: an incremental below the declared bound archives the
  retained delete and restores without resurrecting; a base below the
  retention floor or above a provable window fails closed; the chain's
  anchor is recorded as a consumer floor.
- Migration/export: a tombstone-selected smuggler export emits retained
  evidence below the bound in place of the journal record.

Why `in-progress` and not `implemented`: the row's V1 scope is
family-carrying tombstones (document, attachment, counter, time-series,
revision tombstones — PB-3035) with cleanup-waits-for-consumers semantics.
This packet proves the retention contract and its three consumer classes on
the generic put/delete plane; the family-specific tombstone encodings and
the durable retained-evidence store remain open (see the coordination rows
below).

## Coordination rows (no disposition proposed here)

| Row | Relation to this packet |
| --- | --- |
| PB-3035 `family-conflicts-tombstones` | Owns the family tombstone encodings and V1's `TombstoneCleaner` wait-for-consumers behavior. GAP-R02 supplies the "consumers" that cleanup waits for: a family tombstone cleaner (V1 parity) should be a retention consumer, not a timer. Propose the linkage be recorded on that row when its owner lands. |
| PB-3013 `counters-backup-and-replication` | GAP-F07's counter tombstone (delete-wins, re-creation fenced by `TombstoneConflict`) is a live record on the generic plane, so its retention is exactly this packet's watermark; the decision record's limits section states this. |
| PB-5013 / PB-6010..PB-6023 backup rows | The below-bound incremental is a new capability on an existing row's seam, not a new row; the fail-closed `SourceUnavailable` cases are unchanged behavior for a plan without a watermark. |
| PB-5024 `replication-anti-entropy-and-catch-up` | A member below the bound that missed a put still needs a full state transfer; this packet's limits name it so the anti-entropy owner sees the boundary. |
| PB-5008 (GAP-S1 storage compaction, #81) | The declared history bound, the extraction-before-discard rule, the durability-before-discard refusal, and the window-must-fully-exist rule (D2.4: a declared bound may not exceed the journal's durable head) are the contract compaction must satisfy; GAP-S1 owns the durable retained-evidence store and the production declaration. |

## Proposed follow-ups (for the parent to file or fold)

1. A durable retained-evidence store (GAP-S1 rider): the watermark's
   `durably_held` assertion is honest only while the journal covers the
   window, which is exactly the pre-compaction state.
2. Typed operator surface for watermark transitions (floor advance, bound
   declaration, refusal log) — natural rider on GAP-R03's typed
   replication observation operations (PB-5035), which already depends on
   TBEP.
3. A family-tombstone cleaner as a retention consumer (PB-3035 linkage).
