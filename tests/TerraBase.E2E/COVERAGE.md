# Replication scenario coverage: old suite → design C

R2a-1 (#48, merged 2026-10-12) deleted the old replication tests: about 200 Fast and Slow tests and the binary harness
(`CrashableTerraBase`, `PartitionablePeer`, `FakeRegionNetwork`, `RegionBus`). A green Slow run proves none of those
scenarios for design C. This table maps each scenario to the test that replaces it and records whether the replacement
runs against the new replicated path.

**Since R4a-2 (#87, merged 2026-10-12):** peer streams carry change groups between machines, but only the CDC tailer
(R3) produces them from real writes. Until R3 lands nothing replicates. On main every replicated scenario therefore stops
in the cluster's setup (the collection created in gru never reaches sjc), before its own assertions.

**Release rule.** Release correctness and the 1M-document acceptance stay **BLOCKED** until every row's replacement passes
against the design-C replicated path at the exact release commit, both locally and on ts-orch. A row that is missing or an
expected failure blocks. The obsolete binary harness is not coming back: crash and storage-stall scenarios need new
harness work on the design-C transport (listed under "Missing").

Status words:

- **pass**: the replacement runs on the design-C replicated path and passes.
- **expected failure (slice)**: the replacement exists and is a strict expected failure in `Gaps.cs` until that slice
  lands. It counts only when the scenario fails its own way with the gap's declared signature (`Expected.cs`). A 5xx, a
  crash, a lock or a harness error fails the test. It fails as soon as it passes.
- **setup-blocked (slice)**: the scenario never reaches its assertions, because the cluster's setup cannot finish without
  that slice. It is reported as *not supported*, never as an expected failure. Every replicated row is in this state on
  main.
- **missing**: no replacement yet.
- No row is skipped on ts-orch for its credential: every request carries the e2e project's ts-auth project token
  (`databases:*`, `documents:*`, minted from `TERRABASE_E2E_ORCH_KEY`, docs/terrabase/e2e-ts-orch.md), so an authentication
  refusal fails the test and is never counted as a gap. Scenarios that stop ts-orch regions keep their own guard and run
  only in the pinned outage run.

## Last runs

Summaries come from `make terrabase-e2e` itself (`tests/TerraBase.E2E/Outcomes.cs`, `scripts/terrabase/e2e-summary.sh`),
never from a pass count. Each test declares its nodes and faults (`ScenarioAttribute`); each line says whether it ran on
in-process nodes, separate OS processes or no node (harness check), and whether its partition was simulated or its fault
a TCP-proxy network fault. CI's `terrabase-check` rejects a block here or in `RUNS.md` that lacks a category, the commit,
the working-tree patch identity, the stacked-on line or a test's nodes and faults, or whose counts do not add up.

**Acceptance candidates** are published on #108 by the strict-test owner (one named candidate per batch, frozen branch).
Each counts once the R3 owner acknowledges its exact SHA and both owners' 3 repeats are posted there.
- **Recorded: candidate-3 `ab499d84`** (`test/candidate-3`, on #112 `8b8758ff`): 45 actual pass / 4 harness check /
  8 expected failure / 0 not supported / 3 skip / 0 fail of 60, identical over 6 runs (3 per owner). 42 passes ran on
  in-process nodes and 3 on separate OS processes with SIGKILL.
- **Pending acknowledgement:** candidate-4 `4193f8fe` and candidate-5 `77f0072c`.
- **Historical:** `f929c623` and `f1ddb954`, each under its own SHA.

The row-by-row record is `COVERAGE.md` on #108's branch. This file on main reports main. R3, R4a, R5, R6 and R8 are on
main, so every scenario runs plainly except the five expected failures that wait on a slice not on main (`Gaps.cs`: E11
R9, E12 and the deleted-database scenario R10a #116/#121, E13 R11, E14 R7 #120) and three not-supported harness
placeholders. Status cells in the scenario table below predate this and are history.

Two runs of main `e30ffdc2` plus the honest-gaps change (`f1cfbc0f`), 2026-10-13, on **delta** (16 CPUs; `nice -n 19`,
under the heavy lock, `flock -o /tmp/ts-heavy.lock`). They are identical test by test; both blocks are in `RUNS.md`:

| Run | Commit | Stacked on | Working tree | actual pass | harness check | expected failure | not supported | skip | fail | total | Host load at start → end (1/5/15 min) |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 of 2 | `f1cfbc0f` | none (origin/main `e30ffdc28753`) | clean | 44 | 19 | 5 | 3 | 4 | 0 | 75 | 10.54 8.36 7.21 → 6.44 11.14 9.12 |
| 2 of 2 | `f1cfbc0f` | none (same) | clean | 44 | 19 | 5 | 3 | 4 | 0 | 75 | 6.44 11.14 9.12 → 4.47 9.94 9.23 |

The last run in full:

<!-- e2e-summary -->
E2E run (honest gaps f1cfbc0f, run 2 of 2 (delta)), 2026-10-13T04:34Z on delta

- Commit: `f1cfbc0f50c9f46d6efc8779cd8a20fcba266978`; working tree: clean
- Stacked on: none (merge base with origin/main `e30ffdc287530551b161b0130220be53d3c0475e`)

| Category | Tests |
| --- | ---: |
| actual pass | 44 |
| harness check | 19 |
| expected failure | 5 |
| not supported | 3 |
| skip | 4 |
| fail | 0 |
| total | 75 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a, #116/#121), then garbage collection (R7, #120); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | expected failure | in-process nodes | none | SQL writes over peer streams (R9, no PR yet); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a, #116/#121); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | expected failure | in-process nodes | none | home region and live move (R11, no PR yet); failed as named: HttpRequestException with "unknown_route" |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | expected failure | in-process nodes | NodeStop | garbage collection (R7, #120 / R7-FIX); failed as named: ScenarioException with "GC collects the tombstones of deleted documents" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_delayed_sync_holds_every_commit_for_at_least_the_delay` | harness check | no node (harness) | none |  |
| `A_failed_sync_fails_the_commit_and_the_database_takes_writes_again_after_a_restart` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_kill_at_a_sync_stops_the_process_before_it_and_keeps_what_it_wrote` | harness check | no node (harness) | none |  |
| `A_machine_pinned_request_is_accepted_only_from_that_machine_in_its_region(gru, m-gru, True)` | harness check | no node (harness) | none |  |
| `A_machine_pinned_request_is_accepted_only_from_that_machine_in_its_region(gru, m-other, False)` | harness check | no node (harness) | none |  |
| `A_machine_pinned_request_is_accepted_only_from_that_machine_in_its_region(null, null, False)` | harness check | no node (harness) | none |  |
| `A_machine_pinned_request_is_accepted_only_from_that_machine_in_its_region(sjc, m-gru, False)` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `A_power_loss_after_a_kill_at_a_sync_keeps_exactly_the_commits_that_returned` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | not supported | in-process nodes | StorageStall | E2E harness: the storage stall hook (Cluster.StallStorageAsync) not wired to this scenario; no storage stall hook: cannot stall sjc's database file for 10 s while gru, sjc, fra write; it needs Cluster.StallStorageAsync (#106) |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | E2E harness: node processes as regions (RegionProcesses) not wired to this scenario; node processes cannot form a three-region cluster before peer streams (R4a); a single node process serves alone |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | not supported | in-process nodes | StorageStall | E2E harness: the storage stall hook (Cluster.StallStorageAsync) not wired to this scenario; no storage stall hook: cannot stall gru's database file for 5 s while gru write; it needs Cluster.StallStorageAsync (#106) |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_power_loss_keeps_the_catalog_and_the_api_keys_the_system_database_acknowledged` | actual pass | separate OS processes | ProcessKill, FileTampering |  |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | actual pass | in-process nodes | none |  |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | actual pass | in-process nodes | none |  |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | actual pass | in-process nodes | none |  |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | actual pass | in-process nodes | ClockSkew |  |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | actual pass | in-process nodes | ClockSkew |  |
| `Concurrent_inc_in_three_regions_sums` | actual pass | in-process nodes | none |  |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_different_fields_both_survive` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | actual pass | in-process nodes | none |  |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | actual pass | in-process nodes | SimulatedPartition, ClockSkew |  |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | actual pass | in-process nodes | none |  |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | actual pass | in-process nodes | none |  |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | actual pass | in-process nodes | none |  |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order` | actual pass | in-process nodes | none |  |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins` | actual pass | in-process nodes | none |  |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | actual pass | in-process nodes | none |  |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | actual pass | separate OS processes | ProcessKill |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | actual pass | in-process nodes | SimulatedPartition |  |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | actual pass | in-process nodes | none |  |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | actual pass | in-process nodes | none |  |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | actual pass | in-process nodes | none |  |
| `Writes_a_region_acknowledged_before_it_could_ship_them_reach_every_region_after_a_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Commits_one_row_per_transaction_each_synced` | skip | separate OS processes | none | the process of WalSyncFaultTests: started by it with the fault shim loaded |
| `Hosts_one_region_until_its_process_is_killed` | skip | separate OS processes | ProcessKill | a region of RegionProcesses: started by the harness in a process of its own |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

## Scenarios

| # | Scenario | Old test (deleted in #48) | Replacement (tests/TerraBase.E2E) | On the design-C replicated path? | Last run | Notes |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | Write in one region readable in the others | Fast/CrossRegion/CrossRegionTests: `A_write_in_either_region_is_readable_in_the_other_with_the_same_version` | ReplicationE2ETests: `Write_in_gru_is_readable_in_sjc…` (E1), `Write_in_sjc_is_readable_in_gru…` (E2); ThreeRegionE2ETests: `Write_in_one_region_is_readable_in_the_other_two` ×6 pairs | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | All 3 pairs, both ways |
| 2 | Concurrent same-field writes, 2-way | Fast/Replication/ReplicationTests: `Two_nodes_that_write_at_the_same_time_converge`; CrossRegionTests: `Concurrent_writes_in_both_regions_converge_on_the_same_documents` | ReplicationE2ETests: `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` (E3) | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | |
| 3 | Concurrent same-field writes, 3-way | (none; the old suite had 2 nodes or 2 regions) | ThreeRegionE2ETests: `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value`; `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | The benchmark adds 200 docs × 5 rounds per region (`benchmark/bench`) |
| 4 | Concurrent different-field writes, 2-way | (none; the old engine kept whole-document LWW) | ReplicationE2ETests: `Concurrent_writes_to_different_fields_both_survive…` (E4) | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | Per-field lattice R1/R2a, replicated by R4a |
| 5 | Concurrent different-field writes, 3-way | (none) | ThreeRegionE2ETests: `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | |
| 6 | Delete vs concurrent edit | ReplicationTests: `A_version_read_on_one_member_deletes_the_document_on_the_other`; Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: `Delete_in_one_region_and_edit_in_the_other…` (E5); ThreeRegionE2ETests: `Delete_in_one_region_and_edits_in_the_other_two…` | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | Many concurrent deletes: the benchmark's 1 % deletes |
| 7 | Partition, then heal | CrossRegionTests: `Partitioned_regions_keep_serving_their_writes_and_converge_after_heal`; Slow/CrossRegion/CrossRegionPartitionTests: `Regions_converge_without_losing_or_corrupting_writes_through_a_lossy_partitioned_link_and_restarts` | ThreeRegionFaultE2ETests: `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | `Cluster.Partition` throws until R4a wires its streams in. Local only; Fly has no partition switch (skipped there, with the reason) |
| 8 | Region down, then rejoin | Fast/CrossRegion/CrossRegionDeletionTests: `A_restarted_region_removes_…`; (ts-orch) OrchOutageE2ETests | ThreeRegionFaultE2ETests: `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down`; FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` (E7) | setup-blocked (R3); E7 also needs R6 | 94086cd6: not supported (setup-blocked) | Fly runs only through `make terrabase-e2e-fly-outage` |
| 9 | Node restart mid-replication (receiver); restore to an older state | ReplicationTests: `A_restarted_node_resumes_after_its_peers_applied_prefix`; Slow/ReceiverRestoreTests: `A_caught_up_sender_repairs_a_restarted_receiver_restored_to_an_older_frontier` | ThreeRegionFaultE2ETests: `Restart_in_the_middle_of_replication_loses_no_write`; RepairAndContractE2ETests: `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | setup-blocked (R3; restore: R6) | 94086cd6: not supported (setup-blocked) | Restore copies sjc's data directory back to a snapshot taken 100 writes earlier |
| 10 | Sender restarts with an unsent backlog | Fast/Replication/HintedHandoffTests: `A_restarted_sender_replays_durable_backlog_when_its_peer_returns_without_new_writes` | ThreeRegionFaultE2ETests: `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | New in this change |
| 11 | Clock skew | Fast/Replication/ReplicatedStampTests: `A_stamp_far_ahead_is_applied_and_counted_but_moves_the_clock_only_the_skew_ahead` | ThreeRegionFaultE2ETests: `Causally_later_write_wins_everywhere_despite_clock_skew` (±2 s), `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | Missing: the `terrabase.clock.skew_capped` counter assertion (R4a metrics). Local only (skipped on Fly) |
| 12 | Acknowledged-write durability: ack, then crash | Slow/ReplicationChaosTests: `Three_nodes_preserve_acknowledged_writes_after_partition_skew_storage_stall_and_crash`, `The_history_checker_rejects_a_lost_or_changed_acknowledged_write` | CrashAndStallE2ETests: `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` (single node, real SIGKILL of a `terrabase` process, NodeProcess harness); `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` (replicated); ThreeRegionFaultE2ETests: `Writes_acknowledged_just_before_their_region_stops…` (clean stop) | single node: **pass**; replicated: not supported (node processes as regions are #111's `RegionProcesses`, not on main) | 2e937568: single node pass (555 acknowledged, all survived); replicated expected failure | Process kill, not power loss: an OS crash can lose the last commits with `synchronous = NORMAL` (design §18), which no local test simulates |
| 13 | Storage stall | Slow/ReplicationChaosTests (storage stall phase); Fast/Replication/CaptureAdmissionTests | CrashAndStallE2ETests: `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` (single node); `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` (sjc stalls 10 s) | single node: **not supported** (since #98 the engine holds the file's write lock; the in-process stall hook is R3's #106); replicated: setup-blocked (R3) | 2e937568: single node not supported; replicated expected failure | The stall is the database file held by another writer (`BEGIN` plus a write on a separate connection), so commits wait. A slow fsync would need an IO-delay shim, which is not built |
| 14 | CDC / replication backlog drain; flow control | Slow/ReplicationSlowTests: `Advancing_ACKs_keep_a_bandwidth_limited_window_alive…`, `A_database_written_without_pause_does_not_hold_back_the_others`; ReplicationTests: `Exhausted_byte_credit_blocks…`, `The_sender_stays_within_the_byte_credit…` | ThreeRegionFaultE2ETests: `Replication_backlog_of_3000_writes_drains_after_the_region_returns`; RepairAndContractE2ETests: `A_hot_database_does_not_hold_back_replication_of_another` | setup-blocked (R3; flow control also R10a) | 94086cd6: not supported (setup-blocked) | Flow control runs on Fly with the project token and deletes its hot database. The 24 h / 1 GiB retention cut and `refill_required` cannot run in real time end to end. R6 must ship them as a Fast test with a fake clock; that requirement is tracked here |
| 15 | Large documents | CrossRegionTests: `A_group_larger_than_one_reply_crosses_in_parts` | ThreeRegionE2ETests: `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` (900 KiB, 2000 fields) | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | |
| 16 | Counters (PN) | (none) | LatticeE2ETests: E8; ThreeRegionE2ETests: `Concurrent_inc_in_three_regions_sums` | setup-blocked (R5 + R3) | 94086cd6: not supported (setup-blocked) | R5 is #75 |
| 17 | Sets (add-wins) | (none) | LatticeE2ETests: E9; ThreeRegionE2ETests: `Concurrent_set_adds_in_three_regions_union…` | setup-blocked (R5 + R3) | 94086cd6: not supported (setup-blocked) | |
| 18 | Bulk convergence with counts and digests | Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: E6 (1000); ThreeRegionE2ETests: `Bulk_3000_documents…`; benchmark `repl-bench` (1M) | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | The 1M run on design C waits for R4a |
| 19 | Lossy link (drop, delay, reset) | Slow/CrossRegionPartitionTests (lossy link); Fast/Replication/SyncProtocolTests, ResponseLimitTests, PeerTransportTests | CrashAndStallE2ETests: `Writes_converge_exactly_over_lossy_peer_links` (LossyLink TCP proxy: 200 ms delay, 256 KiB/s, 5 % resets per second); `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` (the proxy itself) | setup-blocked (R3; `Cluster.RouteThrough` lands with it); proxy self-check: **pass** | 94086cd6: not supported (setup-blocked) | Byte corruption and frame duplication need fault injection inside the peer protocol (R4a), not a TCP proxy: design §9 refuses a corrupt group at the trust boundary |
| 20 | Anti-entropy repair of rows the streams missed | Fast/Replication/AntiEntropyTests: `Caught_up_members_repair_missing_and_stale_rows_without_new_writes` and 11 others | RepairAndContractE2ETests: `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` (deletes the document's rows from sjc's file while it is down; only a repair can bring it back) | setup-blocked (R6 + R3) | 94086cd6: not supported (setup-blocked) | 200 s window: rounds every 60 s, repair after two differing rounds |
| 21 | Unique constraints under partition | Fast/Replication/UniqueReplicationTests: `Partitioned_unique_writes_converge_and_a_deleted_winner_releases_its_key`; Slow/UniqueConstraintReplayTests | RepairAndContractE2ETests: `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | Missing: a deleted winner releasing its key |
| 22 | Collection, index and schema DDL replication | ReplicationTests: `Collections_and_indexes_created_and_dropped_on_one_member_follow_on_the_other`; CollectionSchemaReplicationTests (2) | RepairAndContractE2ETests: `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another`; every cluster start (collection create) | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | Fly: runs with the project token |
| 23 | Database deleted at home not brought back | CrossRegionTests: `A_database_deleted_in_its_home_is_not_brought_back…`; CrossRegionDeletionTests | RepairAndContractE2ETests: `A_database_deleted_while_a_region_is_down_does_not_come_back` (category fly-outage) | setup-blocked (R10a + R7) | 94086cd6: not supported (setup-blocked) | Missing: checking fra's file is removed from disk. Fly: stops a machine, so only the pinned disposable outage run |
| 24 | Catalog: database created in one region usable in another; same-name race | CrossRegionTests: `A_commit_registers_a_database_under_a_chosen_id_once` | CatalogE2ETests: E12 | setup-blocked (R10a) | 94086cd6: not supported (setup-blocked) | Fly: strict expected failure with the project token |
| 25 | Placement and move | CrossRegionTests: placement tests (5) | CatalogE2ETests: E13 | setup-blocked (R11) | 94086cd6: not supported (setup-blocked) | Fly: strict expected failure with the project token. `fly-replay` itself is covered by #50's Fast tests, not here |
| 26 | Changes feed across regions | (none: the per-collection feed and shapes were deleted in R2c) | LatticeE2ETests: E10 | setup-blocked (R8) | 94086cd6: not supported (setup-blocked) | |
| 27 | SQL writes merge across regions | (none) | LatticeE2ETests: E11 | setup-blocked (R9) | 94086cd6: not supported (setup-blocked) | |
| 28 | ts-auth refresh reuse across regions | (none) | LatticeE2ETests: E15 (storage half) | setup-blocked (R5 + R8); the ts-auth half is R12 | 94086cd6: not supported (setup-blocked) | |
| 29 | Replication observability (lag, health, metrics) | Slow/ReplicationObservabilityTests (2); CrossRegionTests: `Both_regions_meter_encoded_replication_bytes_by_direction…`; ReplicationStatusTests (3) | RepairAndContractE2ETests: `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` (`/health/replication` status ok with both peers and lag ≤ 5 s; `terrabase_replication_tail_lag` on `/metrics`) | setup-blocked (R3) | 94086cd6: not supported (setup-blocked) | Metering per direction is not covered (R4a metric names are not published yet) |

## Counts (2026-10-12, at 94086cd6 on main)

| Status | Rows |
| --- | ---: |
| pass on design C (replicated) | 0 |
| setup-blocked (a slice) | 29 |
| missing (no replacement) | 0 |

Row 12 also has a single-node half that passes on the current write path (SIGKILL durability). Row 13's single-node half
is not supported on main until R3's in-process stall hook.

Sub-scenarios still without a test, named in their notes:
- power loss (12);
- a slow fsync (13);
- the 24 h / 1 GiB retention cut (14, an R6 Fast requirement);
- corruption and duplication inside the peer protocol (19);
- a deleted unique winner releasing its key (21);
- file removal of a deleted database (23);
- metering per direction (29).

## Test hooks (R3 owner, not on main yet)

The R3 owner is the sole writer of the shared harness. Its hooks live in R3's stack, not on main:
- `Cluster.Partition`/`Heal` and `RouteThrough` (#106 `2ee8b148`);
- `Cluster.StallStorageAsync` (#106 `b838729e`);
- `RegionProcesses` for node processes as regions (#111 `07f8d81f`);
- the PeerLink abort fix (local `a77a7574`/`80c67345`).

The candidates on #108 run on them.

## Skipped tests and why

| Test | Skipped when | Reason |
| --- | --- | --- |
| OrchOutageE2ETests: `Each_region_stopped_then_started…` | locally, or on ts-orch without `TERRABASE_E2E_ORCH_OUTAGE=1` | It stops regions, so it runs alone through `make terrabase-e2e-orch-outage`, and only in a pinned run (`Cluster.LiveOutage`) of the disposable service `terrabase-e2e`, never production |
| BenchmarkClusterHost: `Hosts_three_local_regions…` | always, unless `TERRABASE_BENCH_CLUSTER_FILE` is set | It is the benchmark's local cluster host, not a scenario |
| PitrLabHost: `Runs_the_PitrLab_job…` | always, unless `TERRABASE_PITR_JOB` is set | It is the PitrLab backup/PITR harness host (benchmark/docs/pitr-100m.md), not a scenario |
| ThreeRegionFaultE2ETests: partition, backlog, both skew tests; WaveAE2ETests and ShapeConvergenceE2ETests partition, loss and skew tests | on ts-orch | ts-orch has no switch for a partition, a file edit or a clock skew between nodes |
| ThreeRegionFaultE2ETests: region down, restart, ack-then-stop, restarted backlog; RepairAndContractE2ETests: deleted database | on ts-orch without `TERRABASE_E2E_ORCH_OUTAGE=1` | They stop ts-orch regions |
| CrashAndStallE2ETests (all but the proxy self-check); RepairAndContractE2ETests: anti-entropy repair, restore, unique under partition | on ts-orch | They kill local processes, lock or edit local files, or partition; ts-orch has no such switch |

## Keeping this current

Update a row in the same PR that changes its status: a slice that lands removes its `Gaps.cs` entries and marks the row
"pass" with the commit of the run that proved it. Re-run `make terrabase-e2e` locally and `make terrabase-e2e` plus
`make terrabase-e2e-orch-outage` on ts-orch at the release commit, and record both in "Last run".

## Backup qualification producer (#164)

`BackupQualificationE2ETests` uses a real three-node local cluster or a pinned ts-orch run. It takes a ready
manual backup and two shipped PIT cuts, performs actual HTTP restores, and feeds their count, canonical digest,
versions and source/placement identity to `BackupQualification.Failures`. A pre-window cut must answer exactly
400 `invalid_request`. Supported-cut refusals fail. The same-body rewrite regression proves version loss fails
independently of the body digest. This row is not a `Gaps` expected failure.

Local backup storage uses test-only S3 transport; a local result is not Tigris evidence. The ts-orch producer uses
the run's project token (database scopes, from `TERRABASE_E2E_ORCH_KEY`), creates and cleans up its own source and
restored databases, and fails if unpinned or missing that credential. Target: the disposable service `terrabase-e2e`. Per-cut details are retained in the run's
`TERRABASE_E2E_RESULTS` JSONL and console log. Exact acknowledgement-time cuts before convergence, shipping faults,
lease handoff and restored multi-region placement are not qualified by this scenario.

## Shape publication durability (FR-25)

`ShapeDurabilityE2ETests` drives the real shape sealer in separate gru/sjc operating-system processes through a
test-only probe. The peer refuses database applies before storing them; catalog replication continues. Both served
chunks and raw persisted chunks exclude the unacknowledged insertion and overwrite. After SIGKILL of the origin,
the peer lacks both writes. A second scenario proves that a remotely acknowledged frontier and safe immutable chunk
survive origin SIGKILL/restart. These scenarios passed locally; they do not qualify a Fly deployment or power loss.

The probe bypasses endpoint wiring owned by FR-20. Fast tests cover held/refused acknowledgements, publication after
ack, immutable old values during an unacknowledged overwrite, worker wake, missing capture, and cell-of-one commits.

## Sealed shape chunks under power loss (V1)

`ShapeSealPowerLossTests` loads the write-ahead log fault shim into both gru and sjc processes
(`RegionProcesses.StartWithWalFaultsAsync`). After remote durable writes are sealed into snapshot pages and chunks
(shape HTTP API on the public listener, plus the probe's served and persisted chunk rows), one tenant-log sync is
killed: a gru write after the seals, a seal's own commit, or sjc's apply of the next batch. Every unsynced byte is
dropped, then the dropped frames but the last are written back with a torn commit frame. After restart every op of
every chunk, page and persisted row resolves to its document value on gru (served ones on sjc too), served chunks keep
their range and body, and the old HTTP handle resumes or answers 409 `must_refetch`. Six seed/cut pairs; results in
`benchmark/bench/2026-10-14-replication-v1.md`. The shim models only log cuts: torn main-database pages after a
checkpoint are not simulated, and no Fly machine lost power.

## Shape cache headers (FR-24)

`ShapeCacheE2ETests` runs once per serving origin (gru, sjc, fra) of a three-region cluster, pinned to one machine for the
shape handle's lifetime, through the public shape, manifest (FR-22) and presigned-object paths. It seals a chunk, then
fetches it from its presigned URL with no TerraBase credential. Every answer must be the manifest's TBS1 bytes (size,
SHA-256) with `public, max-age=31536000, immutable`; the bootstrap 307, snapshot page, chunk, manifest and bounded live tail
and malformed-offset errors must be `private, no-store`. The object is fetched twice, with identical ciphertext required.
Cache-hit evidence and origin storage-read counts per cache location belong to V3 (#305). This test is outside
`local-only`, so the post-deploy e2e job's `Category!=local-only` run on `main` covers it with the GitHub OIDC project
token. It needs FR-22 (#283) and FR-24 (#315) deployed.
