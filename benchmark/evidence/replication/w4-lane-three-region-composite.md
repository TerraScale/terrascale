# W4-REPLICATION R-3 lane receipt: three-region-composite

No production claim: this file records lane-scoped facts from the
deterministic in-process qualification lane only. Nothing here states a
production topology, timing, performance, or durability capability.

- Commit that produced this receipt: `f70a5d8` (branch `fleet/w4-replication`,
  worktree `terrabasev2-w4-replication`) — the repair-loop-1 revision; the
  measurements are unchanged from the first emission at `f6082d7` (the
  repairs fixed correctness paths the composite's script did not alter: the
  acknowledgement home-Region derivation, source-matched resumption, and
  remote-vs-remote conflict resolution — the convergence observed here now
  rests on genuinely applied commits rather than a masked skip)
- Producing test: `three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag`
- Run command: `cargo test --offline --locked --lib three_region_fault_lane -- --nocapture`
- Determinism: `lane_composite_scenario_is_deterministic` runs the same script
  twice and asserts equal receipts.
- Topology: three Regions, two Members per Region, each Region one failure
  domain, every Member a real durable Engine in a unique temporary directory
  under the system temp root (paths not recorded here by design).
- Clock: the lane clock advances exactly one tick per network pump; no wall
  time, threads, async runtimes, or sleeps anywhere in the lane.

## Scenario script (fixed and deterministic)

1. Healthy links: `op-1` written at `m-a1`, acknowledged at `AllRegions`
   with every Member contributing at `LocalDurability::Synced`.
2. `op-2` written locally; lag observed for the five non-writers.
3. Link faults armed (delay+reorder on one direction, drop on another,
   duplicate on another); catch-up repair under the faults; healing.
4. `m-b2` restarted (engine closed and reopened from its directory),
   rejoined against its durable cursor, and caught up with no duplicates.
5. Full two-Region partition (`r-beta` and `r-gamma` isolated). A write on
   `m-b1` under the base view fails admission (leader unreachable, no
   confirmed new leader). The transitioned view (owner `m-b1`, epoch 2) is
   applied on the isolated side; both sides write concurrently to the same
   document key under divergent ownership views.
6. `op-3` written at `m-a1` inside the partition and acknowledged at
   `RegionQuorum` by `m-a1` and `m-a2` (the r-alpha Members; the
   partitioned peers receive nothing).
7. Healing recorded at its lane tick.
8. The entire `r-alpha` Region is lost (both engines closed) after the
   acknowledged write.
9. Two lane ticks of detection and view application, then the first
   post-fault successful operation: `op-4` written by the surviving owner.
10. Lag observed for the surviving followers, repair to every survivor,
    and the convergence observation for `op-4`.

## Receipt (verbatim artifact output)

```text
scenario: three-region-composite
regions: r-alpha, r-beta, r-gamma
members: m-a1 r-alpha; m-a2 r-alpha; m-b1 r-beta; m-b2 r-beta; m-g1 r-gamma; m-g2 r-gamma
failure domains: fd-r-alpha [m-a1, m-a2]; fd-r-beta [m-b1, m-b2]; fd-r-gamma [m-g1, m-g2]
faults: link-delay, link-reorder, link-drop, link-duplicate, duplicate-replay, healing, member-restart, member-rejoin, leader-loss, region-partition, region-loss-after-acknowledged-write
acknowledged: op-1 at AllRegions by m-a1, m-a2, m-b1, m-b2, m-g1, m-g2
acknowledged: op-3 at RegionQuorum by m-a1, m-a2
acknowledged: op-4 at Local by m-b1
convergence: op-4 held by m-b1, m-b2, m-g1, m-g2 at lane tick 22
convergence condition: every surviving Member durably holds the operation
rpo: 1 lost, storage-sequence window 3..=3
rpo definition: The set of operations acknowledged at the requested level but absent from every surviving Member after the recorded fault, reported as a count and a storage-sequence window; never a time guess.
rto definition: The interval from fault injection to the first post-fault successful operation satisfying the availability condition for the surviving topology, in lane ticks of the lane clock (one tick per network pump); lane time only.
rto: 2 lane ticks
convergence interval: 8 lane ticks
convergence definition: The interval from the last healing event to the first Convergence observation naming every surviving Member, in lane ticks of the lane clock, together with the condition checked.
replication lag: m-a2 1, m-b1 1, m-b2 1, m-g1 1, m-g2 1, m-b2 2, m-g1 2, m-g2 3
lag definition: The storage-sequence distance between a Member's durable replication cursor and the source's published sequence at observation time; a sequence distance, not a latency claim.
limit: lane-scoped facts only: no production topology, timing, or performance claim
limit: RPO counts operations acknowledged then absent from every survivor
limit: RTO availability condition: first post-fault local write by the surviving owner
limit: convergence condition names the surviving Members, not the lost Region
limit: replication lag is a storage-sequence distance, not a latency
claim: none beyond the lane; lane-scoped facts only
```

## Reading the measurements honestly

- RPO = 1 (`op-3`, storage-sequence window `3..=3`): the operation was
  acknowledged at `RegionQuorum` inside `r-alpha`, and the loss of the whole
  Region removed every Member that durably held it. The `AllRegions`-acked
  `op-1` is retained by the survivors and is not lost.
- RTO = 2 lane ticks: the script pumps exactly twice between the Region loss
  and the surviving owner's first successful write. This is the scripted
  detection and view-application window, not an observed system delay.
- Convergence interval = 8 lane ticks from the healing event to the first
  `Convergence` observation naming all four surviving Members for `op-4`;
  the pumps in between are the script's repair rounds.
- Replication lag values are sequence distances at the stated observation
  instants (after `op-2` for the first five; after `op-4` for the last
  three), before the corresponding repair.

## Limits

Everything in this file is a fact of one deterministic run of the synthetic
lane at the named commit. The lane proves the seam behaviors it asserts
(acknowledgement lattice, timeout, fencing, conflicts, convergence,
recovery, isolation) and the receipt shape; it does not measure any real
network, disk, or deployment, and no number here may be cited outside the
lane.
