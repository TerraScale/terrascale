# Member-loss-after-acknowledgement lane receipt (gap-sweep row 10)

No production claim: lane-scoped facts from deterministic in-process lanes
only. This receipt closes the "what remains" column of row 10 of
`docs/evidence/qualification-gap-sweep.md` — the exact charter scenario
"a member fails **after** acknowledgement, the survivor is shown to retain
the write, and local writes route elsewhere until RF=2 returns" — which the
pre-existing lanes did not name (member-restart/rejoin and region-loss
lanes, and `member_failure_is_partial_and_recovery_uses_the_same_operation`
/ `member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success`
cover loss **before** the acknowledgement, not after it).

- Producing tests:
  - `src/replication/tests.rs::member_loss_after_acknowledgement_retains_the_write_and_routes_elsewhere_until_rf2_returns`
    (engine replication seam — real durable members, deterministic
    `ReplicationNetwork`)
  - `crates/terrabase-cluster/tests/member_loss_after_ack.rs::member_loss_after_ack_survivor_retains_the_write_and_local_writes_refuse_until_rf2_returns`
    (cluster acknowledgement ledger — `DurabilityCoordinator`,
    `DurabilityLevel::RegionalDurable`/`GlobalDurable`)
  - `crates/terrabase-cluster/tests/member_loss_after_ack.rs::the_degraded_window_blocks_global_durability_too_and_it_returns_after_replacement`
- Run commands (both green at the landing commit):
  - `cargo test --offline --locked -p terrabase-cluster`
  - `cargo test --offline --locked --lib replication::tests::member_loss_after_acknowledgement`

## Scenario

Two selected Regions, two Members each — the RF=2/2 production shape (in
the engine lane on separate failure domains, one owned range per Region so
both Regions carry their own local write path). A write is acknowledged at
the regional boundary with **both** Members contributing; one Member of
`r-alpha` then fails (engine closed) **after** the acknowledgement; the
cluster keeps operating through the single-survivor window; the failed seat
is then recovered/replaced and the Region returns to RF=2.

## Facts asserted

The charter row's five clauses, in order:

1. **Acked at both members.** The engine lane's write acknowledges at
   `AcknowledgementLevel::RegionQuorum` with the report naming
   `r-alpha: [m-a1, m-a2]` — a two-Member Region's quorum is both Members
   (`quorum_of(2) = 2`), so the acknowledgement is RF=2/2. The cluster
   lane's receipt names the same shape:
   `DurabilityReceipt::contributors() == [m-a1, m-a2]` at
   `RegionalDurable`.
2. **One member fails after ack.** `set_member_active(m-a2, false)` /
   `m_a2.close()`.
3. **The survivor retains the write.** The acknowledged document reads
   back at the survivor (`m-a1`) with its value and its journal entry
   intact. At the ledger boundary, the survivor's stored observation of
   the acknowledged operation outlives the failure: re-recording it is the
   typed `DuplicateAcknowledgement` refusal, never a fresh fact.
4. **New local writes refuse typed / route elsewhere — no quorum
   weakening.** In the degraded Region:
   - the engine lane's NEW local write cannot reach `RegionQuorum` and the
     wait answers the immediate typed
     `ReplicationError::QuorumUnreachable` — not a timeout, not a slower
     path, and never a weakened single-Member acknowledgement (the local
     commit is named internal progress only);
   - the ledger refuses every new regional submit in the window with the
     typed `DurabilityFailure::Partial` whose observation names exactly the
     missing seat (`missing_members == [m-a2]`) and the Region
     (`missing_regions == [r-alpha]`), with one store reported as internal
     progress and `is_durable() == false`;
   - the same logical write is served by the other selected Region: in the
     engine lane the identical operation commits at `r-beta`'s owner and
     acknowledges with `r-beta: [m-b1, m-b2]` — the healthy Region's own
     full RF=2 — and reads back there; in the ledger, submits at `m-b1`
     keep answering two-seat receipts throughout the window;
   - `GlobalDurable` refuses typed during the window as well (one Region
     below RF=2 blocks the global boundary, `missing_regions ==
     [r-alpha]`), so no acknowledgement level weakens anywhere.
5. **Recovery restores RF=2.** Following the existing recovery patterns:
   - engine lane — the failed Member reopens from its durable location
     (`restart`), catches up from the survivor through the ordinary
     catch-up exchange (the already-held acknowledged operation resolves
     idempotently; only the missed operation applies; the recovered Member
     serves the Region's converged state), and the next local write
     acknowledges at `RegionQuorum` with **both** `r-alpha` Members
     contributing again;
   - ledger — the failed seat is replaced by a NEW Member through the
     overlapping membership step (`advance_membership`, generation and
     epoch both advanced, overlap with the old quorum required), the
     partial write from the degraded window is fenced by the typed
     `StaleOperation` refusal (it never silently completes under the new
     epoch; the client carries it as a new operation identity), local
     submits resume answering two-seat receipts with the replacement
     contributing, and `GlobalDurable` returns with four contributors.

## Honest limits

- Deterministic in-process lanes only: synthetic Regions (`r-alpha`,
  `r-beta`), synthetic members, a lane clock, and the deterministic
  `ReplicationNetwork`/`DurabilityCoordinator` fault models. No timing,
  WAN, production-shape, or Orchestrator-driven replacement claim.
- Member "failure" is modeled as engine closure / seat deactivation at the
  two lane seams, the same modeling the cited pre-existing rows use; there
  is no crash injection at the cluster seam (that limit is row 9's).
- "Routes elsewhere" is proven as the degraded Region's typed refusal
  coexisting with the other selected Region's healthy two-seat acceptance
  of the same logical write at both seams. No standalone write-router
  component exists in this repository to drive; the routing decision above
  these seams belongs to `TerraScale.Database`.
- The replacement leg at the ledger seam is the overlapping-epoch
  membership step with a fresh Member; the engine lane's recovery leg is
  restart-from-durable-location plus catch-up. An Orchestrator-driven
  replacement with a new host is out of scope for this lane.
- One run of each lane at the landing commit on this host (x86_64); the
  suites are deterministic by construction, and the run commands above
  re-derive every fact.
