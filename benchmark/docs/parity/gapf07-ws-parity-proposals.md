# WS-PARITY proposals from GAP-F07 (ticket #76)

Proposals only — the parity overlay and its generated inventory are
integrator-owned; this builder mutates none of them (charter shared-file
rules). Each proposal cites its decision record and evidence.

## PB-3011 counters-replication-conflict

Proposed disposition: `seam-implemented` for the merge encoding, with the
lane receipt as proof of the implemented scope.

- Decision record: `docs/families/counter-merge-encoding.md` (D1/D2/D3),
  proposed for parent ratification.
- Encoding: per-counter PN-counter buckets keyed by the never-reused
  engine identity, per-writer monotone `seq`, delete-wins tombstones.
- Evidence: `evidence/replication/gapf07-family-merge-lane-receipt.md`
  (concurrent increments from two members converge to the merged total on
  every member; identity-scoped delta dedup under duplicate delivery and
  replay; rejoin preserves family totals). Producing tests live in
  `src/replication/lane_tests.rs` and `src/families/tests.rs`.
- Remaining deltas (keep the row honest): cluster-wide counter totals
  across sharded routing and tombstone cleanup with the consumer-wait
  rule stay with the SH wave and the PB-3012 lifecycle residual; counter
  re-creation after delete is an explicit exclusion pending a typed create
  operation (decision D2).

## PB-3013 counters-backup-smuggler

Proposed disposition: unchanged owner set (BK wave plus GAP-F8), with one
dependency satisfied: "restored stores replicate without changing totals"
is now true by encoding — a restored counter record carries the same
per-writer buckets, and replication merges it without altering totals
(decision D1; the duplicate-delivery and rejoin lane receipts cover the
identity-scoped dedup). The row should cite the GAP-F07 decision record
when it moves from `cross-seam-partial`. No overlay change is proposed by
this builder beyond that citation.

## PB-5029 replication-counters-and-time-series

Proposed disposition: split the row's two halves.

- Counters: merge implemented with lane receipts (PB-3011 proposal above).
- Time-series: explicit merged-or-excluded decision recorded as EXCLUDED
  in this packet (decision D4): series records keep whole-record D-3 LWW
  because a correct merge needs per-entry union, same-timestamp
  tie-breaks, and deleted-range precedence tombstones (PB-3021), which is
  a format-scale change without a parent directional decision behind it.
  A lane test pins the exclusion so it is observable, not silent.

The w4 row mapping's "Family merge-by-construction under D-3" wording
should be read, after this packet, as: counters merge by construction;
time-series convergence is record LWW by explicit decision.
