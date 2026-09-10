# Cross-file billing audit — current source

Status: the earlier “first touched shard” finding is withdrawn for the current
source. `src/engine/cross_file.rs` now selects the participant whose engine is
the root/coordinator (`self.engine`) as `billing_shard`; it does not select the
first routed write shard. The root intent is retained for metering-only waves,
and `sqlite::write_usage::mint` debits the root budget once.

Existing evidence: `src/storage/sqlite/cross_file_tests.rs` test
`prepared_cross_file_root_metering_conserves_one_refill_across_different_children`
installs one refill in `root`, runs two operations labelled `shard-2` and
`shard-3`, replays the root install, and asserts root remaining credit is zero,
one allocation row, two operation debits, and two usage facts. This covers the
stable root billing decision and idempotent install at the SQLite participant
boundary.

Residual evidence gap: both loop iterations open the same
`location("cross-file-child-budget")`; child names are participant metadata,
not distinct child SQLite files, and the test does not drive
`RouteParticipantRegistry::begin_batch`/`Transaction::commit_cross_file`.
Qualification should retain one integration regression with physically
distinct child paths, operations whose routed sets differ, crash/reopen between
prepare/install, and exact root receipt/debit conservation. No current source
defect is established by this gap.
