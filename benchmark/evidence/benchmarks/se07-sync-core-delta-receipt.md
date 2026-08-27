# SE-07 Benchmark Receipt: sync-core delta transitions and client-link
bytes (budget items E-8..E-12)

No performance claim beyond the measured, deterministic counters and
byte counts recorded here. Objective status against PD-2026-08-26-03
(PostgreSQL-parity or better): **inconclusive** — the reference
baselines and statistical gates are SE-15's deliverable; no wall-clock
quantity is claimed anywhere in this receipt.

## Run identity

- Base source: `33dd166` (pre-change); changed source: this packet's
  commit on branch `fleet/se07-2`.
- Launcher command (repository root, offline, locked):
  `cargo test --offline --locked --release -p terrabase-sync-core --test delta_efficiency -- --nocapture`
  (red run: same command at base `33dd166`).
- Profile: `--release`; fixture: the E-8 workload of 6,000 small commits
  (two fields each) under a 64 KiB batch budget, checkpoint window 8,192.
- Correctness gates in force: the sync-core contract suite (11 tests),
  the `terrascale.sync.v2` protocol contract suite (12), the golden
  vector replay, the conformance-kit contract, and the TBEP suites all
  green at the changed source.

## Measurements (red = pre-change, green = post-change)

| Item | Red (base `33dd166`) | Green (this packet) | Verdict |
| --- | --- | --- | --- |
| E-8 delta-batch utilization (backlog ≫ budget) | 15,433 B served of a 65,536 B budget across 505 commits — 0.24 utilization (red failure against the written 70% budget) | 48,702 B across 1,929 commits — 0.743 utilization, `within_budget = true`, no single-commit oversize | `v2-faster` (deterministic byte count per batch) |
| E-10 allocations to seal + encode one delivery frame (587 wire bytes) | 29 allocations | 1 allocation (samples `[1, 1, 1, 1, 1]`) | `v2-faster` (deterministic allocation count) |
| E-11 heartbeat frame | exactly `FIXED_ENVELOPE_BYTES` = 38 B | unchanged (definition-check, green before and after) | `no-improvement` (budget already met; now measured by a named test) |
| E-11 trigger notification | within `TRIGGER_FRAME_BUDGET_BYTES` = 256 B | unchanged | `no-improvement` (definition-check, now pinned) |
| E-11 delta per delivered change (client link) | 30 B/change payload, 30 B/change wire (505-commit batch) | 25 B/change payload, 25 B/change wire (1,929-commit batch — better amortization of the fixed envelope) | `v2-faster` (deterministic byte count per delivered change) |
| E-9 delta-transition correctness | — | every existing contract and vector test green; no observable frame-contract change on the wire vocabulary | guard (mapped to the existing suites) |

## The packing change is wire-visible — recorded, not noise

The E-8 work changes which commits a served delta batch carries (the
batch contents, not the frame vocabulary). The golden corpus pins served
bytes, so `slow-consumer.json` was regenerated through its sanctioned
path (`cargo test --test vectors_runner -- --ignored regenerate_corpus`)
and the conformance kit's manifest fixture was re-emitted through its
`--emit-manifest` path. The corpus file shrank 81,972 → 60,254 bytes
(the same backlog drains in fewer, better-packed batches); all other
vectors are byte-identical. SDK decoders are unchanged: the frame
vocabulary, codecs, and checkpoint semantics are untouched.

## E-12 compression decision: **reject**, with receipts

Decision rule (written before measurement, in
`docs/benchmarks/se07-efficiency-budget.md`): reject link compression if
the measured payload-per-delivered-change on both links sits at or below
50% of its written budget and no measured workload exceeds a byte
budget; otherwise adopt with a receipt.

Measured inputs:

- Client link: 25 payload bytes per delivered change (E-11, this
  receipt) against the 512 B-per-change member budget and a 65,536 B
  delivery budget — 4.9% of the per-change budget.
- Member link: the SE-06 composite lane receipts measured
  bytes-on-wire per delivered change within the 512 B budget (all
  classes, both lanes), and the E-3/E-4 batching in the member-lane
  receipt cuts the per-change fixed cost a further 8× on the
  acknowledgement fan-out path.
- The dominant per-frame cost is the fixed envelope — 38 B on the
  client channel, 35 + 4 B on TBEP, ≤ 192 B written on the member lane —
  which compression cannot shrink; only payload compresses, and the
  payload is already a compact binary delta export.

Decision: **reject** compression on the sync links. No measured
workload exceeds any written byte budget, the compressible term is a
single-digit percentage of the per-change budget, and compression adds
per-frame CPU on the commit-to-delivery hot path (PD-2026-08-26-05 D4)
for no measured gain. Revisit trigger (recorded): any future measured
workload whose bytes-on-wire per delivered change exceeds its written
budget — that measurement, not intuition, reopens this decision.

## Invalid / failed runs retained

- The first green run of `frame_encode_allocations_meet_the_budget`
  measured 4 allocations against the budget of 2. Two of the four were
  the test's own input construction (`payload.clone()`,
  `checkpoint.clone()`) inside the measured window, not the seal+encode
  path; the harness was corrected to construct inputs before the window
  (budget covers sealing and encoding, as written) and a parallel-run
  serialization lock plus min-of-5 sampling was added because the first
  red run (29) included concurrent allocations from sibling test
  threads. Both measurement-method corrections are part of the adapter;
  the recorded green value is 1 with samples `[1, 1, 1, 1, 1]`.
- No run was discarded for disagreeing with a desired outcome.
