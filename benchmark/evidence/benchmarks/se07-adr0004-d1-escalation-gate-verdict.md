# SE-07: ADR-0004 D-1 escalation-gate verdict (openraft)

ADR-0004 D-1 records the escalation path: Option C (a full-consensus
data plane, openraft) escalates at the W4-REPLICATION gate if
`old_owner_is_fenced_after_leader_transition` and its fault variants
cannot pass with the accepted single-leader mechanism (admission-time
topology-ownership fencing plus apply-time epoch checks), or if that
mechanism cannot meet the plane's written efficiency budgets. SE-07
(ticket TerraScale/TerraBaseV2#9) records the verdict with measurements.

## Verdict: **not fired**

The gate does not escalate. The accepted D-1 mechanism passes both of
its conditions at the measured source. openraft stays the recorded
fallback of the ADR, not an adopted component; no follow-up wave is
spawned by this verdict.

## Measurements the verdict rests on

1. Fencing (`old_owner_is_fenced_after_leader_transition` and variants):

   - The scenario is green on the accepted W4 plane: scenario 20 of the
     R-2 in-crate contract (tracked in
     `docs/replication/w4-replication-r3.md`, suite `src/replication`),
     re-run green at this packet's source in the full workspace test
     run (`cargo test --offline --locked --release --workspace`: 527
     engine tests passed, 0 failed).
   - The scenario is green on the member transport over both
     engine-owned lanes: `old_owner_is_fenced_after_leader_transition`
     and the leader-loss / deposed-owner retention variants run in
     `crates/terrabase-member-transport/tests/contract_scenarios.rs`
     (24/24 green, in-process lane) and `tests/quic_contract_scenarios.rs`
     (22/22 green, engine-socket lane) at this packet's source — the
     full 11-class fault vocabulary included.

2. Efficiency budgets on the same plane (this packet's receipts, all
   deterministic counters):

   - Member lane (`evidence/benchmarks/se07-member-lane-efficiency-receipt.md`):
     catch-up throughput within the batch bound (E-1), flow-control
     bounds enforced (E-2), batched acknowledgement frames 5 vs 40 for
     an 8-operation `AllRegions` wait (E-3/E-4), slow-consumer isolation
     proven — a full inbound link defers one peer observably while the
     healthy peer is served in the same pass (E-5), apply-path
     allocations independent of tenant document count (E-6), bounded
     push serving (E-7). The SE-06 bytes-on-wire budgets
     (PD-2026-08-26-13 D1) stayed within their written bounds on both
     lanes, and the E-3/E-4 batching cut the acknowledgement fan-out's
     per-change frame cost 8×.
   - Sync core (`evidence/benchmarks/se07-sync-core-delta-receipt.md`):
     delta-batch utilization 0.743 against the written 0.70 budget
     (E-8), frame seal+encode at 1 allocation (E-10), client-link byte
     budgets measured and met (E-11), compression decision rejected on
     measured margins (E-12).
   - TBEP (`evidence/benchmarks/se07-tbep-overhead-receipt.md`): every
     budget class within the fixed envelope bound (E-13), one-write
     stream framing (E-14).

3. Durability: the fsync-per-commit member journal, per-record digests,
   and every fail-closed envelope discipline are unchanged by the
   efficiency pass — no setting was weakened for timing, so the
   escalation question is decided on the mechanism's own terms.

## What would fire the gate (unchanged, for the record)

- A fencing-scenario failure (`old_owner_is_fenced_after_leader_transition`
  or its fault variants) on any lane, at any future source; or
- an efficiency budget the single-leader plane cannot meet that the
  consensus plane demonstrably can (with its own Benchmark Receipt); or
- a charter-level decision to require cross-Region synchronous
  acknowledgement on the write path that the single-leader mechanism
  cannot deliver.

A fired gate spawns a named follow-up wave (a new ticket scoped to the
consensus data-plane evaluation with its own receipts), never a widened
SE-07; the parent owns that dispatch. This verdict creates no such
follow-up.

## Objective status (canonical line, added by SE-15)

PostgreSQL-parity-or-better: inconclusive

Added by the SE-15 standing objective-status discipline
(PD-2026-08-26-03 D3, TerraScale/TerraBaseV2#23) so this receipt carries
the machine-checkable status line `scripts/check_benchmark_receipts.py`
fails closed on. No measured value, verdict, or note above is altered: the
SE-07 runs measured deterministic counters only, so the objective question
is unanswerable from them until the reference baselines exist and run.
