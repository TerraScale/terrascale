# SE-06 lane receipt: three-region-composite (engine-socket, QUIC)

No production claim: this file records lane-scoped facts from the
engine-socket qualification lane of the SE-06 member-transport crate only —
the first real network incarnation of the accepted W4 replication plane on
engine-owned sockets (PD-2026-08-26-13 D2, ADR 0012 D-2). Nothing here
states a production topology, timing, performance, or durability
capability.

- Base: `develop @ ef72eca` (branch `fleet/se06-2`, worktree
  `terrabasev2-se06`), crate `crates/terrabase-member-transport`
- Producing test: `three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag`
  (in `tests/quic_contract_scenarios.rs`)
- Run command: `cargo test --offline --locked --test quic_contract_scenarios
  three_region_fault_lane -- --nocapture`
- Topology: three Regions, two Members per Region, each Region one failure
  domain, every Member a real durable member journal in a unique temporary
  directory; one QUIC endpoint per Member on loopback, one certificate per
  Member minted from the lane certificate authority (no shared Member
  credentials), mutual-TLS connections, one uni stream per encoded envelope
  — no broker between Members.
- Clock: the lane clock stays tick-based (one tick per pump; every protocol
  vocabulary value is a tick); each pump additionally drains real socket
  traffic to a causal completion condition — every flushed frame is read
  off the wire before the pump returns. No wall-clock value appears in
  the receipt.
- The 23 W4 contract scenarios re-run over this lane in
  `tests/quic_contract_scenarios.rs`; the full 11-class fault vocabulary
  fires at the lane boundary of each direction on real frames crossing real
  sockets. The determinism double-run stays an in-process property: over
  sockets, identical scripts assert identical outcomes, while delivery
  timing is the network's.

## Receipt (verbatim artifact output)

```text
scenario: three-region-composite
lane: engine-socket
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
limit: the socket lane frames each envelope with a four-byte length prefix on the wire; the counters measure envelope bytes at the delivery boundary
claim: none beyond the lane; lane-scoped facts only
```

## Reading the measurements honestly

- The outcome facts match the in-process lane's receipt (same script, same
  assertions): all fault classes fired, `op-1` acknowledged at `AllRegions`
  by every Member, `op-3` at `RegionQuorum` inside `r-alpha`, RPO = 1 with
  journal-sequence window `3..=3`, RTO = 2 lane ticks (the scripted
  detection and view-application window), convergence naming every surviving
  Member, and both PD-13 D1 budget checks passing.
- Tick counts are facts of this run: the script fixes the pump count
  between its steps, and a socket-lane pump is a complete delivery step
  (it returns only once every frame it flushed has been read off the wire),
  which is why the values equal the in-process lane's. A slower host still
  observes the same outcomes; the assertions check outcomes, not tick
  values, and the pump-causality contract itself is pinned by
  `tests/quic_lane_determinism.rs` with the streak receipt under
  `se06-quic-lane-determinism.md`.
- Bytes-on-wire: measured per frame class at the socket lane's delivery
  boundary. The socket lane frames each envelope with a four-byte length
  prefix on the wire; the counters measure envelope bytes, as the receipt's
  last limit states. Provisional budgets until SE-07 ratifies them.

## Limits

Everything in this file is a fact of one run of the synthetic socket lane
at the named base, on loopback, with fixture certificates from a lane
certificate authority that exists only for the run. The lane proves the
seam behaviors it asserts on real engine-owned sockets; it does not measure
any real network, WAN, disk, or deployment, and no number here may be cited
outside the lane.
