# W4-REPLICATION R-3 lane receipt: region-partition-isolated-writes

No production claim: lane-scoped facts from the deterministic in-process
qualification lane only.

- Commit: `f70a5d8` (branch `fleet/w4-replication`)
- Producing tests: `lane_region_partition_with_isolated_concurrent_writes_converges`
  and act E of `run_composite_lane`
- Run command: `cargo test --offline --locked --lib lane_region_partition -- --nocapture`

## Scenario (focused test)

`r-gamma` is fully partitioned from both other Regions in both directions.
Under the partition, `m-g1` receives the transitioned view (owner of the
range at epoch 2) while `m-a1` keeps the base view (owner at epoch 1) — the
split-brain state. Both sides write concurrently to the same document key
under their own ownership views. The partition heals, the deposed side
accepts the transitioned view (recording its deposition and keeping its
local receipt), and catch-up repair runs in both directions.

## Facts asserted

- Both sides converge to the same value for the contested document: the D-3
  policy decides identically on both sides because the comparison is a total
  order on `(stamp, Member)`.
- The losing write is retained as observable, content-free conflict history
  (`ConflictOutcome::Winner` naming the winner); neither side's document
  value appears in any retained conflict record.
- Deposed-side admission fencing holds for new writes after the transition
  (asserted by the R-2 fencing suite and act E of the composite script).

## Composite-script variant

Act E of the composite script runs the same class with the two-Region
partition (`r-beta` and `r-gamma` isolated), including the acknowledgement of
`op-3` at `RegionQuorum` inside the isolated `r-alpha`, whose loss in act F
produces the composite receipt's RPO of 1.

## Measurements

Not measured in this scenario in isolation: RPO (measured in the composite
script after the Region loss), RTO, and replication lag. The convergence of
the contested key is a verdict asserted on both sides, not an interval.

## Limits

One deterministic run of the synthetic lane at the named commit. The
scenario proves the D-3 convergence of isolated concurrent writes after
healing at the seam, in the lane's clock discipline. Cross-restart conflict
ordering (stamps surviving a member restart) is a documented limit with the
proposed W4-CA-10 collision action, and is not claimed here.
