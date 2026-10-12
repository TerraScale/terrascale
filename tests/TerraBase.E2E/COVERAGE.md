# Replication scenario coverage: old suite → design C

R2a-1 (#48, merged 2026-10-12) deleted the old replication tests: about 200 Fast and Slow tests and the binary harness
(`CrashableTerraBase`, `PartitionablePeer`, `FakeRegionNetwork`, `RegionBus`). A green Slow run proves none of those
scenarios for design C. This table maps each scenario to the test that replaces it and records whether the replacement
runs against the new replicated path.

**Combined test candidate `f929c623`** (branch `test/e2e-combined-candidate`): R3's PR #106 head `81fa4fcb` (CDC change group
source, per-node peer links with Partition/Heal and lossy routing, the in-process storage stall; R3 owns those harness
files) plus the assertion-side commits (outcome recorder, scenario declarations, `Gaps.cs`, counter settle). Every result
below is from runs at exactly that commit with a clean working tree. R3 is not merged (held by the T1 format freeze), so
main does not yet reproduce these passes.

**What "pass" means here.** Every replicated pass ran on **in-process nodes in one test host** (real Program, real
listeners, real gRPC peer streams over loopback), never on separate OS processes or across a WAN. Partitions are
**simulated** by the harness cutting each node's peer links; the lossy link is a **TCP proxy** (delay, cap, resets), the
closest thing to a network fault run here. The only separate-OS-process scenario that passes is single-node SIGKILL
durability; the replicated one is not supported yet. Fly: never run against design C.

**Release rule.** Release correctness and the 1M-document acceptance stay **BLOCKED** until every row's replacement passes
against the design-C replicated path at the exact release commit, both locally and on Fly. A row that is missing or an
expected failure blocks. The obsolete binary harness is not coming back: crash and storage-stall scenarios need new
harness work on the design-C transport (listed under "Missing").

Status words:

- **pass**: every test of the row ran on the design-C replicated path (in-process nodes, see above) and held.
- **partial**: some of the row's tests pass, the rest are expected failures or not supported (named in the row).
- **expected failure (slice)**: the replacement exists and is a strict expected failure in `Gaps.cs` until that slice
  lands. It fails as soon as it passes.
- **missing**: no replacement yet.
- **BLOCKED on Fly**: the scenario cannot run with the Fly suite's credential (a TerraBase API key, `documents:read/write`
  on the e2e project only). It is skipped there with that reason, never counted as an expected failure, and blocks like a
  missing row. E12, E13, flow control (row 14), DDL (row 22) and the deleted database (row 23) write databases, indexes or
  schemas, which needs `databases:*`; unblocked by the GitHub-OIDC → ts-auth project token.

## Last runs

Summaries come from `make terrabase-e2e` itself (`tests/TerraBase.E2E/Outcomes.cs`, `scripts/terrabase/e2e-summary.sh`),
never from a pass count. Each test declares its nodes and faults (`ScenarioAttribute`); each line says whether it ran on
in-process nodes, separate OS processes or no node (harness check), and whether its partition was simulated or its fault
a TCP-proxy network fault. CI's `terrabase-check` rejects a block here or in `RUNS.md` that lacks a category, the commit,
the working-tree patch identity, the stacked-on line or a test's nodes and faults, or whose counts do not add up.

Three repeat runs on delta at the combined candidate, 2026-10-12, identical test by test; every block is in `RUNS.md`:

| Run | Commit (candidate) | Stacked on (R3 #106 head) | Working tree | actual pass | harness check | expected failure | not supported | skip | fail | total |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 of 3 | `f929c623cab9` | `81fa4fcb360a` | clean | 37 | 4 | 11 | 1 | 2 | 0 | 55 |
| 2 of 3 | `f929c623cab9` | `81fa4fcb360a` | clean | 37 | 4 | 11 | 1 | 2 | 0 | 55 |
| 3 of 3 | `f929c623cab9` | `81fa4fcb360a` | clean | 37 | 4 | 11 | 1 | 2 | 0 | 55 |

Of the 37 actual passes, 36 ran on in-process nodes (24 without a fault; 4 node stop; 3 simulated partition, one of them
with clock skew; 2 clock skew; 2 storage stall; 1 TCP-proxy network fault) and 1 on a separate OS process (single-node
SIGKILL). Skipped in every run: `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` (the benchmark's
cluster host) and `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` (Fly outage test). Not
supported in every run: `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` (node processes
as regions). Expected failures: the changes feed (E10, E15), SQL writes (E11), the catalog (E12, deleted database),
move (E13), anti-entropy and refill (repair, restore), unique-loser on replicated applies, DDL replication and the tail
lag metric.

The commits after the candidate on this branch change only `COVERAGE.md` and `RUNS.md`
(`git diff f929c623cab9 -- ':!*.md'` is empty).

The last run in full:

<!-- e2e-summary -->
E2E run (combined candidate, run 3 of 3), 2026-10-12T05:58Z on delta

- Commit: `f929c623cab928152fea8c4ed5cbd8ecd94d59c9`; working tree: clean
- Stacked on: `81fa4fcb360a4a565a0af5de812220c0fbcae60f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 37 |
| harness check | 4 |
| expected failure | 11 |
| not supported | 1 |
| skip | 2 |
| fail | 0 |
| total | 55 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7) |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | expected failure | in-process nodes | NodeStop, FileTampering | refill and anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | expected failure | in-process nodes | NodeStop, FileTampering | anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a) |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a) |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | expected failure | in-process nodes | none | home region and live move (R11) |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | expected failure | in-process nodes | none | the CDC tail lag metric terrabase_replication_tail_lag (design section 18; R3 publishes send and ack lag only) |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | expected failure | in-process nodes | none | index, schema and drop replication through the metadata documents (design section 5): an index made in gru never reaches sjc |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | expected failure | in-process nodes | SimulatedPartition | the unique-loser rule on replicated applies (design section 9): after the heal gru answers 409 for the larger id, sjc still serves it |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | node processes as regions in the harness (region identity and peer addresses off Fly); node processes cannot form a three-region cluster before peer streams (R4a); /tmp/terrabase-process-L4ojY6 serves alone |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | actual pass | in-process nodes | StorageStall |  |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | actual pass | in-process nodes | none |  |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | actual pass | in-process nodes | none |  |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | actual pass | in-process nodes | none |  |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | actual pass | in-process nodes | ClockSkew |  |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | actual pass | in-process nodes | ClockSkew |  |
| `Concurrent_inc_in_three_regions_sums` | actual pass | in-process nodes | none |  |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | actual pass | in-process nodes | SimulatedPartition, ClockSkew |  |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | actual pass | in-process nodes | none |  |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | actual pass | in-process nodes | none |  |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | actual pass | in-process nodes | none |  |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | actual pass | in-process nodes | none |  |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | actual pass | in-process nodes | none |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | actual pass | in-process nodes | StorageStall |  |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

## Scenarios

| # | Scenario | Old test (deleted in #48) | Replacement (tests/TerraBase.E2E) | On the design-C replicated path? | Last run | Notes |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | Write in one region readable in the others | Fast/CrossRegion/CrossRegionTests: `A_write_in_either_region_is_readable_in_the_other_with_the_same_version` | ReplicationE2ETests: `Write_in_gru_is_readable_in_sjc…` (E1), `Write_in_sjc_is_readable_in_gru…` (E2); ThreeRegionE2ETests: `Write_in_one_region_is_readable_in_the_other_two` ×6 pairs | pass | f929c623: pass ×8 (E1, E2, all six pairs) | All 3 pairs, both ways |
| 2 | Concurrent same-field writes, 2-way | Fast/Replication/ReplicationTests: `Two_nodes_that_write_at_the_same_time_converge`; CrossRegionTests: `Concurrent_writes_in_both_regions_converge_on_the_same_documents` | ReplicationE2ETests: `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` (E3) | pass | f929c623: pass | |
| 3 | Concurrent same-field writes, 3-way | (none; the old suite had 2 nodes or 2 regions) | ThreeRegionE2ETests: `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value`; `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | pass | f929c623: pass (three-way, burst) | The benchmark adds 200 docs × 5 rounds per region (`benchmark/bench`) |
| 4 | Concurrent different-field writes, 2-way | (none; the old engine kept whole-document LWW) | ReplicationE2ETests: `Concurrent_writes_to_different_fields_both_survive…` (E4) | pass | f929c623: pass | Per-field lattice R1/R2a, replicated by R4a |
| 5 | Concurrent different-field writes, 3-way | (none) | ThreeRegionE2ETests: `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | pass | f929c623: pass | |
| 6 | Delete vs concurrent edit | ReplicationTests: `A_version_read_on_one_member_deletes_the_document_on_the_other`; Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: `Delete_in_one_region_and_edit_in_the_other…` (E5); ThreeRegionE2ETests: `Delete_in_one_region_and_edits_in_the_other_two…` | pass | f929c623: pass (E5, three-way) | Many concurrent deletes: the benchmark's 1 % deletes |
| 7 | Partition, then heal | CrossRegionTests: `Partitioned_regions_keep_serving_their_writes_and_converge_after_heal`; Slow/CrossRegion/CrossRegionPartitionTests: `Regions_converge_without_losing_or_corrupting_writes_through_a_lossy_partitioned_link_and_restarts` | ThreeRegionFaultE2ETests: `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | pass (simulated partition) | f929c623: pass | `Cluster.Partition` throws until R4a wires its streams in. Local only; Fly has no partition switch (skipped there, with the reason) |
| 8 | Region down, then rejoin | Fast/CrossRegion/CrossRegionDeletionTests: `A_restarted_region_removes_…`; (Fly) FlyOutageE2ETests | ThreeRegionFaultE2ETests: `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down`; FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` (E7) | pass (in-process node stop); Fly half skipped | f929c623: pass; E7 (Fly) skipped | Fly runs only through `make terrabase-e2e-fly-outage` |
| 9 | Node restart mid-replication (receiver); restore to an older state | ReplicationTests: `A_restarted_node_resumes_after_its_peers_applied_prefix`; Slow/ReceiverRestoreTests: `A_caught_up_sender_repairs_a_restarted_receiver_restored_to_an_older_frontier` | ThreeRegionFaultE2ETests: `Restart_in_the_middle_of_replication_loses_no_write`; RepairAndContractE2ETests: `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | partial: restart **pass**; restore to an older copy expected failure (R6) | f929c623: restart pass; restore expected failure | Restore copies sjc's data directory back to a snapshot taken 100 writes earlier |
| 10 | Sender restarts with an unsent backlog | Fast/Replication/HintedHandoffTests: `A_restarted_sender_replays_durable_backlog_when_its_peer_returns_without_new_writes` | ThreeRegionFaultE2ETests: `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | pass | f929c623: pass | New in this change |
| 11 | Clock skew | Fast/Replication/ReplicatedStampTests: `A_stamp_far_ahead_is_applied_and_counted_but_moves_the_clock_only_the_skew_ahead` | ThreeRegionFaultE2ETests: `Causally_later_write_wins_everywhere_despite_clock_skew` (±2 s), `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | pass (clock skew; the concurrent case also partitions, simulated) | f929c623: pass ×3 | Missing: the `terrabase.clock.skew_capped` counter assertion (R4a metrics). Local only (skipped on Fly) |
| 12 | Acknowledged-write durability: ack, then crash | Slow/ReplicationChaosTests: `Three_nodes_preserve_acknowledged_writes_after_partition_skew_storage_stall_and_crash`, `The_history_checker_rejects_a_lost_or_changed_acknowledged_write` | CrashAndStallE2ETests: `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` (single node, real SIGKILL of a `terrabase` process, NodeProcess harness); `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` (replicated); ThreeRegionFaultE2ETests: `Writes_acknowledged_just_before_their_region_stops…` (clean stop) | partial: single-node SIGKILL **pass** (separate OS process); clean stop **pass** (in-process); replicated SIGKILL **not supported** (node processes as regions) | f929c623: SIGKILL single pass; ack-then-stop pass; replicated SIGKILL not supported | Process kill, not power loss: an OS crash can lose the last commits with `synchronous = NORMAL` (design §18), which no local test simulates |
| 13 | Storage stall | Slow/ReplicationChaosTests (storage stall phase); Fast/Replication/CaptureAdmissionTests | CrashAndStallE2ETests: `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` (single node); `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` (sjc stalls 10 s) | pass (harness stall of the node's single writer, in-process) | f929c623: pass ×2 (single node, replicated) | The stall is the database file held by another writer (`BEGIN` plus a write on a separate connection), so commits wait. A slow fsync would need an IO-delay shim, which is not built |
| 14 | CDC / replication backlog drain; flow control | Slow/ReplicationSlowTests: `Advancing_ACKs_keep_a_bandwidth_limited_window_alive…`, `A_database_written_without_pause_does_not_hold_back_the_others`; ReplicationTests: `Exhausted_byte_credit_blocks…`, `The_sender_stays_within_the_byte_credit…` | ThreeRegionFaultE2ETests: `Replication_backlog_of_3000_writes_drains_after_the_region_returns`; RepairAndContractE2ETests: `A_hot_database_does_not_hold_back_replication_of_another` | pass (backlog drain, flow control); retention cut untested | f929c623: pass ×2 | Flow control: Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`). The 24 h / 1 GiB retention cut and `refill_required` cannot run in real time end to end. R6 must ship them as a Fast test with a fake clock; that requirement is tracked here |
| 15 | Large documents | CrossRegionTests: `A_group_larger_than_one_reply_crosses_in_parts` | ThreeRegionE2ETests: `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` (900 KiB, 2000 fields) | pass | f929c623: pass | |
| 16 | Counters (PN) | (none) | LatticeE2ETests: E8; ThreeRegionE2ETests: `Concurrent_inc_in_three_regions_sums` | pass | f929c623: pass (E8, three-way) | R5 is #75 |
| 17 | Sets (add-wins) | (none) | LatticeE2ETests: E9; ThreeRegionE2ETests: `Concurrent_set_adds_in_three_regions_union…` | pass | f929c623: pass (E9, three-way) | |
| 18 | Bulk convergence with counts and digests | Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: E6 (1000); ThreeRegionE2ETests: `Bulk_3000_documents…`; benchmark `repl-bench` (1M) | pass | f929c623: pass (E6, 3000) | The 1M run on design C waits for R4a |
| 19 | Lossy link (drop, delay, reset) | Slow/CrossRegionPartitionTests (lossy link); Fast/Replication/SyncProtocolTests, ResponseLimitTests, PeerTransportTests | CrashAndStallE2ETests: `Writes_converge_exactly_over_lossy_peer_links` (LossyLink TCP proxy: 200 ms delay, 256 KiB/s, 5 % resets per second); `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` (the proxy itself) | pass (TCP-proxy network fault); proxy self-check: harness check | f929c623: pass; harness check | Byte corruption and frame duplication need fault injection inside the peer protocol (R4a), not a TCP proxy: design §9 refuses a corrupt group at the trust boundary |
| 20 | Anti-entropy repair of rows the streams missed | Fast/Replication/AntiEntropyTests: `Caught_up_members_repair_missing_and_stale_rows_without_new_writes` and 11 others | RepairAndContractE2ETests: `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` (deletes the document's rows from sjc's file while it is down; only a repair can bring it back) | expected failure (R6) | f929c623: expected failure | 200 s window: rounds every 60 s, repair after two differing rounds |
| 21 | Unique constraints under partition | Fast/Replication/UniqueReplicationTests: `Partitioned_unique_writes_converge_and_a_deleted_winner_releases_its_key`; Slow/UniqueConstraintReplayTests | RepairAndContractE2ETests: `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | expected failure: the unique-loser rule on replicated applies (gru answers 409 for the larger id, sjc still serves it) | f929c623: expected failure | Missing: a deleted winner releasing its key |
| 22 | Collection, index and schema DDL replication | ReplicationTests: `Collections_and_indexes_created_and_dropped_on_one_member_follow_on_the_other`; CollectionSchemaReplicationTests (2) | RepairAndContractE2ETests: `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another`; every cluster start (collection create) | expected failure: index, schema and drop replication through the metadata documents (an index made in gru never reaches sjc) | f929c623: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 23 | Database deleted at home not brought back | CrossRegionTests: `A_database_deleted_in_its_home_is_not_brought_back…`; CrossRegionDeletionTests | RepairAndContractE2ETests: `A_database_deleted_while_a_region_is_down_does_not_come_back` (category fly-outage) | expected failure (R10a + R7); **BLOCKED on Fly** | f929c623: expected failure | Missing: checking fra's file is removed from disk. Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 24 | Catalog: database created in one region usable in another; same-name race | CrossRegionTests: `A_commit_registers_a_database_under_a_chosen_id_once` | CatalogE2ETests: E12 | expected failure (R10a); **BLOCKED on Fly** | f929c623: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 25 | Placement and move | CrossRegionTests: placement tests (5) | CatalogE2ETests: E13 | expected failure (R11); **BLOCKED on Fly** | f929c623: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`). `fly-replay` itself is covered by #50's Fast tests, not here |
| 26 | Changes feed across regions | (none: the per-collection feed and shapes were deleted in R2c) | LatticeE2ETests: E10 | expected failure (R8) | f929c623: expected failure | |
| 27 | SQL writes merge across regions | (none) | LatticeE2ETests: E11 | expected failure (R9) | f929c623: expected failure | |
| 28 | ts-auth refresh reuse across regions | (none) | LatticeE2ETests: E15 (storage half) | expected failure (R8; R12 for the ts-auth half) | f929c623: expected failure | |
| 29 | Replication observability (lag, health, metrics) | Slow/ReplicationObservabilityTests (2); CrossRegionTests: `Both_regions_meter_encoded_replication_bytes_by_direction…`; ReplicationStatusTests (3) | RepairAndContractE2ETests: `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` (`/health/replication` status ok with both peers and lag ≤ 5 s; `terrabase_replication_tail_lag` on `/metrics`) | expected failure: the CDC tail lag metric `terrabase_replication_tail_lag` (send and ack lag exist) | f929c623: expected failure | Metering per direction is not covered (R4a metric names are not published yet) |

## Counts (2026-10-12, combined candidate f929c623)

| Row status | Rows |
| --- | ---: |
| pass (in-process nodes; partitions simulated) | 17 |
| partial | 2 (9, 12) |
| expected failure | 10 (20 to 29) |
| missing (no replacement) | 0 |

No row passes on separate OS processes or on Fly. Sub-scenarios still without a test: power loss (12), a slow fsync (13;
the stall holds the writer), the 24 h / 1 GiB retention cut (14, an R6 Fast requirement), corruption and duplication
inside the peer protocol (19), a deleted unique winner releasing its key (21), file removal of a deleted database (23)
and metering per direction (29).

## Test hooks (owned by the R3 owner)

The R3 owner is the sole writer of the shared harness. At the candidate: `Cluster.Partition`/`Heal` cut and reopen each
node's peer links (simulated partition), `Cluster.RouteThrough` sends a node's peer connections through a `LossyLink`, and
`Cluster.StallStorageAsync` holds a node's single writer. Still missing: node processes as regions (a `terrabase`
process needs a region identity and its peers' addresses off Fly), which keeps the replicated SIGKILL scenario not
supported.

## Skipped tests and why

| Test | Skipped when | Reason |
| --- | --- | --- |
| FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` | locally, or on Fly without `TERRABASE_E2E_FLY_OUTAGE=1` | It stops a machine of the shared Fly app, so it runs alone through `make terrabase-e2e-fly-outage` |
| BenchmarkClusterHost: `Hosts_three_local_regions…` | always, unless `TERRABASE_BENCH_CLUSTER_FILE` is set | It is the benchmark's local cluster host, not a scenario |
| ThreeRegionFaultE2ETests: partition, backlog, both skew tests | on Fly | Fly has no switch for a partition or a clock skew between machines |
| ThreeRegionFaultE2ETests: region down, restart, ack-then-stop, restarted backlog; RepairAndContractE2ETests: deleted database | on Fly without `TERRABASE_E2E_FLY_OUTAGE=1` | They stop Fly machines |
| CrashAndStallE2ETests (all but the proxy self-check); RepairAndContractE2ETests: anti-entropy repair, restore, unique under partition | on Fly | They kill local processes, lock or edit local files, or partition; Fly has no such switch |

## Keeping this current

Update a row in the same PR that changes its status: a slice that lands removes its `Gaps.cs` entries and marks the row
"pass" with the commit of the run that proved it. Re-run `make terrabase-e2e` locally and `make terrabase-e2e` plus
`make terrabase-e2e-fly-outage` on Fly at the release commit, and record both in "Last run".
