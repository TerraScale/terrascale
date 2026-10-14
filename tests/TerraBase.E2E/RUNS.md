# E2E runs

Full summaries of the repeat runs `COVERAGE.md` lists, as `make terrabase-e2e` printed them. Newest last. CI's
`terrabase-check` validates every block (`scripts/terrabase/e2e-summary.sh check`).

## Main `e30ffdc2` plus the honest-gaps change `f1cfbc0f` (delta, 2 runs)

<!-- e2e-summary -->
E2E run (honest gaps f1cfbc0f, run 1 of 2 (delta)), 2026-10-13T04:30Z on delta

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

## Main plus #105 at `94086cd6` (mariogk-dev, 3 runs)

The earlier main runs at `2e937568` (permissive recorder) are kept, unchanged, in git history of this file.

<!-- e2e-summary -->
E2E run (#105 94086cd6 on main, run 1 of 3 (mariogk-dev)), 2026-10-12T10:52Z on mariogk-dev

- Commit: `94086cd6f6eba4a34ce4daa943e7c58ce1646dd6`; working tree: clean
- Stacked on: none (merge base with origin/main `740ad0d6b2d0c409e2606ee187b6c77892803e8e`)

| Category | Tests |
| --- | ---: |
| actual pass | 1 |
| harness check | 11 |
| expected failure | 0 |
| not supported | 48 |
| skip | 2 |
| fail | 0 |
| total | 62 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | not supported | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_hot_database_does_not_hold_back_replication_of_another` | not supported | in-process nodes | none | peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | not supported | in-process nodes | NodeStop, FileTampering | refill and anti-entropy (R6) over peer streams fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | not supported | in-process nodes | StorageStall | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | not supported | in-process nodes | NodeStop, FileTampering | anti-entropy (R6) over peer streams fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | not supported | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | not supported | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_inc_in_three_regions_sums` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | not supported | in-process nodes | SimulatedPartition, ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail` | not supported | in-process nodes | none | the changes feed (R8) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | not supported | in-process nodes | none | SQL writes (R9) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | not supported | in-process nodes | none | the catalog as a lattice database (R10a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | not supported | in-process nodes | none | home region and live move (R11); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | not supported | in-process nodes | none | garbage collection (R7) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail` | not supported | in-process nodes | none | the changes feed (R8) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | not supported | in-process nodes | none | replication metrics with the CDC source (R3): tail lag, and stream lags that move; never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Restart_in_the_middle_of_replication_loses_no_write` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | the CDC change group source (R3) for the peer streams (R4a); node processes cannot form a three-region cluster before peer streams (R4a); a single node process serves alone |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_converge_exactly_over_lossy_peer_links` | not supported | in-process nodes | NetworkFault | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | not supported | in-process nodes | StorageStall | a storage stall hook for the Turso engine (#98 holds the file's write lock); no storage stall hook: cannot stall gru's database file for 5 s while gru write; it needs Cluster.StallStorageAsync (#106) |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (#105 94086cd6 on main, run 2 of 3 (mariogk-dev)), 2026-10-12T10:53Z on mariogk-dev

- Commit: `94086cd6f6eba4a34ce4daa943e7c58ce1646dd6`; working tree: clean
- Stacked on: none (merge base with origin/main `740ad0d6b2d0c409e2606ee187b6c77892803e8e`)

| Category | Tests |
| --- | ---: |
| actual pass | 1 |
| harness check | 11 |
| expected failure | 0 |
| not supported | 48 |
| skip | 2 |
| fail | 0 |
| total | 62 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | not supported | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_hot_database_does_not_hold_back_replication_of_another` | not supported | in-process nodes | none | peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | not supported | in-process nodes | NodeStop, FileTampering | refill and anti-entropy (R6) over peer streams fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | not supported | in-process nodes | StorageStall | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | not supported | in-process nodes | NodeStop, FileTampering | anti-entropy (R6) over peer streams fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | not supported | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | not supported | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_inc_in_three_regions_sums` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | not supported | in-process nodes | SimulatedPartition, ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail` | not supported | in-process nodes | none | the changes feed (R8) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | not supported | in-process nodes | none | SQL writes (R9) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | not supported | in-process nodes | none | the catalog as a lattice database (R10a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | not supported | in-process nodes | none | home region and live move (R11); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | not supported | in-process nodes | none | garbage collection (R7) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail` | not supported | in-process nodes | none | the changes feed (R8) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | not supported | in-process nodes | none | replication metrics with the CDC source (R3): tail lag, and stream lags that move; never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Restart_in_the_middle_of_replication_loses_no_write` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | the CDC change group source (R3) for the peer streams (R4a); node processes cannot form a three-region cluster before peer streams (R4a); a single node process serves alone |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_converge_exactly_over_lossy_peer_links` | not supported | in-process nodes | NetworkFault | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | not supported | in-process nodes | StorageStall | a storage stall hook for the Turso engine (#98 holds the file's write lock); no storage stall hook: cannot stall gru's database file for 5 s while gru write; it needs Cluster.StallStorageAsync (#106) |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (#105 94086cd6 on main, run 3 of 3 (mariogk-dev)), 2026-10-12T10:54Z on mariogk-dev

- Commit: `94086cd6f6eba4a34ce4daa943e7c58ce1646dd6`; working tree: clean
- Stacked on: none (merge base with origin/main `740ad0d6b2d0c409e2606ee187b6c77892803e8e`)

| Category | Tests |
| --- | ---: |
| actual pass | 1 |
| harness check | 11 |
| expected failure | 0 |
| not supported | 48 |
| skip | 2 |
| fail | 0 |
| total | 62 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | not supported | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_hot_database_does_not_hold_back_replication_of_another` | not supported | in-process nodes | none | peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | not supported | in-process nodes | NodeStop, FileTampering | refill and anti-entropy (R6) over peer streams fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | not supported | in-process nodes | StorageStall | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | not supported | in-process nodes | NodeStop, FileTampering | anti-entropy (R6) over peer streams fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | not supported | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | not supported | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_inc_in_three_regions_sums` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | not supported | in-process nodes | SimulatedPartition, ClockSkew | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail` | not supported | in-process nodes | none | the changes feed (R8) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | not supported | in-process nodes | none | SQL writes (R9) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | not supported | in-process nodes | none | the catalog as a lattice database (R10a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | not supported | in-process nodes | none | home region and live move (R11); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | not supported | in-process nodes | none | garbage collection (R7) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail` | not supported | in-process nodes | none | the changes feed (R8) over peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail` | not supported | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | not supported | in-process nodes | none | replication metrics with the CDC source (R3): tail lag, and stream lags that move; never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Restart_in_the_middle_of_replication_loses_no_write` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | not supported | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | not supported | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | the CDC change group source (R3) for the peer streams (R4a); node processes cannot form a three-region cluster before peer streams (R4a); a single node process serves alone |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | not supported | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_converge_exactly_over_lossy_peer_links` | not supported | in-process nodes | NetworkFault | the CDC change group source (R3) for the peer streams (R4a); never reached its assertions: the cluster's setup cannot finish without it (NotConvergedException with "collection on sjc did not converge") |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | not supported | in-process nodes | StorageStall | a storage stall hook for the Turso engine (#98 holds the file's write lock); no storage stall hook: cannot stall gru's database file for 5 s while gru write; it needs Cluster.StallStorageAsync (#106) |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

## FR-25 process durability scenarios (working tree)

<!-- e2e-summary -->
E2E run (local), 2026-10-14T06:02Z on delta

- Commit: `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`; working tree: 2541e8771da12bfcd4c86c5dfa0eb8abdec26b4434cd538f11703bbb1347211c
- Stacked on: none (merge base with origin/main `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`)

| Category | Tests |
| --- | ---: |
| actual pass | 2 |
| harness check | 0 |
| expected failure | 0 |
| not supported | 0 |
| skip | 0 |
| fail | 0 |
| total | 2 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin` | actual pass | separate OS processes | ProcessKill |  |
| `The_remote_durable_frontier_survives_the_origins_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
<!-- /e2e-summary -->


<!-- e2e-summary -->
E2E run (local), 2026-10-14T06:46Z on delta

- Commit: `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`; working tree: ea3ec095bed736912489d31f2144b910779a1e7b351c4940d0f4190bc284f485
- Stacked on: none (merge base with origin/main `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`)

| Category | Tests |
| --- | ---: |
| actual pass | 2 |
| harness check | 0 |
| expected failure | 0 |
| not supported | 0 |
| skip | 0 |
| fail | 0 |
| total | 2 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin` | actual pass | separate OS processes | ProcessKill |  |
| `The_remote_durable_frontier_survives_the_origins_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
<!-- /e2e-summary -->


## FR-25 process durability after #286 rebase (committed tree)

<!-- e2e-summary -->
E2E run (local), 2026-10-14T08:22Z on delta

- Commit: `f280e7ae3bee9b2d955d1e924f83c6b135c2b08c`; working tree: clean
- Stacked on: none (merge base with origin/main `0eac37a89c7217d75275bfb543ef92d4680939b7`)

| Category | Tests |
| --- | ---: |
| actual pass | 2 |
| harness check | 0 |
| expected failure | 0 |
| not supported | 0 |
| skip | 0 |
| fail | 0 |
| total | 2 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin` | actual pass | separate OS processes | ProcessKill |  |
| `The_remote_durable_frontier_survives_the_origins_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
<!-- /e2e-summary -->


## FR-25 process durability after test isolation (committed tree)

<!-- e2e-summary -->
E2E run (local), 2026-10-14T08:29Z on delta

- Commit: `89f54c3e5f94f414b94fcf8e83b1c223888061c7`; working tree: clean
- Stacked on: none (merge base with origin/main `0eac37a89c7217d75275bfb543ef92d4680939b7`)

| Category | Tests |
| --- | ---: |
| actual pass | 2 |
| harness check | 0 |
| expected failure | 0 |
| not supported | 0 |
| skip | 0 |
| fail | 0 |
| total | 2 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin` | actual pass | separate OS processes | ProcessKill |  |
| `The_remote_durable_frontier_survives_the_origins_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
<!-- /e2e-summary -->


## FR-25 HTTP integration and bounded proof wait (remote committed tree)

<!-- e2e-summary -->
E2E run (local), 2026-10-14T13:19Z on mariogk-dev

- Commit: `3d1491bcf05caa6688f398333c953b8c2bb13379`; working tree: clean
- Stacked on: none (merge base with origin/main `778fba30cbfcc9c4e879578ad869e158480b51c2`)

| Category | Tests |
| --- | ---: |
| actual pass | 2 |
| harness check | 0 |
| expected failure | 0 |
| not supported | 0 |
| skip | 0 |
| fail | 0 |
| total | 2 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin` | actual pass | separate OS processes | ProcessKill |  |
| `The_remote_durable_frontier_survives_the_origins_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
<!-- /e2e-summary -->
