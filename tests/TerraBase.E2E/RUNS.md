# E2E runs

Full summaries of the repeat runs `COVERAGE.md` lists, as `make terrabase-e2e` printed them, grouped by candidate. Each block names its own commit, and results never carry over between candidates. Newest last. CI's
`terrabase-check` validates every block (`scripts/terrabase/e2e-summary.sh check`).

## Combined candidate `f929c623` (historical)

Its 3 blocks are kept in git, unchanged, at `test/e2e-candidate-f929c623:tests/TerraBase.E2E/RUNS.md` (commit `376044cf`).

## candidate-3 `ab499d84` (recorded earlier; superseded)

Its 6 blocks (3 per owner) are kept, unchanged, at `test/e2e-combined-candidate:tests/TerraBase.E2E/RUNS.md` (commit
`7dc901e9`).

## candidate-8 `70b55472` (recorded earlier; superseded)

Its 6 blocks (3 per owner) are kept, unchanged, at `test/record-candidate-8:tests/TerraBase.E2E/RUNS.md` (commit
`a933baad`).

## candidate-9 `1b68b852` (recorded; the strict owner's 3 runs, then the R3 owner's 3 runs)

<!-- e2e-summary -->
E2E run (candidate-9 1b68b852, run 1 of 3), 2026-10-12T11:18Z on delta

- Commit: `1b68b852b8f5775313d81d6028c347e46bfa02a5`; working tree: clean
- Stacked on: `dea6b364a8b345f652252256b3c2222ee5bc951f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 53 |
| harness check | 16 |
| expected failure | 4 |
| not supported | 0 |
| skip | 3 |
| fail | 0 |
| total | 76 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds` | expected failure | in-process nodes | none | home region and live move (R11); failed as named: HttpRequestException with "/move answered 404" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_failed_write_fails_the_gate_at_once_with_every_operations_outcome` | harness check | no node (harness) | none |  |
| `A_failed_write_is_a_real_failure_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Aborting_a_connection_twice_does_not_throw` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `Resetting_a_link_while_its_connections_close_never_throws` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `Waiting_for_every_write_fails_with_the_failed_operation` | harness check | no node (harness) | none |  |
| `A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order` | actual pass | in-process nodes | NodeStop |  |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | actual pass | in-process nodes | StorageStall |  |
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
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields` | actual pass | in-process nodes | none |  |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_ack_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_send_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_tail_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_reports_its_peers_and_replication_lag_on_health` | actual pass | in-process nodes | none |  |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | actual pass | separate OS processes | ProcessKill |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order` | actual pass | separate OS processes | ProcessKill |  |
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
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(False)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(True)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | actual pass | in-process nodes | StorageStall |  |
| `Hosts_one_region_until_its_process_is_killed` | skip | separate OS processes | none | a region of RegionProcesses: started by the harness in a process of its own |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (candidate-9 1b68b852, run 2 of 3), 2026-10-12T11:21Z on delta

- Commit: `1b68b852b8f5775313d81d6028c347e46bfa02a5`; working tree: clean
- Stacked on: `dea6b364a8b345f652252256b3c2222ee5bc951f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 53 |
| harness check | 16 |
| expected failure | 4 |
| not supported | 0 |
| skip | 3 |
| fail | 0 |
| total | 76 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds` | expected failure | in-process nodes | none | home region and live move (R11); failed as named: HttpRequestException with "/move answered 404" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_failed_write_fails_the_gate_at_once_with_every_operations_outcome` | harness check | no node (harness) | none |  |
| `A_failed_write_is_a_real_failure_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Aborting_a_connection_twice_does_not_throw` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `Resetting_a_link_while_its_connections_close_never_throws` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `Waiting_for_every_write_fails_with_the_failed_operation` | harness check | no node (harness) | none |  |
| `A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order` | actual pass | in-process nodes | NodeStop |  |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | actual pass | in-process nodes | StorageStall |  |
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
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields` | actual pass | in-process nodes | none |  |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_ack_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_send_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_tail_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_reports_its_peers_and_replication_lag_on_health` | actual pass | in-process nodes | none |  |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | actual pass | separate OS processes | ProcessKill |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order` | actual pass | separate OS processes | ProcessKill |  |
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
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(False)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(True)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | actual pass | in-process nodes | StorageStall |  |
| `Hosts_one_region_until_its_process_is_killed` | skip | separate OS processes | none | a region of RegionProcesses: started by the harness in a process of its own |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (candidate-9 1b68b852, run 3 of 3), 2026-10-12T11:24Z on delta

- Commit: `1b68b852b8f5775313d81d6028c347e46bfa02a5`; working tree: clean
- Stacked on: `dea6b364a8b345f652252256b3c2222ee5bc951f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 53 |
| harness check | 16 |
| expected failure | 4 |
| not supported | 0 |
| skip | 3 |
| fail | 0 |
| total | 76 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds` | expected failure | in-process nodes | none | home region and live move (R11); failed as named: HttpRequestException with "/move answered 404" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_failed_write_fails_the_gate_at_once_with_every_operations_outcome` | harness check | no node (harness) | none |  |
| `A_failed_write_is_a_real_failure_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Aborting_a_connection_twice_does_not_throw` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `Resetting_a_link_while_its_connections_close_never_throws` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `Waiting_for_every_write_fails_with_the_failed_operation` | harness check | no node (harness) | none |  |
| `A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order` | actual pass | in-process nodes | NodeStop |  |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | actual pass | in-process nodes | StorageStall |  |
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
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields` | actual pass | in-process nodes | none |  |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_ack_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_send_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_tail_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_reports_its_peers_and_replication_lag_on_health` | actual pass | in-process nodes | none |  |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | actual pass | separate OS processes | ProcessKill |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order` | actual pass | separate OS processes | ProcessKill |  |
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
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(False)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(True)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | actual pass | in-process nodes | StorageStall |  |
| `Hosts_one_region_until_its_process_is_killed` | skip | separate OS processes | none | a region of RegionProcesses: started by the harness in a process of its own |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (r3c9-1), 2026-10-12T11:29Z on delta

- Commit: `1b68b852b8f5775313d81d6028c347e46bfa02a5`; working tree: clean
- Stacked on: `dea6b364a8b345f652252256b3c2222ee5bc951f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 53 |
| harness check | 16 |
| expected failure | 4 |
| not supported | 0 |
| skip | 3 |
| fail | 0 |
| total | 76 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds` | expected failure | in-process nodes | none | home region and live move (R11); failed as named: HttpRequestException with "/move answered 404" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_failed_write_fails_the_gate_at_once_with_every_operations_outcome` | harness check | no node (harness) | none |  |
| `A_failed_write_is_a_real_failure_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Aborting_a_connection_twice_does_not_throw` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `Resetting_a_link_while_its_connections_close_never_throws` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `Waiting_for_every_write_fails_with_the_failed_operation` | harness check | no node (harness) | none |  |
| `A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order` | actual pass | in-process nodes | NodeStop |  |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | actual pass | in-process nodes | StorageStall |  |
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
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields` | actual pass | in-process nodes | none |  |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_ack_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_send_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_tail_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_reports_its_peers_and_replication_lag_on_health` | actual pass | in-process nodes | none |  |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | actual pass | separate OS processes | ProcessKill |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order` | actual pass | separate OS processes | ProcessKill |  |
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
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(False)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(True)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | actual pass | in-process nodes | StorageStall |  |
| `Hosts_one_region_until_its_process_is_killed` | skip | separate OS processes | none | a region of RegionProcesses: started by the harness in a process of its own |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (r3c9-2), 2026-10-12T11:34Z on delta

- Commit: `1b68b852b8f5775313d81d6028c347e46bfa02a5`; working tree: clean
- Stacked on: `dea6b364a8b345f652252256b3c2222ee5bc951f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 53 |
| harness check | 16 |
| expected failure | 4 |
| not supported | 0 |
| skip | 3 |
| fail | 0 |
| total | 76 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds` | expected failure | in-process nodes | none | home region and live move (R11); failed as named: HttpRequestException with "/move answered 404" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_failed_write_fails_the_gate_at_once_with_every_operations_outcome` | harness check | no node (harness) | none |  |
| `A_failed_write_is_a_real_failure_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Aborting_a_connection_twice_does_not_throw` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `Resetting_a_link_while_its_connections_close_never_throws` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `Waiting_for_every_write_fails_with_the_failed_operation` | harness check | no node (harness) | none |  |
| `A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order` | actual pass | in-process nodes | NodeStop |  |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | actual pass | in-process nodes | StorageStall |  |
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
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields` | actual pass | in-process nodes | none |  |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_ack_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_send_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_tail_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_reports_its_peers_and_replication_lag_on_health` | actual pass | in-process nodes | none |  |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | actual pass | separate OS processes | ProcessKill |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order` | actual pass | separate OS processes | ProcessKill |  |
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
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(False)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(True)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | actual pass | in-process nodes | StorageStall |  |
| `Hosts_one_region_until_its_process_is_killed` | skip | separate OS processes | none | a region of RegionProcesses: started by the harness in a process of its own |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (r3c9-1), 2026-10-12T11:29Z on delta

- Commit: `1b68b852b8f5775313d81d6028c347e46bfa02a5`; working tree: clean
- Stacked on: `dea6b364a8b345f652252256b3c2222ee5bc951f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 53 |
| harness check | 16 |
| expected failure | 4 |
| not supported | 0 |
| skip | 3 |
| fail | 0 |
| total | 76 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds` | expected failure | in-process nodes | none | home region and live move (R11); failed as named: HttpRequestException with "/move answered 404" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_failed_write_fails_the_gate_at_once_with_every_operations_outcome` | harness check | no node (harness) | none |  |
| `A_failed_write_is_a_real_failure_never_the_gap` | harness check | no node (harness) | none |  |
| `A_harness_timeout_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_null_reference_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_passing_scenario_has_landed` | harness check | no node (harness) | none |  |
| `Aborting_a_connection_twice_does_not_throw` | harness check | no node (harness) | none |  |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `Resetting_a_link_while_its_connections_close_never_throws` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `The_scenario_failing_its_own_way_is_the_gap` | harness check | no node (harness) | none |  |
| `Waiting_for_every_write_fails_with_the_failed_operation` | harness check | no node (harness) | none |  |
| `A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order` | actual pass | in-process nodes | NodeStop |  |
| `A_hot_database_does_not_hold_back_replication_of_another` | actual pass | in-process nodes | none |  |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | actual pass | in-process nodes | NodeStop, FileTampering |  |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | actual pass | in-process nodes | NodeStop |  |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | actual pass | in-process nodes | StorageStall |  |
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
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields` | actual pass | in-process nodes | none |  |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds` | actual pass | in-process nodes | none |  |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets` | actual pass | in-process nodes | none |  |
| `E9_concurrent_add_and_remove_of_one_element_add_wins` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_ack_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_send_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_publishes_the_replication_lag_series(terrabase_replication_tail_lag)` | actual pass | in-process nodes | none |  |
| `Every_region_reports_its_peers_and_replication_lag_on_health` | actual pass | in-process nodes | none |  |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | actual pass | in-process nodes | none |  |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | actual pass | in-process nodes | SimulatedPartition |  |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | actual pass | in-process nodes | NodeStop |  |
| `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | actual pass | separate OS processes | ProcessKill |  |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | actual pass | in-process nodes | SimulatedPartition |  |
| `Restart_in_the_middle_of_replication_loses_no_write` | actual pass | in-process nodes | NodeStop |  |
| `Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_with_every_later_write_once_in_order` | actual pass | separate OS processes | ProcessKill |  |
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
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(False)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back(True)` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | actual pass | in-process nodes | NodeStop |  |
| `Writes_converge_exactly_over_lossy_peer_links` | actual pass | in-process nodes | NetworkFault |  |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | actual pass | in-process nodes | StorageStall |  |
| `Hosts_one_region_until_its_process_is_killed` | skip | separate OS processes | none | a region of RegionProcesses: started by the harness in a process of its own |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->
