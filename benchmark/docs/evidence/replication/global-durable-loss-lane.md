# GlobalDurable complete-Region-loss lane (in-process receipt)

Status: recorded 2026-08-29. The lane is
`crates/terrabase-cluster/tests/global_durable_loss.rs` — 5 rows, all
green. This receipt closes the positive half of gap-sweep row 14
(`docs/evidence/qualification-gap-sweep.md`): a receipt now NAMES the
cluster-crate `DurabilityLevel::GlobalDurable` as the acknowledged level
of a write that then survives the complete loss of one Region.

No production claim: every fact below is a deterministic in-process
lane fact. Nothing here states a production topology, WAN behavior,
timing, or performance capability.

## Commands

- `cargo test -p terrabase-cluster --test global_durable_loss` — 5
  passed, 0 failed.
- `cargo test -p terrabase-cluster` — the packet's focused check. At
  2026-08-29T23:09:43Z every suite of the crate was green except one
  file that is NOT this lane's: `tests/model_checker.rs` (another
  concurrent packet's in-flight, uncommitted work; its
  `the_checker_reports_truncation_when_bounds_cut_the_space` row failed
  with `left: 11, right: 10`). Excluding that foreign file, the crate
  stood at 60 green tests: `--lib` 40, `coordinator_crash_matrix` 9,
  `global_durable_loss` 5, `member_loss_after_ack` 2, `qualification` 4
  — the last three of those are also concurrent packets' files present
  uncommitted in the shared working tree; this lane owns only
  `global_durable_loss.rs` and this receipt.

## Topology and model

- Three selected Regions `r-alpha`, `r-beta`, `r-gamma`, RF=2 in each
  (seats `m-a1/m-a2`, `m-b1/m-b2`, `m-g1/m-g2`), configuration
  generation 1, replica-set epoch 1 — the same shape as the w4
  three-region composite receipt.
- Two planes, each proving only what it can:
  - the cluster-crate acknowledgement ledger
    (`terrabase_cluster::durability::DurabilityCoordinator`) — the
    deterministic model at the storage/transport integration boundary
    where `DurabilityLevel::GlobalDurable` lives. "Complete Region
    loss" there is both of the Region's Members marked unavailable.
  - the real-member host (`terrabase_cluster::host::ClusterHost`) — six
    real durable Members in separate directories, the write committed
    through the composed coordinators at
    `LeaderlessLevel::GlobalQuorum`. "Complete Region loss" there is
    the cluster closed and the lost Region's two member directories
    destroyed, then the four survivors reopened from their own durable
    state.

## What is proven (in-process)

1. **Row 14 positive — the acknowledged write survives.**
   `global_durable_receipt_survives_complete_region_loss_in_the_ledger`:
   `op-1` acknowledged at `DurabilityLevel::GlobalDurable` in the
   three-Region shape answers a receipt whose `level()` IS
   `GlobalDurable`, whose contributors are all six seats (both seats of
   every selected Region) under the one fencing pair (generation 1,
   epoch 1). After the complete loss of `r-beta`, a re-coordination of
   the SAME operation identity from a surviving Member is idempotent
   (it adds no contributor; still 6 stored observations) and refuses
   typed (`DurabilityFailure::Partial`) with `missing_regions ==
   ["r-beta"]` — the boundary over the lost shape is never re-claimed —
   while the observation names all four surviving seats as durably
   holding the acknowledged write. Zero acknowledged-write loss. The
   lane then advances the membership to the two survivors (overlapping
   step, generation/epoch 2), shows the old operation is fenced
   (`StaleOperation`, never silently re-acked under a new
   configuration), and a NEW write acknowledges at `GlobalDurable`
   with the surviving four-seat footprint.

2. **The receipt contrast — RegionalDurable's honest RPO window.**
   `regional_durable_receipt_states_its_rpo_window_and_the_identical_loss_takes_the_write`:
   in the identical three-Region shape, `op-r` acknowledged at
   `RegionalDurable` answers a receipt whose durable footprint is
   exactly the accepting Region's two seats — the receipt itself states
   the regional-loss window at acknowledgement time. The complete loss
   of the ACCEPTING Region destroys every copy of `op-r` (its whole
   footprint was inside the lost Region; CRDT convergence cannot
   recover a mutation absent from every surviving copy), while the
   GlobalDurable write `op-g` is still held by all four surviving
   seats after the identical fault. RPO 0 for the global write against
   one complete Region loss; a whole-write loss window for the
   regional one — the charter's stated contrast
   (`GOAL.md`, "Durability and acknowledgement levels").

3. **The level demands EVERY selected Region — a typed refusal, not a
   majority.**
   `global_durable_demands_every_selected_region_and_refuses_typed_when_one_cannot_be_selected`:
   with only the accepting Region reachable out of three (the other two
   WAN-partitioned), `GlobalDurable` refuses typed
   (`DurabilityFailure::Partial`, `missing_regions == ["r-beta",
   "r-gamma"]`, `is_durable() == false`) and names the reachable
   Region's two durable partials for the retry; the identical shape
   acknowledges at `RegionalDurable`. When the WAN heals, the SAME
   operation identity recovers to the global level through a different
   Member as coordinator (six contributors, no replacement operation
   minted). This extends the existing negative row
   `wan_partition_never_claims_global_durability` (one partitioned
   Region) to the case where only one Region can be selected at all.

4. **Single selected Region — the degenerate shape, pinned honestly.**
   `a_single_selected_region_gets_no_region_loss_protection_from_the_global_level`:
   over a one-Region membership the ledger mints a DEGENERATE
   `GlobalDurable` receipt (level named, footprint = that one Region's
   two seats, no WAN boundary crossed), and the complete loss of that
   Region takes the acknowledged write with it — every contributor on
   the receipt was inside the lost Region, and the post-loss
   re-coordination answers a typed `Timeout` naming the lost Region.
   See the limits below: the packet brief asked for a typed REFUSAL
   here; that refusal does not exist in the code today.

5. **Real durable Members — readable in every surviving Region.**
   `global_durable_write_survives_complete_region_loss_on_real_members`:
   the same three-Region RF=2 shape as real `ClusterHost` Members; the
   write commits at `LeaderlessLevel::GlobalQuorum` (the host's
   boundary check requires two contributors in EVERY selected Region)
   with both seats of each Region contributing. The ledger receipt for
   the same operation identity names the identical six durable seats.
   The cluster is then closed, the lost Region's two member directories
   are destroyed, and the four survivors reopen from their own durable
   state — every surviving Member READS the acknowledged body back
   (4/4), and a new global-level write over the surviving shape
   acknowledges and is readable in both surviving Regions.

## Honest limits

- **In-process only.** No WAN, no real Region, no timing: the lane
  clock is the test's own instruction order. Row 29's production-shape
  WAN lanes remain blocked-external.
- **The ledger has no read-only query.** Its live post-loss observation
  is a re-coordination of the same operation identity — safe because
  ledger storage is idempotent (it provably adds no contributor) —
  whose `PartialObservation` names the durable stores. "Surviving
  copies" is always computed as the stored observations intersected
  with the surviving membership; the ledger retains the lost seats'
  observations as history and this receipt never counts them as
  surviving copies.
- **The RegionalDurable contrast is ledger-side only.** The in-process
  host fans every write synchronously to every seated Member whatever
  level is requested, so it cannot model the un-replicated regional
  window; asserting a RegionalDurable loss on the host would be false.
- **The typed single-Region refusal is NOT implemented.** The packet
  brief also asked for `GlobalDurable` to refuse typed when only one
  Region is selected. `DurabilityCoordinator::submit` does not do
  that: over a one-Region membership it mints the degenerate receipt
  (row 4 above), and the in-crate unit row
  `global_requires_all_regions_for_one_two_and_three_region_shapes`
  pins that success. The charter does not mandate the refusal — it
  conditions the RPO 0 promise on "at least two selected active
  Regions" (`GOAL.md` "Durability and acknowledgement levels"; ADR 0033
  §"RPO, RTO, and backup defaults") — but a typed
  refusal would be defensible hardening. Implementing it requires an
  edit to `crates/terrabase-cluster/src/durability.rs` and to that
  pinned unit row, which are outside this packet's owned paths
  (new test files plus this receipt). Unimplemented is unimplemented:
  no claim is made beyond the pinned behavior and its consequence.
- **Row 14 is not closed at production scope.** The gap sweep's own
  framing stands: this is a deterministic lane fact about the
  cluster-crate boundary, not a production Region-loss qualification.
  The negative half of the row (a WAN partition never CLAIMS global
  durability) was already proven by
  `wan_partition_never_claims_global_durability`; the composite-receipt
  `AllRegions` survival evidence lives in
  `docs/evidence/replication/w4-lane-three-region-composite.md`.
