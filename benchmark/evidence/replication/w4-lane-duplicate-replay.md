# W4-REPLICATION R-3 lane receipt: duplicate-replay

No production claim: lane-scoped facts from the deterministic in-process
qualification lane only.

- Commit: `f70a5d8` (branch `fleet/w4-replication`)
- Producing test: `lane_duplicate_replay_applies_each_operation_once`
- Run command: `cargo test --offline --locked --lib lane_duplicate_replay -- --nocapture`

## Scenario

`m-a1` writes `op-1` and `op-2` for the lane tenant. The `m-a1` to `m-b1`
link direction is armed with the duplicate control, so every frame is
delivered twice. Catch-up repair runs under the fault, then the link heals.

## Facts asserted

- The delivery resolves idempotently: the apply pass reports at least two
  duplicates resolved (the duplicated catch-up batch arrives twice).
- `m-b1`'s publication journal holds exactly the two operations: each
  operation applies exactly once through the W3 operation ledger, by
  operation identity.
- After healing, the follower's view equals the source's view for the
  tenant.

## Measurements

Not measured in this scenario: RPO (no loss fault), RTO (no outage), and the
convergence interval (no partition healing under observation). Replication
lag reaches zero after repair, which the caught-up report states; no nonzero
lag value is claimed.

## Limits

One deterministic run of the synthetic lane at the named commit. The
scenario proves at-least-once delivery with idempotent apply at the seam; it
makes no claim about real network duplication rates or retry behavior.
