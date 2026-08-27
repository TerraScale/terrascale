# Family merge encoding decision record (GAP-F07, ticket #76)

Status: **accepted** — ratified by the parent at integration, 2026-08-26,
on review of D1-D4 against charter PD-2026-08-26-14 D5, the rejected
alternatives' evidence, the lane receipts (convergence, duplicate-once,
redelivery-by-identity, delete-wins, rejoin totals), and both gate
verdicts (inspector GO, warden PASS). Direction was parent-decided
(PD-2026-08-26-14 D5: counters REQUIRE merge semantics; an LWW-forever
outcome is not available to a builder); the encoding choice, delete
semantics, and ledger interaction are accepted as recorded here. The
recorded limits (per-restart writer buckets; tombstone lifecycle with
the SH wave; typed counter-create as a recorded follow-up) stand. This
record owns the encoding choice, the create/delete semantics, the
operation-ledger interaction, and the merged-or-excluded decisions for the
other family kinds. The implementation in this packet lands behind this
record; if the parent rejects the encoding, every changed path is inside
this packet's write scope and reversible.

Owner: GAP-F07 (#76). Supersedes the counter-record encoding statements of
`docs/families/w3-families.md` (which stand as the W3-F history).

## Problem

ADR-0004 D-3 adjudicated document convergence as deterministic
last-writer-wins on a bounded hybrid logical clock, with one carve-out:
"Per-family deterministic merge applies where the family defines one
(counters, time-series — V1 merges these by construction)". No family
defined one. Counter records are ordinary documents of the reserved
collection, so two members' concurrent counter batches on the same record
converge by whole-record LWW: the loser's increments are silently lost.
V1 does not have this divergence (PB-3011 evidence: V1 merges concurrent
increments by construction), and PB-3013 ("restored stores replicate
without changing totals") would become untrue under record LWW.

## D1 — Encoding: per-counter PN-counter buckets keyed by writer

The counter record of one document changes from the W3-F whole-value form
`{"counters": {"<name>": <i64>}}` to a per-counter, per-writer
contribution form:

```text
{"counters": {
   "<name>": {
     "buckets": {"<writer>": {"p": <u64>, "n": <u64>, "seq": <u64>}},
     "tombstone": null | {"writer": "<writer>", "seq": <u64>}
   }
}}
```

- `p` accumulates that writer's positive deltas, `n` its negative deltas
  (magnitudes). The counter's value is `sum(p) - sum(n)` over the record's
  writers, checked into `i64` (the existing typed `CounterOverflow`).
- `<writer>` is the never-reused engine identity of the engine that minted
  the write, taken from the write request's validated tenant scope. It is
  an opaque bucket key to every other layer.
- `seq` is a per-writer, per-record monotone sequence minted by the writing
  engine (one per batch). It orders only that writer's own entries and
  exists so replays and merges are exact.
- Merge of two records is per counter name: per writer, keep the entry with
  the greater `seq`; two entries with the same writer and `seq` but
  different `p`/`n` fail closed (they cannot be minted by one engine).
  The merge is commutative, associative, and idempotent — the properties
  convergence and duplicate-delivery safety need.

Why this encoding wins (alternatives and evidence):

- **Per-counter delta operations with idempotent operation identities** —
  an append-only log of `(identity, name, delta)` where the value is the
  sum over unique identities. Rejected as the primary encoding: the record
  grows with every increment ever made and eventually exceeds the engine's
  document limits, so it requires a compaction protocol (who compacts,
  when it is safe relative to every member's cursor) that is at least as
  large as this decision and unbounded until then. V1 does not keep a
  counter operation log; its merge is state-based by construction.
- **Per-counter keys with own stamps** — each counter carries its own
  (value, stamp) and converges per counter by LWW. Rejected: it only
  narrows the clobber from record granularity to counter granularity.
  Two concurrent increments on the same counter still lose one of them,
  which is exactly the divergence PD-2026-08-26-14 D5 forbids.
- **PN-counter buckets (chosen)** — state-based, bounded by
  `counters x writers`, no compaction protocol, every concurrent increment
  from every writer survives, and the tombstone rule below gives deletes a
  deterministic precedence. This is the classic state-based counter CRDT
  shape and it is the closest match to "merges by construction".

Recorded limits of the chosen encoding:

- A writer bucket is per engine instance. An engine restart mints a new
  engine identity, so the old bucket freezes (never shrinks, never
  dominates) and the new one starts at zero; totals stay correct and the
  record grows by one bucket per restart per counter. A member-stable
  writer identity would remove this limit but requires an engine-seam
  change outside this packet's write scope; recorded as a ratification
  option, not silently taken.
- The W3-F whole-value record form still decodes (see D4): it maps to a
  single bucket under the reserved writer tag `legacy-v1` with `seq` 0.
  Two members that both hold different legacy values for the same name
  cannot be distinguished (one writer tag); legacy records are therefore
  single-writer by construction and the merge keeps the greater legacy
  bucket. New writes always encode the new form.

## D2 — Create and delete conflict semantics

- **Create vs create**: two members' first writes on the same counter
  record both create the record with their own buckets; the merge keeps
  both. Convergence is the union, not a winner.
- **Delete**: `CounterOperation::Delete` records a per-counter tombstone
  `{writer, seq}` and drops the deleting record's buckets for that name.
  On merge, a name with any tombstone is deleted: every bucket for that
  name is dominated and dropped, and the tombstone persists. Delete
  therefore wins over concurrent increments — the same precedence the
  engine already applies at the document level ("a recorded family
  tombstone that is newer than the writer's view rejects the write
  first"). A delete of a name that carries neither buckets nor a
  tombstone is a no-op (blind deletes cannot flood tombstones).
- **Re-create after delete**: fails closed. An `Increment` on a tombstoned
  counter name is rejected with the existing typed
  `FamilyError::TombstoneConflict`, locally and at the merge. The closed
  family registry has no typed create/reset operation, and a silent
  resurrect would be indistinguishable from a concurrent increment that
  the delete must dominate. The explicit exclusion: counter re-creation
  after delete requires a typed create operation that does not exist yet;
  the WS-PARITY proposals doc records it, and the tombstone lifecycle
  (cleanup safe for all members, PB-3012's consumer-wait rule) stays a
  recorded residual with the SH wave.
- **Record-level emptiness and record-level deletes**: the record document
  is cleared only when it holds neither live counters nor tombstones. A
  tombstone keeps the record alive so a remote stale increment cannot
  resurrect the name unnoticed. A replicated record-level delete — the
  source's EMPTY record state — carries no merge information: under the
  merge the union keeps the receiver's record, so the replication apply
  path stages nothing for it (a delivery consisting only of such deletes
  is a no-op apply). Without this rule, a blind batch deleting absent
  names would clear a replica's merged record and lose other writers'
  increments.

## D3 — Merge interaction with the operation ledger

The W3 operation ledger binds an operation identity to its write set by
digest and answers replays by identity. Merge changes the staged value of
an incoming counter-record write (local merged with incoming), so the
binding needs one exact rule:

- First delivery of operation `O`: the receiving member stages
  `merge(local, incoming)` under `O` and commits; its own ledger now binds
  `O` to that merged write set.
- Any later delivery of an already-committed `O` (duplicate frames, a
  source's redelivery, another member's journal carrying `O`) resolves by
  identity and stages nothing. This is required, not an optimization: the
  member's record may have merged further operations since, so restaging
  a merged value under `O` would trip the ledger's typed
  `OperationIdReuse` guard. Resolving by identity is also the fail-closed
  answer to a hostile delivery that reuses a live identity with different
  contents: the durable first binding wins and the hostile value is
  dropped, never merged.
- Delta dedup is therefore identity-scoped twice: per writer bucket
  (`seq` max, so a re-delivered record contributes nothing) and per
  operation (the durable ledger replay). Both are exercised by lane tests.

## D4 — Which family regions merge, which are excluded

- **Counters: MERGE** (this record). Replication apply merges counter
  records; they do not compete under D-3 LWW, and pure counter operations
  are never conflict-skipped.
- **Time-series: EXCLUDED** from merge in this packet. Recorded reason:
  a correct merge needs entry-level union with same-timestamp tie-breaks
  and deleted-range precedence tombstones (PB-3021's deleted-range
  precedence and dead-segment semantics), which is a per-entry causality
  format change of the same magnitude as D1 without the parent's
  directional decision behind it. Series records keep whole-record D-3
  LWW; a lane test pins that decided behavior so it is an explicit
  exclusion, never a silent one.
- **Attachments: EXCLUDED** from merge. Recorded reason: attachment
  metadata merge without the byte plane (GAP-F1 owns content bytes) can
  desynchronize metadata from content, and V1's same-name,
  different-stream resolution has no V2 counterpart yet (PB-3004 routes
  payload, byte transfer, and conflict rules to GAP-F1 plus this ticket).
- **Revisions: EXCLUDED** from merge. Recorded reason: revision numbers
  are minted from each record's local `next` counter, so two members'
  histories collide on revision numbers and a union would need a
  renumbering contract plus cleanup barriers (PB-3028's start-from-etag
  and cleanup barriers are explicitly absent); merging ill-defined
  histories is worse than record LWW.

Each exclusion is a decision, not a deferred default: changing it requires
a superseding record with new evidence, the same bar as overturning D1.

## Classification note (for ratification)

The change affects the encoded value of family record documents. It does
not touch the storage format (pages, frames, WAL, ledger, journal,
codec), the recovery machinery, or the durability contract: the same
document, transaction, journal, and ledger machinery carries the new
value shape, and the W3-F form still decodes. Recovery-lane behavior is
unchanged by construction; the lane receipts for this packet live under
`evidence/replication/`.
