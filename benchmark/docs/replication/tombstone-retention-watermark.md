# Tombstone retention watermark decision record (GAP-R02, ticket #79)

Status: **proposed** — written for parent ratification. Direction was
parent-decided by the gap adjudication (PD-2026-08-26-14 D3/D4: GAP-R02
exists because `servable_history_start` is the only bound on deletion
evidence today); the watermark's bounds, advance rules, consumer classes,
and durability rule are this record's to propose. Every changed path in
this packet is inside its write scope and reversible.

Owner: GAP-R02 (#79), parity row PB-5023 (`replication-tombstones`,
partial). Coordinates GAP-F07 (the counter-record tombstone that D2 of its
record leaves with "tombstone lifecycle with the SH wave"), #42 (BK-01
backup chains), #43 (BK-02 restore), and GAP-S1 (#81, storage compaction —
the component that will drive the watermark in production).

## Problem

A delete is a publication like any other: the journal records
`Delete { collection, id, version_at_delete }` under the commit's
operation identity, and replication replays it idempotently with the
recorded key/version binding. The gap is retention. Nothing in the tree
says how long that deletion evidence must survive:

- `servable_history_start` bounds what the journal can serve, but nothing
  bounds what the journal may *discard* against the consumers that still
  need it. When history is given up (GAP-S1 compaction is the accepted
  follow-up), an offline member, an incremental backup chain, or a
  migration that still needs a delete below the new bound loses it — and
  a lost tombstone is delayed data loss: the deleted document resurfaces
  when the stale member rejoins or the chain restores.
- An incremental backup whose base falls below the servable bound fails
  closed (`BackupError::SourceUnavailable`) — correct, but with no
  retention rule there is no state in which that chain could ever be
  served instead of refused.
- V1's own contract is explicit that this is a retention rule, not a
  replication rule: "Tombstones prevent stale replication from
  resurrecting deleted counters until cleanup is safe for all members"
  (PB-3013) and "Cleanup waits for configured consumers and does not
  resurrect stale values or remove unresolved conflicts" (PB-3035).

## Vocabulary

- **Deletion evidence** — one content-free retained fact of a delete:
  tenant, collection, document id, version at delete, journal sequence,
  operation identity, the D-3 stamp and writer it was minted under, and
  the local durability the commit achieved. It never carries document
  content (a tombstone that kept the body would keep deleted data alive).
- **Tombstone retention watermark** (`TombstoneRetention`) — the state
  that owns the retention floor, the retained evidence set, the consumer
  floors, and the declared history bound.
- **Retention floor** — the sequence below which deletion evidence may be
  discarded. Everything at or above the floor is retained.
- **Consumer floor** — the durable progress of one consumer (a member per
  tenant, a backup chain, a migration) in the serving engine's sequence
  space.
- **History bound** — the sequence below which publication history has
  been given up: `max(engine.servable_history_start(), the bound declared
  to the watermark)`. The declared bound is how the storage seam's
  compaction (GAP-S1) tells the watermark what it did.

## D1 — The retention invariant and its bounds

```text
retention floor  <=  every consumer floor
retention floor  <=  floor cap (when the operator set one)
```

- The floor starts at zero: retain everything. It only ever rises.
- The floor may not advance past the *slowest* consumer floor. An offline
  member is exactly the case the rule protects: while its floor is low,
  the watermark holds deletion evidence for it.
- The floor cap is an operator-set absolute bound ("evidence at or above
  this sequence is retained unconditionally"), so retention can be
  configured to outlive any consumer's patience. It is a lower bound on
  retention, never an upper one.
- A consumer whose floor is unknown (`None`) blocks every advance above
  zero: a member that has never synced needs all of the evidence.

Advance is a typed, fail-closed operation
(`TombstoneRetention::advance_floor`): a candidate that violates either
bound is refused (`ConsumerBelowFloor`, `FloorRegression`,
`FloorExceededCap`) and recorded in the bounded refusal log; the floor
does not move. Dropping a consumer (`forget_consumer`) removes its
constraint — that is how a decommissioned member or a pruned chain stops
holding the watermark — and never lowers any other consumer's need.

## D2 — Interaction with `servable_history_start`

The journal's own bound and the watermark compose in one direction:

```text
effective history bound = max(engine servable_history_start,
                             watermark's declared history bound)
```

The watermark never *lowers* the servable bound and never claims
retention the journal cannot prove. Raising the effective bound is a
declaration the storage seam makes to the watermark
(`declare_history_bound`), and the watermark enforces three rules on it:

1. **Monotone.** The bound only rises (`BoundRegression` otherwise).
2. **Extraction before discard.** The declaring caller hands the deletion
   evidence of the window it is giving up; the watermark holds every fact
   at or above the retention floor and counts (never silently drops) the
   ones below it. When the member drives the declaration from its own
   journal, a window it cannot read refuses the declaration outright — an
   unreadable window is not an empty one, or history would be given up
   whose evidence was never extracted.
3. **Durability before discard.** The caller asserts the retained set is
   durably held. In this packet the only durable deletion-evidence store
   is the journal itself, so the assertion is honest only while the
   journal still covers the window — which is exactly the pre-compaction
   state. A retained-only store does not exist yet, and the rule fails
   closed (`EvidenceNotDurable`) until GAP-S1 provides one. This is the
   recovery interaction: a crash between "history discarded" and
   "evidence durable" would be silent delayed data loss, so the ordering
   is enforced as a refusal, not a convention.

The declaration also records, per tenant, the **highest non-deletion
sequence below the bound** (`non_deletion_through`). That single fact
decides whether a consumer below the bound can still be served completely
by deletion evidence alone (D4).

## D3 — Consumer classes

| Consumer | Floor | Who records it |
| --- | --- | --- |
| Member, per tenant | the member's advisory `applied_through` in this source's sequence space | the serving member, from every catch-up request it answers |
| Backup chain | the base recovery point's boundary sequence | the backup run, when the plan carries the watermark |
| Migration, per tenant | the migration's read floor | the migration driver (the smuggler export is state-based and registers no floor; an incremental migration would) |

A member that has never requested a tenant is seeded at floor `None` by
the serving member's topology seeding, so an offline member blocks the
watermark from the moment it is seated — not from the moment it first
talks.

## D4 — Serving below the bound: deletion evidence, not history

Deletion evidence extends catch-up across the history boundary for
*deletions*. It does not reconstruct non-deletion history. The rule is the
same for a member and for an incremental backup chain:

```text
a consumer at floor c is servable below the bound iff
    retention floor <= c                         (evidence is held)
and (per tenant) non_deletion_through <= c       (no put was missed)
```

- **Member catch-up.** When a request's cursor is below the effective
  bound, the source answers with the gap flag as today *and*, when the
  rule holds, a `DeletionEvidenceBatch` carrying the tenant's retained
  evidence above the cursor plus the bound facts. The follower applies
  the deletes idempotently (operation identity + recorded version), does
  not resurrect the deleted documents, and may then resume incremental
  service from the bound. When the rule does not hold (a put was missed),
  the follower keeps today's fail-closed `GapUnservable`: it needs a full
  state transfer, never a silent skip.
- **Backup chain.** An incremental run whose base is below the effective
  bound archives the retained `Delete` records of `(base, bound)` before
  the journal range, so the chain restores without resurrecting. When the
  rule does not hold, the run keeps today's
  `BackupError::SourceUnavailable`.
- **Migration/export.** A smuggler export that selects tombstones emits
  the journal's delete records plus the retained evidence below the bound,
  so a migrated store inherits the same deletion proof. The export is
  state-based, so a missed put below the bound cannot diverge it.

## Rejected alternatives

- **Time-based retention** ("hold tombstones for N days"). The engine has
  no wall clock in its seams, and the correctness condition is not time:
  it is the slowest consumer's progress. A time rule would either drop
  evidence a lagging member still needs or hold it forever with no
  observability. Rejected for the same reason the lane clock is
  caller-driven.
- **Per-tenant watermarks.** Sequences are one global space per engine and
  the journal discards globally; a per-tenant floor would let one tenant's
  evidence be dropped while the same discarded range still holds another
  tenant's consumer. The floor is global; consumer floors are per
  (consumer, tenant).
- **Retain everything, forever.** Correct but unbounded: the retained set
  and the journal grow without limit and no operator can state a bound.
  The watermark exists to make the bound explicit and enforced.
- **A durable tombstone store in this packet.** The right seam for a new
  durable artifact is the storage format (GAP-S1, ADR territory), not the
  replication module. This packet enforces the ordering rule (D2.3) that
  makes the future store safe and leaves the store itself to GAP-S1.

## Recovery interactions

- **Restart.** The watermark state is process-local, like the member's
  advisory progress and D-3 stamps (the recorded CA-10/CA-13 residual).
  The durable facts it is derived from are durable: consumer floors come
  from journal-derived member cursors and sealed backup manifests, and
  the retained evidence is the journal's while the journal covers it. A
  cold restart re-derives both.
- **The unsafe window.** Between "history given up" and "evidence
  durably held" there is no safe state, which is why D2.3 refuses the
  transition instead of warning about it.
- **Restore.** A restored chain replays retained `Delete` records through
  the same engine transaction seam as journal deletes, so a restore below
  the bound removes exactly the documents the source removed.
- **No claim made here.** This record and its tests prove focused,
  lane-scoped behavior only. No Recovery Receipt, multi-region
  qualification, or performance claim is made; the exclusive recovery
  lane (BK-02 follow-on) owns the restart/restore receipts for the
  watermark, and GAP-S1 owns the compaction receipts.

## Limits

- The retained set is in memory and bounded by the window between the
  retention floor and the history bound; the floor cap bounds it from the
  operator's side. No count bound is imposed: dropping a needed tombstone
  to fit a count would be the exact defect this record exists to prevent.
- The declared history bound is driven by tests and by GAP-S1's future
  compaction path. No engine component gives up journal history today, so
  in production the declared bound stays `None` and every behavior in
  this packet is byte-identical to the pre-packet tree.
- A member below the bound that missed a put still requires a full state
  transfer (PB-5024 anti-entropy); this record does not build one.
- The counter-record tombstone GAP-F07 D2 mints (a live record that keeps
  the deleted name fenced) is a family-level fact and rides the generic
  put/delete plane; this record governs how long such evidence survives,
  not its encoding.

## Evidence

Focused tests: `src/replication/retention_tests.rs` (contract rules,
member catch-up below the bound, fail-closed put-missed case),
`src/backup/tests.rs` (chain below the bound), and
`tests/recovery/tombstone_retention_public.rs` (public backup/restore
seam). Lane receipts: `evidence/replication/gapr02-tombstone-retention-lane-receipt.md`.
WS-PARITY proposals: `docs/parity/gapr02-ws-parity-proposals.md`.
