# E2E runs

Full summaries of the repeat runs `COVERAGE.md` lists, as `make terrabase-e2e` printed them. Newest last. CI's
`terrabase-check` validates every block (`scripts/terrabase/e2e-summary.sh check`).

<!-- e2e-summary -->
E2E run (main (no R3), run 1 of 3), 2026-10-12T06:10Z on delta

- Commit: `2e93756802a06635d428f8d7c4c1099bcca60005`; working tree: clean
- Stacked on: none (merge base with origin/main `740ad0d6b2d0c409e2606ee187b6c77892803e8e`)

| Category | Tests |
| --- | ---: |
| actual pass | 1 |
| harness check | 4 |
| expected failure | 46 |
| not supported | 2 |
| skip | 2 |
| fail | 0 |
| total | 55 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7) |
| `A_hot_database_does_not_hold_back_replication_of_another` | expected failure | in-process nodes | none | peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a) |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | expected failure | in-process nodes | NodeStop, FileTampering | refill and anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | expected failure | in-process nodes | StorageStall | the CDC change group source (R3) for the peer streams (R4a) |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | expected failure | in-process nodes | NodeStop, FileTampering | anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | expected failure | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | expected failure | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_inc_in_three_regions_sums` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | expected failure | in-process nodes | SimulatedPartition, ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a) |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a) |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | expected failure | in-process nodes | none | home region and live move (R11) |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | expected failure | in-process nodes | none | garbage collection (R7) over peer streams (R4a) |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | expected failure | in-process nodes | none | replication metrics with the CDC source (R3): tail lag, and stream lags that move |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Restart_in_the_middle_of_replication_loses_no_write` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Writes_converge_exactly_over_lossy_peer_links` | expected failure | in-process nodes | NetworkFault | the CDC change group source (R3) for the peer streams (R4a) |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | the CDC change group source (R3) for the peer streams (R4a); node processes cannot form a three-region cluster before peer streams (R4a); /tmp/terrabase-process-WXIuXL serves alone |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | not supported | in-process nodes | StorageStall | a storage stall hook for the Turso engine (#98 holds the file's write lock); no storage stall hook: the engine holds the database file's write lock, so the test cannot stall it from outside |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (main (no R3), run 2 of 3), 2026-10-12T06:12Z on delta

- Commit: `2e93756802a06635d428f8d7c4c1099bcca60005`; working tree: clean
- Stacked on: none (merge base with origin/main `740ad0d6b2d0c409e2606ee187b6c77892803e8e`)

| Category | Tests |
| --- | ---: |
| actual pass | 1 |
| harness check | 4 |
| expected failure | 46 |
| not supported | 2 |
| skip | 2 |
| fail | 0 |
| total | 55 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7) |
| `A_hot_database_does_not_hold_back_replication_of_another` | expected failure | in-process nodes | none | peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a) |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | expected failure | in-process nodes | NodeStop, FileTampering | refill and anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | expected failure | in-process nodes | StorageStall | the CDC change group source (R3) for the peer streams (R4a) |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | expected failure | in-process nodes | NodeStop, FileTampering | anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | expected failure | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | expected failure | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_inc_in_three_regions_sums` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | expected failure | in-process nodes | SimulatedPartition, ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a) |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a) |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | expected failure | in-process nodes | none | home region and live move (R11) |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | expected failure | in-process nodes | none | garbage collection (R7) over peer streams (R4a) |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | expected failure | in-process nodes | none | replication metrics with the CDC source (R3): tail lag, and stream lags that move |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Restart_in_the_middle_of_replication_loses_no_write` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Writes_converge_exactly_over_lossy_peer_links` | expected failure | in-process nodes | NetworkFault | the CDC change group source (R3) for the peer streams (R4a) |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | the CDC change group source (R3) for the peer streams (R4a); node processes cannot form a three-region cluster before peer streams (R4a); /tmp/terrabase-process-Ougzaj serves alone |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | not supported | in-process nodes | StorageStall | a storage stall hook for the Turso engine (#98 holds the file's write lock); no storage stall hook: the engine holds the database file's write lock, so the test cannot stall it from outside |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->

<!-- e2e-summary -->
E2E run (main (no R3), run 3 of 3), 2026-10-12T06:13Z on delta

- Commit: `2e93756802a06635d428f8d7c4c1099bcca60005`; working tree: clean
- Stacked on: none (merge base with origin/main `740ad0d6b2d0c409e2606ee187b6c77892803e8e`)

| Category | Tests |
| --- | ---: |
| actual pass | 1 |
| harness check | 4 |
| expected failure | 46 |
| not supported | 2 |
| skip | 2 |
| fail | 0 |
| total | 55 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7) |
| `A_hot_database_does_not_hold_back_replication_of_another` | expected failure | in-process nodes | none | peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a) |
| `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | expected failure | in-process nodes | NodeStop, FileTampering | refill and anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` | expected failure | in-process nodes | StorageStall | the CDC change group source (R3) for the peer streams (R4a) |
| `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` | expected failure | in-process nodes | NodeStop, FileTampering | anti-entropy (R6) over peer streams fed by the CDC source (R3) |
| `Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000)` | expected failure | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000)` | expected failure | in-process nodes | ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_inc_in_three_regions_sums` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | expected failure | in-process nodes | SimulatedPartition, ClockSkew | the CDC change group source (R3) for the peer streams (R4a) |
| `Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a) |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a) |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail` | expected failure | in-process nodes | none | home region and live move (R11) |
| `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail` | expected failure | in-process nodes | none | garbage collection (R7) over peer streams (R4a) |
| `E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail` | expected failure | in-process nodes | none | the changes feed (R8) over peer streams (R4a) |
| `E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail` | expected failure | in-process nodes | none | counters and sets over peer streams (R4a) fed by the CDC source (R3) |
| `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` | expected failure | in-process nodes | none | replication metrics with the CDC source (R3): tail lag, and stream lags that move |
| `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Replication_backlog_of_3000_writes_drains_after_the_region_returns` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Restart_in_the_middle_of_replication_loses_no_write` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | expected failure | in-process nodes | SimulatedPartition | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_gru_is_readable_in_sjc_with_the_same_content` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(fra, gru)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(fra, sjc)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(gru, fra)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(gru, sjc)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, fra)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_one_region_is_readable_in_the_other_two(sjc, gru)` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Write_in_sjc_is_readable_in_gru_with_the_same_content` | expected failure | in-process nodes | none | the CDC change group source (R3) for the peer streams (R4a) |
| `Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns` | expected failure | in-process nodes | NodeStop | the CDC change group source (R3) for the peer streams (R4a) |
| `Writes_converge_exactly_over_lossy_peer_links` | expected failure | in-process nodes | NetworkFault | the CDC change group source (R3) for the peer streams (R4a) |
| `Answer_served_from_another_region_is_refused` | harness check | no node (harness) | none |  |
| `Answer_without_a_region_header_is_refused` | harness check | no node (harness) | none |  |
| `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it` | harness check | no node (harness) | none |  |
| `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` | harness check | no node (harness) | NetworkFault |  |
| `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` | not supported | separate OS processes | ProcessKill | the CDC change group source (R3) for the peer streams (R4a); node processes cannot form a three-region cluster before peer streams (R4a); /tmp/terrabase-process-B3fhbf serves alone |
| `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` | not supported | in-process nodes | StorageStall | a storage stall hook for the Turso engine (#98 holds the file's write lock); no storage stall hook: the engine holds the database file's write lock, so the test cannot stall it from outside |
| `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` | actual pass | separate OS processes | ProcessKill |  |
| `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` | skip | in-process nodes | none | the replication benchmark's local cluster host: run it with make benchmark-repl-local |
| `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` | skip | in-process nodes | NodeStop | Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage |
<!-- /e2e-summary -->
