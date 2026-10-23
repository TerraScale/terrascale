# RT2 replication feature → test → result matrix (RT2c re-run on origin/main)

Every replication feature of TerraBase mapped to the integration tests that exercise it, with the result of one run on
origin/main plus RT2c's test branches, on dev host mariogk-dev (46 CPU, shared with two benchmark jobs, load 13 to 67),
2026-10-23 10:26-11:10Z. RT2's first run (delta, `rt2/tests-2`) found F1 to F4; this re-run records where each stands.
Suites: **Fast** (`tests/TerraBase.Fast`, required in CI), **Slow** (`tests/TerraBase.Slow`, non-blocking), **E2E**
(`tests/TerraBase.E2E`: in-process nodes, separate OS processes with SIGKILL, TCP-proxy lossy links).

## Runs

| Run | Branch | Commit | Command | Result |
| --- | --- | --- | --- | --- |
| M | `rt2c/tests-matrix` = origin/main `d9d804812` + `rt2c/tests-1` + `rt2c/tests-sql` | `c4b34be18` | `make terrabase-test-fast` | **2423/2423 passed**, 0 skipped, 7m54s |
| M | same | same | `make terrabase-test-slow TEST_ARGS="-- --treenode-filter /*/TerraBase.Slow.Replication/*/*"` | 9 pass, **2 fail**, 1 skip (child-process entry point), 6m02s, load 37 |
| M | same | same | `make terrabase-e2e-local` | 66/67, **1 fail** (harness self-test, load 67) |
| M | same | same | `make terrabase-replication-matrix` | **16/16 passed** |
| M | same | same | `make terrabase-e2e` | 89 pass, 79 harness, **18 fail**, 2 expected failure, 2 not supported, 13 skip (RT2's E1: 35 fail) |
| K | same | same | each SIGKILL/power-loss E2E test alone (`--treenode-filter /*/*/*/<name>*`) | see F1 |
| K1 | `rt2c/tests-matrix-f1` = M + `origin/rt2/crash-restart` (`4eff45db9`, `2eb9a380c`, `7da2ee1ae`) | `366a74b90` | the same SIGKILL set alone | see F1 |
| L | each RT2c branch alone on delta | below | targeted tests, 5 repeats | below |

Targeted repeats (flaky-proof, run L):

| Branch | Head | Test | Result |
| --- | --- | --- | --- |
| `rt2c/tests-1` | `54ed7b0e7` | Fast `CatalogGateRestartTests` (2); E2E `A_restarted_region_replays_its_backlog_to_a_region_that_returns`, `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` | 5/5 each |
| `rt2c/tests-sql` | `6b32f7369` | Fast `SqlWriteReplicationTests` (2) | 5/5 |
| `rt2c/tests-rebuild` | `a9ebe833a` | Fast `RebuildCertificationTests` (52), `ReplicaRebuildTests` (13) | 52/52 in 5/5 at load 19-33 (2 of 8 earlier runs at load 55-93 hit the 60 s test timeout, as origin/main does at that load); 13/13 in 5/5 |
| `rt2c/tests-rebuild-on-398` | `f14d41fdb` | same, on #398 head `f2f6723f4` | 52/52 in 5/5, 13/13 once |
| `rt2c/tests-resurrection-on-398` | `90b72e805` | Slow `CollectedRepairE2ETests` | 5/5 on #398 |
| `rt2c/tests-resurrection` | `66924213b` | same, on origin/main | fails by design until #398 merges (`no such table: _tb_gc_floor`) |

## Matrix

| # | Feature | Test(s) | Suite | Result |
| --- | --- | --- | --- | --- |
| 1 | Peer streams send/receive, reconnect | `PeerStreamTests` (incl. `Three_regions_converge_from_local_writes_through_the_cdc_tailer`, `A_restarted_sender_resumes_each_peer_from_its_persisted_ack`), `PeerStreamDeadlineTests`; E2E `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down`, `Replication_backlog_of_3000_writes_drains_after_the_region_returns`, replication matrix `Write_in_one_region_is_readable_in_the_other_two` (6 directions) | Fast, E2E | pass |
| 2 | Change capture: Turso CDC tap, change groups, shape frontier | `PeerStreamTests.The_cdc_tailer_ships_one_group_per_transaction_never_echoes_and_drops_what_every_peer_acked`, `CaptureBatchBoundsTests`, `CaptureFrontierMemoryTests`, `CdcWriterLeaseTests`, `CorruptChangeLogTests`, `ShapeResumeTests`; Slow `Local_capture_shape_damage_rebuilds_and_accepts_new_cross_region_writes` | Fast, Slow | pass |
| 3 | Remote-durable acks, shipper leases | `RemoteAckTests`, `ContinuousBackupTests`; E2E `A_201_is_remote_durable_and_another_region_serves_it_at_once` (3), `A_201_survives_the_origins_immediate_SIGKILL_and_the_remote_regions_restart` (2), `The_remote_durable_frontier_survives_the_origins_SIGKILL`; Slow `Remote_ack_with_two_healthy_peers_survives_restart`, `Acknowledgement_survives_immediate_SIGKILL_and_loss_of_unsynced_WAL` (2) | Fast, E2E, Slow | pass |
| 4 | Catalog gate, freshness, anti-entropy, Merkle digests | `CatalogGateTests`, **new** `CatalogGateRestartTests.A_restarted_region_opens_its_catalog_gate_within_seconds` (others up / another down), `CatalogFreshnessTests`, `CatalogLatticeTests`, `MerkleDigestTests`, `KeyAwareDigestTests`, `AntiEntropy*Tests`; E2E `A_restarted_region_replays_its_backlog_to_a_region_that_returns`, `Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart` (both failed in RT2's run, pass now) | Fast, E2E | pass |
| 4b | Anti-entropy of a row lost outside the engine | E2E `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it`, `Shape_clients_converge_after_anti_entropy_repairs_a_lost_document` | E2E | **fail** (F2, unchanged) |
| 5 | Replica rebuild, rejoin, certification, restart | `ReplicaRebuildTests`, `RebuildCertificationTests` (+ liveness on `rt2c/tests-rebuild`), `RebuildLifecycleTests`, `RebuildLedgerRecoveryTests`, `RebuildFaultSafetyTests`, `A6*Tests`; E2E `Each_region_rejoins_with_writes_and_tombstones_from_both_survivors` (3), `Canceling_a_rejoin_while_the_region_is_down_restarts_it_before_returning`, `All_three_nodes_restart_together_and_accept_a_write`, `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | Fast, E2E | pass |
| 5b | Restart after SIGKILL / power loss during writes | E2E `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process`, `A_random_cut_keeps_every_acknowledged_tenant_document_and_discards_the_torn_transaction` (3), `A_power_loss_never_leaves_a_sealed_chunk_naming_a_lost_write` (6), `A_power_loss_keeps_every_change_group_the_receiver_acknowledged` | E2E | **fail** on main (F1); K1 (with lane P's `rt2/crash-restart`): **16/16 pass** |
| 6 | GC, repair, join (resurrection, forget, repair incarnation) | `PeerStreamTests.A_tombstone_collected_while_a_peer_was_away_is_not_brought_back_by_its_refill_or_by_repairs`, `CollectionDropConvergenceTests`, `IncarnationContractTests`, `ChangeLogAgeTests`; E2E `E14_delete_then_gc_then_recreate_leaves_no_ghost_fields`, `A_database_deleted_while_a_region_process_was_killed_stays_deleted_and_its_name_serves_a_new_one`; Slow `CollectedRepairE2ETests` (two nodes, real streams and anti-entropy, `_tb_gc_floor`) | Fast, E2E, Slow | pass; Slow resurrection proof passes on #398 only (needs its `_tb_gc_floor`) |
| 7 | Change feed for readers (DatabaseChanges, cursors, N readers) | `DatabaseChangesTests`, `RegionConvergenceTests.Every_change_feed_reader_in_two_regions_receives_another_regions_writes_in_its_commit_order` (16 readers); E2E `E10_*` (2), `A_cursor_from_sjc_resumes_on_fra_after_sjc_stops_with_every_later_write_once_in_order`, `Separate_processes_a_cursor_from_sjc_resumes_on_fra_after_sjc_is_killed_...`; Slow `Feed_resumes_after_its_region_restarts_with_two_healthy_peers`, `Empty_change_feed_opens_on_a_clock_skewed_three_node_cluster` | Fast, E2E, Slow | pass |
| 8 | Conflict resolution: per-document LWW by HLC, node tiebreak | `RegionConvergenceTests` (3 LWW tests), `PeerStreamTests.Three_regions_converge_through_concurrent_reordered_and_duplicated_groups`; **new** `SqlWriteReplicationTests.Concurrent_sql_updates_of_one_row_in_gru_and_sjc_converge_to_one_of_them_everywhere`; E2E `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value`, `Burst_of_overwrites_from_three_regions_converges_to_a_last_write`, `Delete_in_one_region_and_edits_in_the_other_two_converge_and_stay_converged`, E8, E9 | Fast, E2E | pass |
| 9 | All-regions placement (#405) | `AllRegionsPlacementTests` (2) | Fast | pass |
| 10a | Faults: partition | E2E `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id`, `Shape_clients_in_every_region_converge_after_a_partition_heals`, `Regions_with_another_placed_peer_accept_writes_and_converge_after_a_direct_partition_heals`; Slow R8 `Acknowledged_writes_converge_through_seeded_faults(0)` | E2E, Slow | pass, except `Regions_with_another_placed_peer_...` and R8 seed 0: **fail under load** (F3) |
| 10b | Faults: node kill | E2E `A_killed_origins_durable_change_log_ships_to_a_region_that_missed_its_confirmed_writes`, `Restart_in_the_middle_of_replication_loses_no_write`, `A_201_survives_the_origin_process_SIGKILL_right_after_it`; Slow `Seed_67_qualifies_serving_after_every_replica_restarts`, `Local_ack_survives_restart` | E2E, Slow | pass; single-node copy after SIGKILL: row 5b |
| 10c | Faults: slow / lossy link | `PeerStreamDeadlineTests`; E2E `Writes_converge_exactly_over_lossy_peer_links` | Fast, E2E | pass (lossy link passed in this run; RT2 saw it fail under load) |
| 10d | Faults: clock skew | `OriginStampOrderTests`; E2E `Causally_later_write_wins_everywhere_despite_clock_skew` (2), `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins`, `Shape_clients_converge_under_clock_skew` (2) | Fast, E2E | pass |
| 11 | SQL writes replicate (SQL API, Hrana) | **new** `SqlWriteReplicationTests.Sql_inserts_updates_and_deletes_in_gru_and_a_hrana_insert_in_sjc_are_read_with_sql_in_every_region` (gru, sjc, fra over the public `…/sql/execute` and `…/libsql/v2/pipeline`); `Sql.SqlReplicationTests.Sql_tables_and_rows_replicate_and_concurrent_increments_add` (2 regions, in-process session) | Fast | pass (SQ1 #420 added the write path; F4 closed for SQL/Hrana) |
| 11b | pgwire writes replicate | none on main: PostgreSQL wire writes arrive with open PR #445 (`ISqlSession` over the wire); cross-region pgwire reads pass (`RegionConvergenceTests.Documents_written_in_one_region_are_read_with_sql_over_the_postgres_wire_in_the_others`) | Fast | writes: **not on main** (#445) |
| 11c | E11: SQL update and document patch on one document both survive | E2E `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add_expected_to_fail` | E2E | expected failure: posts to the old `…/sql` route on a collection with no SQL table definition; needs a rewrite onto `…/sql/execute` |

## Findings

- **F1 (SIGKILL → `copy_faulted`).** Unchanged on main: run K reproduces it alone, 11 of 16 SIGKILL/power-loss cases fail in
  10 to 80 s (`Writes_acknowledged_before_a_SIGKILL_...` 1/1, `A_random_cut_...` 3/3, `A_power_loss_never_leaves_a_sealed_chunk_...`
  6/6 in this alone-run, `A_power_loss_keeps_every_change_group_...` 1/1). With lane P's crash-restart fix (run K1, `366a74b90`): **all 16 cases pass** alone (14 to 25 s
  each, load 19 to 36), so `rt2/crash-restart` closes F1 for every E2E SIGKILL/power-loss scenario; F1 stays open on main
  until lane P lands it.
- **F2 (anti-entropy of a row lost outside the engine).** Unchanged: both E2E tests fail after 200 s. Still a design decision
  (recompute or verify digests from rows, or declare physical loss out of scope).
- **F3 (load sensitivity).** At load 25 to 67 on 46 CPUs: `Bulk_1000_documents_...`, `Regions_with_another_placed_peer_..._partition_heals`,
  Slow R8 `Acknowledged_writes_converge_through_seeded_faults(0)` and `A_drop_of_more_documents_than_one_owner_may_lease_...`
  answered 503 `write_outcome_unknown` at the fixed 10 s remote-ack bound; the harness self-test
  `WritesTests.A_failed_write_fails_the_gate_at_once_with_every_operations_outcome` (`tests/TerraBase.E2E/WritesTests.cs:28`) assumes
  writes 0-4 finish before write 5 fails, which concurrent writes do not guarantee (doc002 was still in flight). Fewer F3
  failures than RT2's delta run (2 E2E vs 14) on a bigger, less loaded host. Proposed isolation fix, not implemented (over
  the 2 h / 400 line bound): every in-process cluster's three nodes (Kestrel, engine, CDC tailer, peer streams) are built in
  the runner process by `Cluster.StartLocalCoreAsync` (`tests/TerraBase.E2E/Cluster.cs:275`) through `Cluster.Local`
  (`Cluster.cs:548`), so all concurrent scenarios share one thread pool and one server GC; a starved pool delays acks past
  10 s. Fix: host each local cluster in its own child process, as `RegionProcesses` (`tests/TerraBase.E2E/RegionProcesses.cs:29`)
  already does per node via `RegionProcessHost`, moving `Local`'s in-memory hooks (partition and lossy `PeerLink`s,
  `FakeS3`, clock skew, `Via` edge, `Sql`) behind the child's internal listener. Interim and cheaper: shard
  `make terrabase-e2e` (`Makefile:198-201`) into one runner process per test class, keeping each runner's default parallelism.
  Neither lowers parallelism nor lengthens bounds.
- **F4 (SQL writes).** Closed for the SQL API and Hrana by SQ1 (#420): new Fast proof in row 11. pgwire writes: #445.
  E11 is stale (row 11c).
- **Stale E2E expectations (unchanged from RT2):** `A_power_loss_keeps_every_client_write_the_node_answered` and
  `A_power_loss_keeps_the_catalog_and_the_api_keys_...` stop at setup with 503 `remote_region_unavailable` (single-node cell);
  `Writes_through_a_storage_stall_...` fails outside its gap signature; `A_database_deleted_while_a_region_is_down_does_not_come_back`
  gets 409 `database_replicated` (#405 rule).
- **Rebuild liveness (`rt2c/tests-rebuild`).** Still needed after #398: #398's final check still compares every bucket with the
  newest source digest, so a busy source keeps a rebuild from finishing. Only conflict with #398: the round loop in
  `src/TerraBase/Features/Replication/RebuildCertification.cs`; resolution on `rt2c/tests-rebuild-on-398`.
