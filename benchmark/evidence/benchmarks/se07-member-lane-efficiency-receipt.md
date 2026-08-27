# SE-07 Benchmark Receipt: member-lane efficiency (budget items E-1..E-7)

No performance claim beyond the measured, deterministic counters recorded
here. The measured quantities are frames, rounds, heap-allocation
counts, and queue depths — exact and machine-independent, so each
comparison below is reproducible from the named test. Objective status
against PD-2026-08-26-03 (PostgreSQL-parity or better): **inconclusive**
— the TerraBase V1 and PostgreSQL reference baselines and their
statistical gates are SE-15's deliverable and do not exist yet; no
wall-clock quantity is claimed anywhere in this receipt.

## Run identity

- Base source: `33dd166` (pre-change); changed source: this packet's
  commit on branch `fleet/se07-2` (the diff between them is exactly the
  SE-07 member-transport change).
- Launcher commands (from the repository root, offline, locked):
  - `cargo test --offline --locked --release -p terrabase-member-transport --test efficiency_budgets -- --nocapture`
  - Red (pre-change) run: same command at base `33dd166` before the
    implementation landed (the adapter's first run; captured below).
  - Dev-profile runs added by the warden repair below:
    `cargo test --offline --locked -p terrabase-member-transport --test efficiency_budgets`
    — green 10/10 at the repaired source (both repair rows below are red
    at the packet commit `81c595d` in the same dev profile).
- Profile: `--release` (the profile `scripts/check.sh` gates on).
- Lane: the in-process member lane (`InProcessLane`), three-Region
  topology, six durable member journals under the system temp root, the
  lane clock advancing one tick per pump; no wall time anywhere.
- Correctness gates in force: the 24 W4 contract scenarios, the 22 QUIC
  contract scenarios, and the envelope-discipline suite all green at the
  changed source (same commands, `--test contract_scenarios`,
  `--test quic_contract_scenarios`, `--test envelope_discipline`).
- Durability: unchanged — fsync-per-commit journal records, digest
  framing, and every fail-closed envelope discipline stay exactly as
  landed; nothing was weakened for timing.

## Measurements (red = pre-change, green = post-change)

| Item | Red (base `33dd166`) | Green (this packet) | Verdict |
| --- | --- | --- | --- |
| E-1 catch-up rounds, 512-commit backlog, batch 32 | 16 rounds (bound 17) — budget already met | 16 rounds (bound 17) | `no-improvement` (the budget held before; the guard test now pins it) |
| E-1 catch-up rounds, batch 128 | 4 rounds (bound 5) | 4 rounds (bound 5) | `no-improvement` (throughput scales with the validated batch bound) |
| E-2 flow-control bounds (NATS-shared `LaneSettings`) | values like `catch_up_batch = 7` accepted (red assertion failure) | closed ranges `[8, 256]` commits and `[1, 10_000]` ms enforced, fail closed outside | red→green contract (no counter to compare; validation added) |
| E-3 push frames, 8 operations at `AllRegions` | 40 push frames (per-operation path) | 5 push frames (batched path; one per peer per round) | `v2-faster` (deterministic frame count, 8×) |
| E-4 acknowledgement frames, same wait | 40 ack-report frames | 5 acknowledgement frames (one batched `ack-batch` frame per peer per round) | `v2-faster` (deterministic frame count, 8×) |
| E-5 slow-consumer isolation | dispatch pass aborted with `TransportFull` when the first reply's link was full; the healthy peer's request in the same pass unserved (red failure) | pass completes; the healthy peer is served (`served = 1`), the slow peer is deferred with an observable counter (`DispatchReport::slow_consumer_deferrals`), its link stays at the bounded capacity | red→green contract (isolation proven with lane evidence) |
| E-5 bounded queues under a silent consumer | (invariant held) 74 frames offered to a non-draining link: queue never exceeded `MAX_LINK_FRAMES`, 10 overflows counted and observable | same, and the healthy pair converged around the silent consumer | `no-improvement` (the bound held; now pinned by a named test) |
| E-6 allocations per applied commit | 334 at D=32 documents vs 1,697 at D=512 — linear growth with the document count, 5.1x (red failure) | 81 at D=32 vs 89 at D=512 — 1.10x, within amortized container-growth slack (stable across three runs) | `v2-faster` (deterministic allocation count; the pre-change growth was linear in D) |
| E-7 allocations to serve one push | 643 at journal length 32 vs 1,638 at length 300 — forward scan over every page (red failure) | 27 vs 27 — identical, positioned single-page read | `v2-faster` (deterministic allocation count, journal-length-independent) |

## Wire-visible changes recorded with this receipt

- Envelope minor version `1` (additive; minor-0 frames decode
  unchanged): the new `ack-batch` frame kind and `AckBatch` payload —
  the batched acknowledgement of a multi-operation push. Single-operation
  pushes keep the single-report frame shape, which is why every
  pre-existing scenario's wire behavior is unchanged (all 24 + 22
  scenario suites green without edits, except the envelope-discipline
  row that pinned kind byte `5` as unknown — updated to the first
  actually-unknown byte `6` with the reason in the test).
- New lane API: `LaneMember::send_push_batch` and
  `LaneHarness::wait_ack_batch` (the E-3/E-4 path);
  `wait_ack` is now a single-operation wrapper with unchanged
  per-operation semantics.
- The journal gains a point-lookup `document_collection` used by the
  apply path; `documents_at` remains for whole-state reads.

## Warden repair loop 1 (this repair)

Two warden findings repaired; both red values below were captured by this
repair at the packet commit `81c595d` (dev profile, the same adapter),
not at the packet's pre-SE-07 base — these are defects the packet itself
introduced or left open.

| Finding | Red (packet commit `81c595d`) | Green (this repair) |
| --- | --- | --- |
| MINOR — `send_push_batch` fenced a whole batch under the LAST operation's (range, epoch) | a mixed batch (`op-fence-main`, range `main` at epoch 1; `op-fence-aux`, range `aux` at epoch 4) returned `Ok(2)` and sent **one** push frame claiming `range="aux" epoch=4` while carrying **both** commits — a `main`-range commit rode fenced under the `aux` identity, which the receiver's D-1 apply-time fencing judges the whole batch by | the same batch refuses with the typed `MemberError::MixedFencingIdentity { first: ("main", 1), other: ("aux", 4) }` and sends nothing (`pending == 0`); a uniform batch on the same writer still sends and carries both commits, so the E-3/E-4 batching economy is unchanged |
| NOTE — AckBatch / commit decode capacity trusted the declared element count | a digest-valid frame declaring 262,144 ack reports over a payload that holds 2 spent **8,388,680 bytes** in the decode window — dominated by the 262,144 × 32 B = 8,388,608 B report-vector reservation the declared count alone drove — before failing closed on truncation | decode capacity is bounded by the payload's own bytes — `declared.min(remaining / min_wire_bytes)` per element class (5 B per ack report, 32 B per commit, 13 B per write — each an exact lower bound of that element's wire form) — so the same probe allocates payload-proportional memory and still fails closed (`Truncated`) |

Named tests added to the adapter:
`a_mixed_fencing_batch_refuses_instead_of_fencing_under_one_identity`
and `hostile_element_counts_do_not_amplify_decode_memory`. The fencing
invariant is now documented on `send_push_batch`: one frame fences every
commit it carries under exactly one (range, epoch) identity, so a caller
holding operations that span identities sends one batch per pair and the
mixed batch is refused before anything is sent. The hostile-decode probe
re-signs its corrupted frame with the crate's own digest backend, a
test-only use of the already-direct `ring` dependency (the workspace
lock and the engine-socket crypto closure are unchanged).

Also repaired with this packet (recorded in the sync-core receipt): the
finish protocol now runs the whole workspace in the dev profile —
839 passed / 0 failed / 3 ignored across 36 test binaries — which is how
both rows above were measured.

## Invalid / failed runs retained

- The first red run of `link_queues_stay_bounded_under_a_silent_consumer`
  asserted `rejected == 10` send errors; the lane's contract for an
  undelayed full direction is a counted, observed drop
  (`overflowed`), not a send error. The assertion was corrected to the
  counted-drop contract and the corrected run is the one recorded above.
- The adapter's allocation tests initially ran unserialized against
  sibling tests in the same binary; concurrent allocations from other
  test threads polluted the counts (one intermittent green-phase failure
  and drifting values). The adapter now serializes its tests behind a
  mutex and the recorded green values are from the serialized adapter,
  stable across three consecutive runs (E-6 `81/89`, E-7 `27/27`,
  E-3/E-4 `40+40` vs `5+5` every run). The red values were captured
  before serialization; their signal (5.1x growth for E-6, 2.5x for E-7)
  exceeds any noise by an order of magnitude.
- No run was discarded for disagreeing with a desired outcome; every red
  failure above is the pre-change behavior captured by the same adapter
  at the same base.
