# W4-REPLICATION R-3 lane receipt: member-restart-rejoin

No production claim: lane-scoped facts from the deterministic in-process
qualification lane only.

- Commit: `f70a5d8` (branch `fleet/w4-replication`)
- Producing test: acts D of `run_composite_lane`, asserted by
  `three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag`
  and the R-1 suite's `member_rejoin_checks_identity_and_catches_up_without_duplicates`
  and `restarted_member_resumes_from_its_durable_replication_cursor`
- Run command: `cargo test --offline --locked --lib three_region_fault_lane -- --nocapture`

## Scenario

After the composite script's link-fault repair and healing, `m-b2` holds two
journaled operations for the lane tenant. Its engine is closed (the member
restart: process exit modeled as engine shutdown) and reopened from the same
durable directory, then rejoined against the durable cursor captured before
the restart, and caught up from `m-a1`.

## Facts asserted

- The reopen is the identity and format check: the location's recorded
  storage format verifies, and the journal-derived cursor after the reopen
  equals the cursor captured before it.
- Rejoin validation passes and the member is admitted.
- Catch-up after the rejoin applies no duplicate: the member's journal length
  for the tenant is unchanged by the repair pass (already-held operations
  resolve by operation identity, and the member's own writes returning
  through another member's journal resolve by identity with their minted
  stamp).
- The repair pass reports caught up.

## Measurements

Not measured in this scenario in isolation: RPO (no loss), RTO (no outage
window). The restart's recovery behavior inside the composite script is part
of its convergence interval (8 lane ticks from healing to the convergence
observation).

## Limits

One deterministic run of the synthetic lane at the named commit. The
scenario proves rejoin validation against the durable cursor and duplicate
free catch-up at the seam. The in-memory replication state (clock, key
stamps) does not survive a restart; that limit and its proposed collision
action (W4-CA-10) are recorded in the R-2 implementation record.
