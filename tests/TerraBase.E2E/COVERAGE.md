# Replication scenario coverage: old suite → design C

R2a-1 (#48, merged 2026-10-12) deleted the old replication tests: about 200 Fast and Slow tests and the binary harness
(`CrashableTerraBase`, `PartitionablePeer`, `FakeRegionNetwork`, `RegionBus`). A green Slow run proves none of those
scenarios for design C. This table maps each scenario to the test that replaces it and records whether the replacement
runs against the new replicated path.

**Release rule.** Release correctness and the 1M-document acceptance stay **BLOCKED** until every row's replacement passes
against the design-C replicated path at the exact release commit, both locally and on Fly. A row that is missing or an
expected failure blocks. The obsolete binary harness is not coming back: crash and storage-stall scenarios need new
harness work on the design-C transport (listed under "Missing").

Status words:

- **pass**: the replacement runs on the design-C replicated path and passes.
- **expected failure (slice)**: the replacement exists and is a strict expected failure in `Gaps.cs` until that slice
  lands. It fails as soon as it passes.
- **missing**: no replacement yet.

Last run, local: `913ca98e43dd` on beta, 2026-10-12, `make terrabase-e2e`: 42 tests, 0 failed. That is 37 strict expected
failures, 3 RegionPin passes and 2 skips. Every replication scenario failed because nothing replicates between R2a-1 and
R4a. Fly: never run against design C. The CI `e2e` job was skipped on main (run 38197258776: deploy failed), and the Fly
test credential handoff is still pending.

## Scenarios

| # | Scenario | Old test (deleted in #48) | Replacement (tests/TerraBase.E2E) | On the design-C replicated path? | Last run | Notes |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | Write in one region readable in the others | Fast/CrossRegion/CrossRegionTests: `A_write_in_either_region_is_readable_in_the_other_with_the_same_version` | ReplicationE2ETests: `Write_in_gru_is_readable_in_sjc…` (E1), `Write_in_sjc_is_readable_in_gru…` (E2); ThreeRegionE2ETests: `Write_in_one_region_is_readable_in_the_other_two` ×6 pairs | expected failure (R4a) | 913ca98e: expected failure | All 3 pairs, both ways |
| 2 | Concurrent same-field writes, 2-way | Fast/Replication/ReplicationTests: `Two_nodes_that_write_at_the_same_time_converge`; CrossRegionTests: `Concurrent_writes_in_both_regions_converge_on_the_same_documents` | ReplicationE2ETests: `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` (E3) | expected failure (R4a) | 913ca98e: expected failure | |
| 3 | Concurrent same-field writes, 3-way | (none; the old suite had 2 nodes or 2 regions) | ThreeRegionE2ETests: `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value`; `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | expected failure (R4a) | 913ca98e: expected failure | The benchmark adds 200 docs × 5 rounds per region (`benchmark/bench`) |
| 4 | Concurrent different-field writes, 2-way | (none; the old engine kept whole-document LWW) | ReplicationE2ETests: `Concurrent_writes_to_different_fields_both_survive…` (E4) | expected failure (R4a) | 913ca98e: expected failure | Per-field lattice R1/R2a, replicated by R4a |
| 5 | Concurrent different-field writes, 3-way | (none) | ThreeRegionE2ETests: `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | expected failure (R4a) | 913ca98e: expected failure | |
| 6 | Delete vs concurrent edit | ReplicationTests: `A_version_read_on_one_member_deletes_the_document_on_the_other`; Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: `Delete_in_one_region_and_edit_in_the_other…` (E5); ThreeRegionE2ETests: `Delete_in_one_region_and_edits_in_the_other_two…` | expected failure (R4a) | 913ca98e: expected failure | Many concurrent deletes: the benchmark's 1 % deletes |
| 7 | Partition, then heal | CrossRegionTests: `Partitioned_regions_keep_serving_their_writes_and_converge_after_heal`; Slow/CrossRegion/CrossRegionPartitionTests: `Regions_converge_without_losing_or_corrupting_writes_through_a_lossy_partitioned_link_and_restarts` | ThreeRegionFaultE2ETests: `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | expected failure (R4a) | 913ca98e: expected failure | `Cluster.Partition` throws until R4a wires its streams in. Local only; Fly has no partition switch (skipped there, with the reason) |
| 8 | Region down, then rejoin | Fast/CrossRegion/CrossRegionDeletionTests: `A_restarted_region_removes_…`; (Fly) FlyOutageE2ETests | ThreeRegionFaultE2ETests: `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down`; FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` (E7) | expected failure (R4a); E7 also needs R6 | 913ca98e: expected failure; Fly: not run | Fly runs only through `make terrabase-e2e-fly-outage` |
| 9 | Node restart mid-replication (receiver) | ReplicationTests: `A_restarted_node_resumes_after_its_peers_applied_prefix`; Slow/ReceiverRestoreTests: `A_caught_up_sender_repairs_a_restarted_receiver_restored_to_an_older_frontier` | ThreeRegionFaultE2ETests: `Restart_in_the_middle_of_replication_loses_no_write` | expected failure (R4a) | 913ca98e: expected failure | Restore to an older frontier (volume rollback): **missing**, R6 refill |
| 10 | Sender restarts with an unsent backlog | Fast/Replication/HintedHandoffTests: `A_restarted_sender_replays_durable_backlog_when_its_peer_returns_without_new_writes` | ThreeRegionFaultE2ETests: `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | expected failure (R4a) | 913ca98e: expected failure | New in this change |
| 11 | Clock skew | Fast/Replication/ReplicatedStampTests: `A_stamp_far_ahead_is_applied_and_counted_but_moves_the_clock_only_the_skew_ahead` | ThreeRegionFaultE2ETests: `Causally_later_write_wins_everywhere_despite_clock_skew` (±2 s), `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | expected failure (R4a) | 913ca98e: expected failure | Missing: the `terrabase.clock.skew_capped` counter assertion (R4a metrics). Local only (skipped on Fly) |
| 12 | Acknowledged-write durability: ack, then crash | Slow/ReplicationChaosTests: `Three_nodes_preserve_acknowledged_writes_after_partition_skew_storage_stall_and_crash`, `The_history_checker_rejects_a_lost_or_changed_acknowledged_write` | ThreeRegionFaultE2ETests: `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` (clean stop only) | **missing** (crash); expected failure (R4a) for the clean-stop proxy | 913ca98e: expected failure | A kill -9 or power loss after the ack needs a process-crash harness on the design-C transport (not `CrashableTerraBase`). The history checker is gone too |
| 13 | Storage stall | Slow/ReplicationChaosTests (storage stall phase); Fast/Replication/CaptureAdmissionTests | none | **missing** | n/a | Needs a stall hook in the design-C write lane or tailer (R3/R4a) |
| 14 | CDC / replication backlog drain | Slow/ReplicationSlowTests: `Advancing_ACKs_keep_a_bandwidth_limited_window_alive…`, `A_database_written_without_pause_does_not_hold_back_the_others`; ReplicationTests: `Exhausted_byte_credit_blocks…`, `The_sender_stays_within_the_byte_credit…` | ThreeRegionFaultE2ETests: `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | expected failure (R4a) | 913ca98e: expected failure | Flow control (byte credit, one hot database not starving others, 24 h / 1 GiB retention cut): **missing**, R4a/R6 |
| 15 | Large documents | CrossRegionTests: `A_group_larger_than_one_reply_crosses_in_parts` | ThreeRegionE2ETests: `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` (900 KiB, 2000 fields) | expected failure (R4a) | 913ca98e: expected failure | |
| 16 | Counters (PN) | (none) | LatticeE2ETests: E8; ThreeRegionE2ETests: `Concurrent_inc_in_three_regions_sums` | expected failure (R5 + R4a) | 913ca98e: expected failure | R5 is #75 |
| 17 | Sets (add-wins) | (none) | LatticeE2ETests: E9; ThreeRegionE2ETests: `Concurrent_set_adds_in_three_regions_union…` | expected failure (R5 + R4a) | 913ca98e: expected failure | |
| 18 | Bulk convergence with counts and digests | Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: E6 (1000); ThreeRegionE2ETests: `Bulk_3000_documents…`; benchmark `repl-bench` (1M) | expected failure (R4a) | 913ca98e: expected failure; 1M only on the old engine | The 1M run on design C waits for R4a |
| 19 | Lossy link (drop, duplicate, corrupt) | Slow/CrossRegionPartitionTests (lossy link); Fast/Replication/SyncProtocolTests, ResponseLimitTests, PeerTransportTests | none | **missing** | n/a | Needs fault injection in the R4a peer transport. Design §9 says a corrupt group is refused at the trust boundary |
| 20 | Anti-entropy repair of rows the streams missed | Fast/Replication/AntiEntropyTests: `Caught_up_members_repair_missing_and_stale_rows_without_new_writes` and 11 others | none | **missing** | n/a | R6 (Digest/ReadBucket). E7 covers catch-up but not repair of a lost group |
| 21 | Unique constraints under partition | Fast/Replication/UniqueReplicationTests: `Partitioned_unique_writes_converge_and_a_deleted_winner_releases_its_key`; Slow/UniqueConstraintReplayTests | none | **missing** | n/a | Design §9: smallest id wins; needs R2b + R4a |
| 22 | Collection, index and schema DDL replication | ReplicationTests: `Collections_and_indexes_created_and_dropped_on_one_member_follow_on_the_other`; CollectionSchemaReplicationTests (2) | Implicit only: `Cluster.StartAsync` waits for each test's collection in every region | expected failure (R4a) for collection create; **missing** for index, schema and drop | 913ca98e: every cluster start times out on "collection on sjc" | |
| 23 | Database deleted at home not brought back | CrossRegionTests: `A_database_deleted_in_its_home_is_not_brought_back…`; CrossRegionDeletionTests | LatticeE2ETests: E14 (document GC, not databases) | **missing** (database delete) | n/a | R10a/R7 |
| 24 | Catalog: database created in one region usable in another; same-name race | CrossRegionTests: `A_commit_registers_a_database_under_a_chosen_id_once` | CatalogE2ETests: E12 | expected failure (R10a) | 913ca98e: expected failure | |
| 25 | Placement and move | CrossRegionTests: placement tests (5) | CatalogE2ETests: E13 | expected failure (R11) | 913ca98e: expected failure | `fly-replay` itself is covered by #50's Fast tests, not here |
| 26 | Changes feed across regions | (none: the per-collection feed and shapes were deleted in R2c) | LatticeE2ETests: E10 | expected failure (R8) | 913ca98e: expected failure | |
| 27 | SQL writes merge across regions | (none) | LatticeE2ETests: E11 | expected failure (R9) | 913ca98e: expected failure | |
| 28 | ts-auth refresh reuse across regions | (none) | LatticeE2ETests: E15 (storage half) | expected failure (R5 + R8); the ts-auth half is R12 | 913ca98e: expected failure | |
| 29 | Replication observability (lag, metering per direction) | Slow/ReplicationObservabilityTests (2); CrossRegionTests: `Both_regions_meter_encoded_replication_bytes_by_direction…`; ReplicationStatusTests (3) | none (the benchmark measures lag from outside) | **missing** | n/a | R4a metrics (`terrabase.replication.tail_lag`) |

## Counts (2026-10-12, at 913ca98e)

| Status | Rows |
| --- | ---: |
| pass on design C | 0 |
| expected failure (a slice) | 22 |
| missing (no replacement) | 7 (rows 12, 13, 19, 20, 21, 23, 29) |

29 rows. Rows 9, 14 and 22 count as expected failures for the part they cover; their missing halves (restore to an older
frontier, flow control and retention, index/schema/drop DDL) are in their notes and also block. Row 12 has a clean-stop
proxy that is an expected failure, but it counts as missing until a real crash is tested.

## Skipped tests and why

| Test | Skipped when | Reason |
| --- | --- | --- |
| FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` | locally, or on Fly without `TERRABASE_E2E_FLY_OUTAGE=1` | It stops a machine of the shared Fly app, so it runs alone through `make terrabase-e2e-fly-outage` |
| BenchmarkClusterHost: `Hosts_three_local_regions…` | always, unless `TERRABASE_BENCH_CLUSTER_FILE` is set | It is the benchmark's local cluster host, not a scenario |
| ThreeRegionFaultE2ETests: partition, backlog, both skew tests | on Fly | Fly has no switch for a partition or a clock skew between machines |
| ThreeRegionFaultE2ETests: region down, restart, ack-then-stop, restarted backlog | on Fly without `TERRABASE_E2E_FLY_OUTAGE=1` | They stop Fly machines |

## Keeping this current

Update a row in the same PR that changes its status: a slice that lands removes its `Gaps.cs` entries and marks the row
"pass" with the commit of the run that proved it. Re-run `make terrabase-e2e` locally and `make terrabase-e2e` plus
`make terrabase-e2e-fly-outage` on Fly at the release commit, and record both in "Last run".
