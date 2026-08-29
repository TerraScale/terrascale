# SE-07 written efficiency budget: multi-region efficiency pass

> Historical efficiency budget. UDS and Loro measurements are baselines only.
> Current release budgets must cover native CRDT commits, direct Region QUIC,
> QUIC TBEP on loopback, authorized reactive queries, and tenant isolation.

Status: the written budget that precedes the SE-07 implementation
(ticket TerraScale/TerraBaseV2#9; charter PD-2026-08-26-03
benchmark-mandatory, PD-2026-08-26-05 D4 native sync-path budgets,
PD-2026-08-26-13 D1 bandwidth budgets). Every item below names the test
or benchmark measurement that reports against it. No number in this
document is a measured result or a performance claim; measured values
live only in the Benchmark Receipts under `docs/evidence/benchmarks/` and
use the charter's exact verdict vocabulary (`v2-faster`,
`no-improvement`, `v2-regression`, `inconclusive`).

## Method and scope

- The measured quantities of this pass are **deterministic counters**
  — bytes, frames, rounds, heap-allocation counts, and journal pages —
  not wall-clock timings. A counter measured twice on the same source
  identity is the same number, so the efficiency comparisons of this
  pass are reproducible lane facts rather than timing samples.
- Wall-clock latency/throughput statements are **out of scope for
  SE-07's verdicts**: the `TerraBase.Benchmark` statistical gates with
  the TerraBase V1 and PostgreSQL reference baselines are SE-15's
  deliverable (PD-2026-08-26-03 D4). Where only wall-clock evidence
  could support a claim, the verdict recorded is `inconclusive`, with
  the reason retained.
- The benchmark adapters this pass adds (named):
  `crates/terrabase-member-transport/tests/efficiency_budgets.rs`
  (member-lane efficiency counters plus a counting allocator),
  `crates/terrabase-sync-core/tests/delta_efficiency.rs`
  (delta-transition and client-link byte counters), and
  `crates/terrabase-tbep/tests/overhead_characterization.rs`
  (registry-wide TBEP overhead and write-path counters). They extend
  the existing lane harnesses (`LaneHarness`, the SE-04 golden corpus,
  and the SE-14 UDS characterization harness), they do not replace
  them.
- No durability setting is weakened for timing anywhere in this pass:
  the member journal's fsync-per-commit record log, its digest-per-record
  framing, and every fail-closed envelope discipline stay exactly as
  landed.

## Budget items

### Member lane (`crates/terrabase-member-transport`)

| ID | Budget (written before implementation) | Measurement (named) |
| --- | --- | --- |
| E-1 catch-up throughput | A follower `B` commits behind catches up in at most `ceil(B / catch_up_batch) + 1` bounded rounds; commits delivered per round equal the configured batch while a backlog remains. | `catch_up_rounds_stay_within_the_batch_bound` (`tests/efficiency_budgets.rs`, in-process lane, B = 512) |
| E-2 flow-control bounds (the NATS-shared knob) | The shared `LaneSettings`/`LaneConfig` catch-up batch is validated to the closed range `[8, 256]` commits and the socket pump window to `[1, 10_000]` ms; values outside fail closed at the config record, so the NATS-shared flow-control knob cannot degenerate the lane (small values starve E-1; large values break the bounded-link contract). | `flow_control_bounds_validate_the_shared_lane_settings` (`tests/efficiency_budgets.rs`) |
| E-3 push-frame batching | Acknowledging a batch of `K` committed operations at any level sends at most **one** push-class frame per peer per acknowledgement round, for any `K ≥ 1`. | `batched_acknowledgement_uses_bounded_frames_per_round` (`tests/efficiency_budgets.rs`, K = 8, AllRegions) |
| E-4 ack-frame batching | The peers answer a `K`-operation push with at most **one** acknowledgement-class frame per peer per round (a batched acknowledgement report), for any `K ≥ 1`; per-operation acknowledgement reports remain on the wire for single-operation probes. | `batched_acknowledgement_uses_bounded_frames_per_round` (same) |
| E-5 slow-consumer isolation | A member whose inbound link sits at its bounded capacity (`MAX_LINK_FRAMES`) is deferred with an observable, bounded counter and never aborts the dispatch pass: every healthy peer's request in the same pass is still served, and every link's queue stays `≤ MAX_LINK_FRAMES` while a silent consumer stops draining. | `slow_consumer_does_not_stall_healthy_catch_up` and `link_queues_stay_bounded_under_a_silent_consumer` (`tests/efficiency_budgets.rs`) |
| E-6 allocation-per-applied-commit | Heap allocations while applying one delivered commit during catch-up are bounded **independently of the tenant's document count `D`**: at `D = 512` the per-commit count stays within amortized container-growth slack of the `D = 32` count (at most 1.5x), measured with a counting allocator — the pre-change apply path cloned the whole derived document state per commit and grew linearly with `D`. | `apply_allocations_are_independent_of_tenant_document_count` (`tests/efficiency_budgets.rs`) |
| E-7 bounded push serving | Serving one pushed operation reads at most **2** journal pages regardless of journal length (positioned by the member's recorded operation sequence, never a full forward scan). | `push_serves_from_bounded_journal_pages` (`tests/efficiency_budgets.rs`, journal length 300 > 9 pages) |

### Sync core, client channel (`crates/terrabase-sync-core`)

| ID | Budget (written before implementation) | Measurement (named) |
| --- | --- | --- |
| E-8 delta-batch utilization | When a session's un-served backlog measures at least the configured batch byte budget, the served delta batch carries at least **70%** of that budget (the SE-02 spike measured ~38% from sum-of-parts estimates; measured re-export feedback tightens packing). The bounded-memory guarantee is unchanged: a multi-commit batch never exceeds the budget, and an oversized single commit still rides alone, flagged. | `delta_batch_utilization_meets_the_written_budget` (`tests/delta_efficiency.rs`) |
| E-9 delta-transition correctness | Every existing sync-core contract test (batching flags, checkpoint resume, retained floor, compaction, SE-04 golden vectors) stays green unchanged; efficiency work may not alter a single observable frame contract. | existing `tests/sync_core_contract.rs`, `tests/vectors_runner.rs` |
| E-10 frame-encode allocations | Sealing and encoding one `SyncFrame` to wire bytes performs at most **2** heap allocations (the digest is computed incrementally over the emitted bytes; the body is emitted once). | `frame_encode_allocations_meet_the_budget` (`tests/delta_efficiency.rs`, counting allocator) |
| E-11 client-link byte budgets | Measured against the SE-04 written definitions: a heartbeat frame encodes to exactly `FIXED_ENVELOPE_BYTES` (38 B); a trigger notification stays within `TRIGGER_FRAME_BUDGET_BYTES` (256 B); measured delta payload bytes per delivered change on the fixture workload are recorded against `DELIVERY_FRAME_BUDGET_BYTES`. | `client_link_frames_stay_within_their_written_budgets` (`tests/delta_efficiency.rs`) |
| E-12 compression decision | Decision rule, written before measurement: **reject** link compression for the sync frames if the measured payload-per-delivered-change on both links sits at or below 50% of its written budget (enough margin that the dominant per-frame cost is the fixed envelope, which compression cannot shrink) **and** no measured workload exceeds a byte budget; otherwise **adopt** with a receipt. The decision, its measured inputs, and the revisit trigger (any future budget-exceeding measurement) are recorded in the sync-core receipt. | measured inputs from E-11 and the member-lane bytes-on-wire counters of `LaneHarness::bytes_on_wire` |

### TBEP overhead (`crates/terrabase-tbep`)

| ID | Budget (written before implementation) | Measurement (named) |
| --- | --- | --- |
| E-13 per-kind envelope overhead | The registry is code-swept closed (every `u16` either decodes to a registered kind or fails), and for a representative maximal-identity frame of every written budget class the measured per-frame overhead (wire bytes minus body bytes, plus the 4-byte stream prefix) stays within the generated fixed envelope bound — the envelope fields are width-fixed per kind, which the saturated-identity case exercises at the worst case. | `every_registry_kind_overhead_stays_within_the_fixed_bound` (`tests/overhead_characterization.rs`) |
| E-14 stream-write path | Writing one frame to a stream performs exactly **1** `write` call — the length prefix and the envelope ride one pre-sized buffer — and strictly fewer allocations than the two-write path it replaces, measured on the same frame in the same test. | `stream_write_uses_one_buffer_and_one_write_call` (`tests/overhead_characterization.rs`, counting writer and allocator) |
| E-15 UDS timing characterization | The SE-14 same-host UDS round-trip harness re-runs before and after as engineering evidence only; its wall-clock outputs support **no** verdict (`inconclusive`) until the SE-15 statistical gates exist. Retained with reasons either way. | `cargo test -p terrabase-tbep --test uds_characterization -- --ignored --nocapture` (existing SE-14 harness) |

### Escalation gate (ADR-0004 D-1, openraft)

The gate fires only if `old_owner_is_fenced_after_leader_transition`
and its fault variants cannot pass on the accepted single-leader plane,
or if the efficiency budgets above cannot be met by that plane. The
verdict is recorded with measurements in
`docs/evidence/benchmarks/se07-adr0004-d1-escalation-gate-verdict.md`; a
fired gate spawns a named follow-up ticket proposal in the build
report, never a widened SE-07.

## Receipt plan

Three receipts land under `docs/evidence/benchmarks/`:
`se07-member-lane-efficiency-receipt.md`, `se07-sync-core-delta-receipt.md`
(including the E-12 compression decision), and
`se07-tbep-overhead-receipt.md` (including the E-15 characterization
runs). Each records: exact source identity (base and changed commit),
launcher command, profile (`--release` for the recorded counters, plus
the dev-profile workspace run the gate now also requires — added by the
SE-07 warden repair after a debug-only frame-header assertion proved
green in every release gate and red in dev),
lane configuration, the red (pre-change) and green (post-change)
counter values, the per-comparison verdict from the exact vocabulary,
objective status against PD-2026-08-26-03 (which stays `inconclusive`
until SE-15's PostgreSQL reference baseline exists), and validity
notes. Invalid or failed runs are retained with their reasons.
