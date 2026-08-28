# SE-06 lane receipt: three-region-composite (in-process)

No production claim: this file records lane-scoped facts from the
deterministic in-process qualification lane of the SE-06 member-transport
crate only. Nothing here states a production topology, timing, performance,
or durability capability.

- Base: `develop @ ef72eca` (branch `fleet/se06-2`, worktree
  `terrabasev2-se06`), crate `crates/terrabase-member-transport`
- Producing test: `three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag`
  (in `tests/contract_scenarios.rs`)
- Run command: `cargo test --offline --locked --test contract_scenarios
  three_region_fault_lane -- --nocapture`
- Determinism: `lane_composite_scenario_is_deterministic` runs the same
  script twice and asserts equal receipts.
- Topology: three Regions, two Members per Region, each Region one failure
  domain, every Member a real durable member journal in a unique temporary
  directory under the system temp root (paths not recorded here by design).
- Clock: the lane clock advances exactly one tick per transport pump; no
  wall time, threads, async runtimes, or sleeps anywhere in this lane.
- The 23 W4 contract scenarios re-run over this lane in
  `tests/contract_scenarios.rs`; the full 11-class fault vocabulary fires on
  real encoded envelopes crossing bounded links (see the receipt's
  `faults:` line).

## Receipt (verbatim artifact output)

```text
scenario: three-region-composite
lane: in-process
regions: r-alpha, r-beta, r-gamma
members: m-a1 r-alpha; m-a2 r-alpha; m-b1 r-beta; m-b2 r-beta; m-g1 r-gamma; m-g2 r-gamma
failure domains: fd-r-alpha [m-a1, m-a2]; fd-r-beta [m-b1, m-b2]; fd-r-gamma [m-g1, m-g2]
faults: link-delay, link-reorder, link-drop, link-duplicate, duplicate-replay, healing, member-restart, member-rejoin, leader-loss, region-partition, region-loss-after-acknowledged-write
acknowledged: op-1 at AllRegions by m-a1, m-a2, m-b1, m-b2, m-g1, m-g2
acknowledged: op-3 at RegionQuorum by m-a1, m-a2
acknowledged: op-4 at Local by m-b1
convergence: op-4 held by m-b1, m-b2, m-g1, m-g2 at lane tick 24
convergence condition: every surviving Member durably holds the operation
rpo: 1 lost, journal-sequence window 3..=3
rpo definition: The set of operations acknowledged at the requested level but absent from every surviving Member after the recorded fault, reported as a count and a journal-sequence window; never a time guess.
rto: 2 lane ticks
rto definition: The interval from fault injection to the first post-fault successful operation satisfying the availability condition for the surviving topology, in lane ticks of the lane clock (one tick per transport pump); lane time only.
convergence interval: 8 lane ticks
convergence definition: The interval from the last healing event to the first Convergence observation naming every surviving Member, in lane ticks of the lane clock, together with the condition checked.
replication lag: m-a2 1, m-b1 1, m-b2 1, m-g1 1, m-g2 1, m-b2 2, m-g1 2, m-g2 3
lag definition: The journal-sequence distance between a Member's durable replication cursor and the source's published sequence at observation time; a sequence distance, not a latency claim.
bytes-on-wire catch-up-request: 9 frames, 220 payload bytes, 1129 total bytes, 909 overhead bytes
bytes-on-wire catch-up-batch: 9 frames, 1846 payload bytes, 2827 total bytes, 981 overhead bytes
bytes-on-wire push: 6 frames, 659 payload bytes, 1409 total bytes, 750 overhead bytes
bytes-on-wire ack-report: 6 frames, 144 payload bytes, 846 total bytes, 702 overhead bytes
bytes-on-wire changes delivered: 25
bytes-on-wire overhead within budget: yes
bytes-on-wire per-change within budget: yes
bytes-on-wire definition: Encoded envelope bytes counted at the transport's delivery boundary: per frame class, the frames delivered, their payload bytes, total bytes, and envelope overhead (total minus payload), measured from the lane's per-direction counters; a byte count, not a bandwidth or latency claim.
limit: lane-scoped facts only: no production topology, timing, or performance claim
limit: RPO counts operations acknowledged then absent from every survivor
limit: RTO availability condition: first post-fault local write by the surviving owner
limit: convergence condition names the surviving Members, not the lost Region
limit: replication lag is a journal-sequence distance, not a latency
claim: none beyond the lane; lane-scoped facts only
```

## Reading the measurements honestly

- RPO = 1 (`op-3`, journal-sequence window `3..=3`): the operation was
  acknowledged at `RegionQuorum` inside `r-alpha`, and the loss of the whole
  Region removed every Member that durably held it. The `AllRegions`-acked
  `op-1` is retained by the survivors and is not lost.
- RTO = 2 lane ticks: the script pumps exactly twice between the Region
  loss and the surviving owner's first successful write. This is the
  scripted detection and view-application window, not an observed system
  delay.
- Convergence interval = 8 lane ticks from the healing event to the first
  `Convergence` observation naming all four surviving Members for `op-4`.
- Replication lag values are journal-sequence distances at the stated
  observation instants, before the corresponding repair.
- Bytes-on-wire: measured per frame class at the transport's delivery
  boundary, checked against the written PD-2026-08-26-13 D1 budgets
  (envelope overhead and payload-per-delivered-change), which stay
  provisional until SE-07 ratifies them; a byte count, never a bandwidth or
  latency claim.

## Limits

Everything in this file is a fact of one deterministic run of the synthetic
in-process lane at the named base. The lane proves the seam behaviors it
asserts (acknowledgement lattice, timeout, fencing, conflicts, convergence,
recovery, isolation, fail-closed envelope discipline) and the receipt shape;
it does not measure any real network, disk, or deployment, and no number
here may be cited outside the lane.
