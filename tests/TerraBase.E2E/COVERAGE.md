# Replication scenario coverage: old suite → design C

R2a-1 (#48, merged 2026-10-12) deleted the old replication tests: about 200 Fast and Slow tests and the binary harness
(`CrashableTerraBase`, `PartitionablePeer`, `FakeRegionNetwork`, `RegionBus`). A green Slow run proves none of those
scenarios for design C. This table maps each scenario to the test that replaces it and records whether the replacement
runs against the new replicated path.

**Since R4a-2 (#87, merged 2026-10-12):** peer streams carry change groups between machines, but only the CDC tailer
(R3) produces them from real writes. Until R3 lands nothing replicates, so the replicated rows wait on R3 (`Gaps.Streams`).

**Release rule.** Release correctness and the 1M-document acceptance stay **BLOCKED** until every row's replacement passes
against the design-C replicated path at the exact release commit, both locally and on Fly. A row that is missing or an
expected failure blocks. The obsolete binary harness is not coming back: crash and storage-stall scenarios need new
harness work on the design-C transport (listed under "Missing").

Status words:

- **pass**: the replacement runs on the design-C replicated path and passes.
- **expected failure (slice)**: the replacement exists and is a strict expected failure in `Gaps.cs` until that slice
  lands. It fails as soon as it passes.
- **missing**: no replacement yet.
- **BLOCKED on Fly**: the scenario cannot run with the Fly suite's credential (a TerraBase API key, `documents:read/write`
  on the e2e project only). It is skipped there with that reason, never counted as an expected failure, and blocks like a
  missing row. E12, E13, flow control (row 14), DDL (row 22) and the deleted database (row 23) write databases, indexes or
  schemas, which needs `databases:*`; unblocked by the GitHub-OIDC → ts-auth project token.

## Last runs

Summaries come from `make terrabase-e2e` itself (`tests/TerraBase.E2E/Outcomes.cs`, `scripts/terrabase/e2e-summary.sh`),
never from a pass count: a strict expected failure "passes" in TUnit but proves nothing about the feature. CI's
`terrabase-check` rejects a summary block here or in `RUNS.md` that lacks a category, the commit, the working-tree patch
identity or the stacked-on line, whose counts do not add up, or that reports "successes".

Three repeat runs on delta (local nodes), 2026-10-12, all with identical outcomes; every block is in `RUNS.md`:

| Run | Commit | Working tree | Stacked on | actual pass | expected failure | not supported | skip | fail | total |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 of 3 | `33733d39` | clean | none (origin/main `740ad0d6`; R3's harness not published yet) | 5 | 46 | 2 | 2 | 0 | 55 |
| 2 of 3 | `33733d39` | clean | none (same) | 5 | 46 | 2 | 2 | 0 | 55 |
| 3 of 3 | `33733d39` | clean | none (same) | 5 | 46 | 2 | 2 | 0 | 55 |

Skipped in every run: `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` (the benchmark's cluster
host; runs only under `make benchmark-repl-local`) and `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage`
(Fly outage test; runs only under `make terrabase-e2e-fly-outage`). Not supported in every run: the replicated SIGKILL
scenario (node processes have no region identity off Fly) and the single-node storage stall (since the Turso engine,
#98, the node's writer holds the file's write lock, so the test cannot stall it from outside). Fly: never run against
design C.

The last run in full:

<!-- e2e-summary -->
E2E run (local run 3 of 3), 2026-10-12T05:11Z on delta

- Commit: `33733d39591be8655200da3a5e36abf14a4ea9b8`; working tree: clean
- Stacked on: none (merge base with origin/main `740ad0d6b2d0c409e2606ee187b6c77892803e8e`)

| Category | Tests |
| --- | ---: |
| actual pass | 5 |
| expected failure | 46 |
| not supported | 2 |
| skip | 2 |
| fail | 0 |
| total | 55 |

**Actual passes:**
- `Answer_served_from_another_region_is_refused`
- `Answer_without_a_region_header_is_refused`
- `Pinned_request_carries_the_preferred_region_and_is_accepted_when_that_region_serves_it`
- `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap`
- `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process`

**Expected failures, by the slice they wait on:**
- SQL writes (R9) over peer streams (R4a): 1 (E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail)
- anti-entropy (R6) over peer streams fed by the CDC source (R3): 1 (Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it)
- counters and sets over peer streams (R4a) fed by the CDC source (R3): 4 (Concurrent_inc_in_three_regions_sums, Concurrent_set_adds_in_three_regions_union_and_a_concurrent_add_beats_a_remove, E8_concurrent_inc_in_both_regions_sums_and_set_0_resets_expected_to_fail, E9_concurrent_add_and_remove_of_one_element_add_wins_expected_to_fail)
- garbage collection (R7) over peer streams (R4a): 1 (E14_delete_then_gc_then_recreate_leaves_no_ghost_fields_expected_to_fail)
- home region and live move (R11): 1 (E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds_expected_to_fail)
- peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a): 1 (A_hot_database_does_not_hold_back_replication_of_another)
- refill and anti-entropy (R6) over peer streams fed by the CDC source (R3): 1 (A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes)
- replication metrics with the CDC source (R3): tail lag, and stream lags that move: 1 (Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics)
- the CDC change group source (R3) for the peer streams (R4a): 31 (A_restarted_region_replays_its_backlog_to_a_region_that_returns, A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge, Bulk_1000_documents_split_across_regions_have_matching_counts_and_checksums, Bulk_3000_documents_written_across_three_regions_have_matching_counts_and_digests, Burst_of_overwrites_from_three_regions_converges_to_a_last_write, Causally_later_write_wins_everywhere_despite_clock_skew(fra, -2000), Causally_later_write_wins_everywhere_despite_clock_skew(sjc, 2000), Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice, Concurrent_writes_to_different_fields_in_three_regions_all_survive, Concurrent_writes_to_the_same_field_converge_to_one_identical_value, Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value, Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins, Delete_in_one_region_and_edit_in_the_other_converge_to_the_same_state, Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged, Documents_near_the_size_limit_and_with_many_fields_replicate_intact, Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another, Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals, Region_down_then_back_catches_up_on_every_write_made_while_it_was_down, Replication_backlog_of_3000_writes_drains_after_the_region_returns, Restart_in_the_middle_of_replication_loses_no_write, Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id, Write_in_gru_is_readable_in_sjc_with_the_same_content, Write_in_one_region_is_readable_in_the_other_two(fra, gru), Write_in_one_region_is_readable_in_the_other_two(fra, sjc), Write_in_one_region_is_readable_in_the_other_two(gru, fra), Write_in_one_region_is_readable_in_the_other_two(gru, sjc), Write_in_one_region_is_readable_in_the_other_two(sjc, fra), Write_in_one_region_is_readable_in_the_other_two(sjc, gru), Write_in_sjc_is_readable_in_gru_with_the_same_content, Writes_acknowledged_just_before_their_region_stops_reach_every_region_after_it_returns, Writes_converge_exactly_over_lossy_peer_links)
- the catalog as a lattice database (R10a): 1 (E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives_expected_to_fail)
- the catalog as a lattice database (R10a) and garbage collection (R7): 1 (A_database_deleted_while_a_region_is_down_does_not_come_back)
- the changes feed (R8) over peer streams (R4a): 2 (E10_feed_cursor_from_gru_resumes_in_sjc_with_no_missing_event_in_per_origin_order_expected_to_fail, E15_refresh_token_used_in_gru_and_sjc_shows_both_uses_on_both_feeds_expected_to_fail)

**Not supported (an unwired test hook):**
- `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` — the CDC change group source (R3) for the peer streams (R4a); node processes cannot form a three-region cluster before peer streams (R4a); /tmp/terrabase-process-fnvcOm serves alone
- `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` — a storage stall hook for the Turso engine (#98 holds the file's write lock); no storage stall hook: the engine holds the database file's write lock, so the test cannot stall it from outside

**Skips:**
- `Hosts_three_local_regions_until_the_benchmark_deletes_its_targets_file` — the replication benchmark's local cluster host: run it with make benchmark-repl-local
- `Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage` — Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage

**Failures:**
<!-- /e2e-summary -->

## Scenarios

| # | Scenario | Old test (deleted in #48) | Replacement (tests/TerraBase.E2E) | On the design-C replicated path? | Last run | Notes |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | Write in one region readable in the others | Fast/CrossRegion/CrossRegionTests: `A_write_in_either_region_is_readable_in_the_other_with_the_same_version` | ReplicationE2ETests: `Write_in_gru_is_readable_in_sjc…` (E1), `Write_in_sjc_is_readable_in_gru…` (E2); ThreeRegionE2ETests: `Write_in_one_region_is_readable_in_the_other_two` ×6 pairs | expected failure (R3) | 33733d39: expected failure | All 3 pairs, both ways |
| 2 | Concurrent same-field writes, 2-way | Fast/Replication/ReplicationTests: `Two_nodes_that_write_at_the_same_time_converge`; CrossRegionTests: `Concurrent_writes_in_both_regions_converge_on_the_same_documents` | ReplicationE2ETests: `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` (E3) | expected failure (R3) | 33733d39: expected failure | |
| 3 | Concurrent same-field writes, 3-way | (none; the old suite had 2 nodes or 2 regions) | ThreeRegionE2ETests: `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value`; `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | expected failure (R3) | 33733d39: expected failure | The benchmark adds 200 docs × 5 rounds per region (`benchmark/bench`) |
| 4 | Concurrent different-field writes, 2-way | (none; the old engine kept whole-document LWW) | ReplicationE2ETests: `Concurrent_writes_to_different_fields_both_survive…` (E4) | expected failure (R3) | 33733d39: expected failure | Per-field lattice R1/R2a, replicated by R4a |
| 5 | Concurrent different-field writes, 3-way | (none) | ThreeRegionE2ETests: `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | expected failure (R3) | 33733d39: expected failure | |
| 6 | Delete vs concurrent edit | ReplicationTests: `A_version_read_on_one_member_deletes_the_document_on_the_other`; Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: `Delete_in_one_region_and_edit_in_the_other…` (E5); ThreeRegionE2ETests: `Delete_in_one_region_and_edits_in_the_other_two…` | expected failure (R3) | 33733d39: expected failure | Many concurrent deletes: the benchmark's 1 % deletes |
| 7 | Partition, then heal | CrossRegionTests: `Partitioned_regions_keep_serving_their_writes_and_converge_after_heal`; Slow/CrossRegion/CrossRegionPartitionTests: `Regions_converge_without_losing_or_corrupting_writes_through_a_lossy_partitioned_link_and_restarts` | ThreeRegionFaultE2ETests: `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | expected failure (R3) | 33733d39: expected failure | `Cluster.Partition` throws until R4a wires its streams in. Local only; Fly has no partition switch (skipped there, with the reason) |
| 8 | Region down, then rejoin | Fast/CrossRegion/CrossRegionDeletionTests: `A_restarted_region_removes_…`; (Fly) FlyOutageE2ETests | ThreeRegionFaultE2ETests: `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down`; FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` (E7) | expected failure (R3); E7 also needs R6 | 33733d39: expected failure; Fly: not run | Fly runs only through `make terrabase-e2e-fly-outage` |
| 9 | Node restart mid-replication (receiver); restore to an older state | ReplicationTests: `A_restarted_node_resumes_after_its_peers_applied_prefix`; Slow/ReceiverRestoreTests: `A_caught_up_sender_repairs_a_restarted_receiver_restored_to_an_older_frontier` | ThreeRegionFaultE2ETests: `Restart_in_the_middle_of_replication_loses_no_write`; RepairAndContractE2ETests: `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | expected failure (R3; restore: R6) | 33733d39: expected failure | Restore copies sjc's data directory back to a snapshot taken 100 writes earlier |
| 10 | Sender restarts with an unsent backlog | Fast/Replication/HintedHandoffTests: `A_restarted_sender_replays_durable_backlog_when_its_peer_returns_without_new_writes` | ThreeRegionFaultE2ETests: `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | expected failure (R3) | 33733d39: expected failure | New in this change |
| 11 | Clock skew | Fast/Replication/ReplicatedStampTests: `A_stamp_far_ahead_is_applied_and_counted_but_moves_the_clock_only_the_skew_ahead` | ThreeRegionFaultE2ETests: `Causally_later_write_wins_everywhere_despite_clock_skew` (±2 s), `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | expected failure (R3) | 33733d39: expected failure | Missing: the `terrabase.clock.skew_capped` counter assertion (R4a metrics). Local only (skipped on Fly) |
| 12 | Acknowledged-write durability: ack, then crash | Slow/ReplicationChaosTests: `Three_nodes_preserve_acknowledged_writes_after_partition_skew_storage_stall_and_crash`, `The_history_checker_rejects_a_lost_or_changed_acknowledged_write` | CrashAndStallE2ETests: `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` (single node, real SIGKILL of a `terrabase` process, NodeProcess harness); `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` (replicated); ThreeRegionFaultE2ETests: `Writes_acknowledged_just_before_their_region_stops…` (clean stop) | single node: **pass**; replicated: expected failure (R3; node processes need a region identity and peer addresses off Fly) | 33733d39: single node pass (555 acknowledged, all survived); replicated expected failure | Process kill, not power loss: an OS crash can lose the last commits with `synchronous = NORMAL` (design §18), which no local test simulates |
| 13 | Storage stall | Slow/ReplicationChaosTests (storage stall phase); Fast/Replication/CaptureAdmissionTests | CrashAndStallE2ETests: `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` (single node); `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` (sjc stalls 10 s) | single node: **not supported** (since #98 the engine holds the file's write lock, so the outside lock cannot stall it: needs an IO stall hook); replicated: expected failure (R3) | 33733d39: single node not supported; replicated expected failure | The stall is the database file held by another writer (`BEGIN` plus a write on a separate connection), so commits wait. A slow fsync would need an IO-delay shim, which is not built |
| 14 | CDC / replication backlog drain; flow control | Slow/ReplicationSlowTests: `Advancing_ACKs_keep_a_bandwidth_limited_window_alive…`, `A_database_written_without_pause_does_not_hold_back_the_others`; ReplicationTests: `Exhausted_byte_credit_blocks…`, `The_sender_stays_within_the_byte_credit…` | ThreeRegionFaultE2ETests: `Replication_backlog_of_3000_writes_drains_after_the_region_returns`; RepairAndContractE2ETests: `A_hot_database_does_not_hold_back_replication_of_another` | expected failure (R3; flow control also R10a); flow control **BLOCKED on Fly** | 33733d39: expected failure | Flow control: Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`). The 24 h / 1 GiB retention cut and `refill_required` cannot run in real time end to end. R6 must ship them as a Fast test with a fake clock; that requirement is tracked here |
| 15 | Large documents | CrossRegionTests: `A_group_larger_than_one_reply_crosses_in_parts` | ThreeRegionE2ETests: `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` (900 KiB, 2000 fields) | expected failure (R3) | 33733d39: expected failure | |
| 16 | Counters (PN) | (none) | LatticeE2ETests: E8; ThreeRegionE2ETests: `Concurrent_inc_in_three_regions_sums` | expected failure (R5 + R3) | 33733d39: expected failure | R5 is #75 |
| 17 | Sets (add-wins) | (none) | LatticeE2ETests: E9; ThreeRegionE2ETests: `Concurrent_set_adds_in_three_regions_union…` | expected failure (R5 + R3) | 33733d39: expected failure | |
| 18 | Bulk convergence with counts and digests | Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: E6 (1000); ThreeRegionE2ETests: `Bulk_3000_documents…`; benchmark `repl-bench` (1M) | expected failure (R3) | 33733d39: expected failure; 1M only on the old engine | The 1M run on design C waits for R4a |
| 19 | Lossy link (drop, delay, reset) | Slow/CrossRegionPartitionTests (lossy link); Fast/Replication/SyncProtocolTests, ResponseLimitTests, PeerTransportTests | CrashAndStallE2ETests: `Writes_converge_exactly_over_lossy_peer_links` (LossyLink TCP proxy: 200 ms delay, 256 KiB/s, 5 % resets per second); `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` (the proxy itself) | expected failure (R3; `Cluster.RouteThrough` lands with it); proxy self-check: **pass** | 33733d39: expected failure; proxy pass | Byte corruption and frame duplication need fault injection inside the peer protocol (R4a), not a TCP proxy: design §9 refuses a corrupt group at the trust boundary |
| 20 | Anti-entropy repair of rows the streams missed | Fast/Replication/AntiEntropyTests: `Caught_up_members_repair_missing_and_stale_rows_without_new_writes` and 11 others | RepairAndContractE2ETests: `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` (deletes the document's rows from sjc's file while it is down; only a repair can bring it back) | expected failure (R6 + R3) | 33733d39: expected failure | 200 s window: rounds every 60 s, repair after two differing rounds |
| 21 | Unique constraints under partition | Fast/Replication/UniqueReplicationTests: `Partitioned_unique_writes_converge_and_a_deleted_winner_releases_its_key`; Slow/UniqueConstraintReplayTests | RepairAndContractE2ETests: `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | expected failure (R3) | 33733d39: expected failure | Missing: a deleted winner releasing its key |
| 22 | Collection, index and schema DDL replication | ReplicationTests: `Collections_and_indexes_created_and_dropped_on_one_member_follow_on_the_other`; CollectionSchemaReplicationTests (2) | RepairAndContractE2ETests: `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another`; every cluster start (collection create) | expected failure (R3); **BLOCKED on Fly** | 33733d39: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 23 | Database deleted at home not brought back | CrossRegionTests: `A_database_deleted_in_its_home_is_not_brought_back…`; CrossRegionDeletionTests | RepairAndContractE2ETests: `A_database_deleted_while_a_region_is_down_does_not_come_back` (category fly-outage) | expected failure (R10a + R7); **BLOCKED on Fly** | 33733d39: expected failure | Missing: checking fra's file is removed from disk. Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 24 | Catalog: database created in one region usable in another; same-name race | CrossRegionTests: `A_commit_registers_a_database_under_a_chosen_id_once` | CatalogE2ETests: E12 | expected failure (R10a); **BLOCKED on Fly** | 33733d39: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 25 | Placement and move | CrossRegionTests: placement tests (5) | CatalogE2ETests: E13 | expected failure (R11); **BLOCKED on Fly** | 33733d39: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`). `fly-replay` itself is covered by #50's Fast tests, not here |
| 26 | Changes feed across regions | (none: the per-collection feed and shapes were deleted in R2c) | LatticeE2ETests: E10 | expected failure (R8) | 33733d39: expected failure | |
| 27 | SQL writes merge across regions | (none) | LatticeE2ETests: E11 | expected failure (R9) | 33733d39: expected failure | |
| 28 | ts-auth refresh reuse across regions | (none) | LatticeE2ETests: E15 (storage half) | expected failure (R5 + R8); the ts-auth half is R12 | 33733d39: expected failure | |
| 29 | Replication observability (lag, health, metrics) | Slow/ReplicationObservabilityTests (2); CrossRegionTests: `Both_regions_meter_encoded_replication_bytes_by_direction…`; ReplicationStatusTests (3) | RepairAndContractE2ETests: `Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics` (`/health/replication` status ok with both peers and lag ≤ 5 s; `terrabase_replication_tail_lag` on `/metrics`) | expected failure (R3) | 33733d39: expected failure | Metering per direction is not covered (R4a metric names are not published yet) |

## Counts (2026-10-12, at 33733d39)

| Status | Rows |
| --- | ---: |
| pass on design C (replicated) | 0 |
| expected failure (a slice) | 29 |
| missing (no replacement) | 0 |

The 29 rows have no "no replacement yet" row left. Row 12 also has a single-node half that passes today on the current
write path (SIGKILL durability). Row 13's single-node half passed on SQLite and is not supported since the Turso engine
(#98); the replicated halves are expected failures. Sub-scenarios still
without a test, named in their notes: power loss (12), a slow fsync (13), the 24 h / 1 GiB retention cut (14, an R6 Fast
requirement), corruption and duplication inside the peer protocol (19), a deleted unique winner releasing its key (21),
file removal of a deleted database (23) and metering per direction (29).

## Test hooks the slices must provide

These `Cluster`/`NodeProcess` calls throw `NotSupportedException` today. The R3 owner is their sole writer (with the
shared harness: per-node membership instead of the shared `FakeMemberStore`, LossyLink routing, node processes as regions
and the CDC source wiring). This file, `Gaps.cs` and the scenario assertions stack on R3's published harness commits, and
every run records that commit on its "Stacked on" line:

- `Cluster.Partition(a, b)` and `Cluster.Heal()`: cut and restore the local peer streams between two regions (plan: hide
  each side from the other's view of the shared machine directory, which R4a-2's sender follows).
- `Cluster.RouteThrough(a, b, peer => new LossyLink(peer, …))`: send a's peer traffic for b through a TCP proxy (needs b's
  peer endpoint).
- Node processes in a region (`Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back`): a
  `terrabase` process needs a region identity and its peers' addresses off Fly.

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
