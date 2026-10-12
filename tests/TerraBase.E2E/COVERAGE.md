# Replication scenario coverage: old suite → design C

R2a-1 (#48, merged 2026-10-12) deleted the old replication tests: about 200 Fast and Slow tests and the binary harness
(`CrashableTerraBase`, `PartitionablePeer`, `FakeRegionNetwork`, `RegionBus`). A green Slow run proves none of those
scenarios for design C. This table maps each scenario to the test that replaces it and records whether the replacement
runs against the new replicated path.

**Recorded candidate: candidate-8 `70b55472`** (frozen local branch `test/candidate-8`), published on #108 by the
strict-test owner, the single publisher of acceptance candidates. Its base `dea6b364` merges two heads:
- the R3 owner's `80c67345`: R7 stale writes `e8b458e5`, failing refill answers refused `7d83cfbf`, the PeerLink abort
  fix; on #120 R7 → #117 → #114 R6 → #113 → #112 → #111 RegionProcesses → #106 R3;
- R8's #118 `90c1b833` (changes feed).

It adds the assertion side, including the **strict expected-failure recorder**. The R3 owner acknowledged that exact SHA,
and both owners ran it 3 times on a clean tree under the heavy-run lock, identical test by test including every detail.
None of it is pushed or merged (COS preview push freeze and global merge hold), so main reproduces none of these passes.

**What "pass" means here.** Most replicated passes run on **in-process nodes in one test host** (real Program, real
listeners, real gRPC peer streams over loopback). Six pass on **separate OS processes** with a real SIGKILL: single-node
durability, replicated SIGKILL (peers up, and peers killed first), the two RegionProcesses harness tests and R8's cursor
move after sjc's SIGKILL. Partitions are **simulated**
(each node's PeerLink TCP proxy is cut: resets plus refused connects), not kernel or 6PN faults. The lossy link is a **TCP
proxy** (delay, cap, resets). Fly: never run against design C.

**Release rule.** Release correctness and the 1M-document acceptance stay **BLOCKED** until every row's replacement passes
against the design-C replicated path at the exact release commit, both locally and on Fly. A row that is partial, an
expected failure or missing blocks. The obsolete binary harness is not coming back.

Status words:

- **pass**: every test of the row ran on the design-C replicated path (nodes as stated in the row) and held.
- **partial**: some of the row's tests pass, the rest are expected failures (named in the row).
- **expected failure (slice)**: the replacement exists and is a strict expected failure in `Gaps.cs` until that slice
  lands. It counts only when the scenario fails its own way with the gap's declared signature (`Expected.cs`). A 5xx, a
  crash, a lock or a harness error fails the test. It fails as soon as it passes.
- **missing**: no replacement yet.
- **BLOCKED on Fly**: the scenario cannot run with the Fly suite's credential (a TerraBase API key, `documents:read/write`
  on the e2e project only). It is skipped there with that reason, never counted as an expected failure, and blocks like a
  missing row. E12, E13, flow control (row 14), DDL (row 22) and the deleted database (row 23) write databases, indexes or
  schemas, which needs `databases:*`; unblocked by the GitHub-OIDC → ts-auth project token.

## Candidates

Each candidate's results stay under its own SHA; counts are never merged across candidates. A candidate counts only once
the R3 owner acknowledged that exact SHA on #108 and both owners' repeats are posted there.

| Candidate | SHA (frozen branch) | Base (harness) | Strict owner, 3 runs | R3 owner, 3 runs | Recorded |
| --- | --- | --- | --- | --- | --- |
| combined | `f929c623` (`test/e2e-candidate-f929c623`) | #106 `81fa4fcb` | 37/4/11/1/2/0 of 55 | 37/4/11/1/2/0 of 55 | historical |
| candidate 2 | `f1ddb954` | #111 `07f8d81f` | 43/4/9/0/3/1 of 60 | 43/4/9/0/3/1 of 60 | historical (unique failed under load; undeclared tests) |
| candidate-3 | `ab499d84` (`test/candidate-3`) | #112 `8b8758ff` | 45/4/8/0/3/0 of 60 | 45/4/8/0/3/0 of 60 | recorded; superseded by candidate-8 |
| candidate-4 | `4193f8fe` (`test/candidate-4`) | #114 `594b052a` | 49/4/6/0/3/0 of 62 | not run (stale, per COS) | historical, strict owner only |
| candidate-5 | `77f0072c` (`test/candidate-5`) | `200e08e1` = #114 `594b052a` + R8 #118 `90c1b833` | 53/4/4/0/3/0 of 64 | not run (stale, per COS) | historical, strict owner only |
| candidate-6 | `5148157a` (local) | `5b5a028f` = #120 `811dbf6e` + R8 `90c1b833` | 53/4/4/0/3/0 ×2, 52/4/4/0/3/**1** | not run | historical; run 3 failed on the PeerLink abort race |
| candidate-7 | `4b350f97` (local) | `187af4e8` = `e8b458e5` + `a77a7574` + R8 `90c1b833` | 53/6/4/0/3/0 of 66 | 52/6/4/0/3/**1**, 53/6/4/0/3/0 ×2 | **failed**: an R3 run's writer gate stopped at 195 of 200 under host load 38.7/16 |
| candidate-8 | `70b55472` (local) | `dea6b364` = `80c67345` + R8 `90c1b833` | 53/13/4/0/3/0 of 73 | 53/13/4/0/3/0 of 73 | **yes: rows below** |

Counts read actual pass / harness check / expected failure / not supported / skip / fail.

## Last runs

Summaries come from `make terrabase-e2e` itself (`tests/TerraBase.E2E/Outcomes.cs`, `scripts/terrabase/e2e-summary.sh`),
never from a pass count. Each test declares its nodes and faults (`ScenarioAttribute`; the harness owner's own tests carry
it with their OK). Each line says whether a test ran on in-process nodes, separate OS processes or no node
(harness check), and whether its partition was simulated or its fault a TCP-proxy network fault. CI's `terrabase-check`
rejects a block here or in `RUNS.md` that lacks a category, the commit, the working-tree patch identity, the stacked-on
line or a test's nodes and faults, or whose counts do not add up.

candidate-8 `70b55472`, 2026-10-12, 6 runs on delta (3 per owner, each from its own worktree and ts-dotnet worktree),
every run under `flock /tmp/ts-heavy.lock`. They are identical test by test, including every expected failure's detail.
Every block is in `RUNS.md`:

| Run | Commit (candidate) | Stacked on | Working tree | actual pass | harness check | expected failure | not supported | skip | fail | total | Host load1 start → end |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| strict owner 1 of 3 | `70b55472207f` | `dea6b364a8b3` | clean | 53 | 13 | 4 | 0 | 3 | 0 | 73 | 11.66 → 14.98 |
| strict owner 2 of 3 | `70b55472207f` | `dea6b364a8b3` | clean | 53 | 13 | 4 | 0 | 3 | 0 | 73 | 18.02 → 3.81 |
| strict owner 3 of 3 | `70b55472207f` | `dea6b364a8b3` | clean | 53 | 13 | 4 | 0 | 3 | 0 | 73 | 4.37 → 2.38 |
| R3 owner 1 of 3 | `70b55472207f` | stamp: merge base `740ad0d6b2d0` (see below) | clean | 53 | 13 | 4 | 0 | 3 | 0 | 73 | 6.90 → 3.24 |
| R3 owner 2 of 3 | `70b55472207f` | same | clean | 53 | 13 | 4 | 0 | 3 | 0 | 73 | 3.11 → 3.36 |
| R3 owner 3 of 3 | `70b55472207f` | same | clean | 53 | 13 | 4 | 0 | 3 | 0 | 73 | 3.94 → 3.03 |

The R3 owner's runs did not set `E2E_STACKED_ON`, so their stacked-on line shows the default (the merge base with
origin/main). The commit stamp is the candidate's exact SHA, so the code is the same.

**Where the 53 actual passes ran:**
- **47 on in-process nodes:**
  - 31 with no fault;
  - 5 node stop, plus 2 node stop with file tampering;
  - 2 storage stall and 2 clock skew;
  - 3 simulated partition, plus 1 with clock skew;
  - 1 TCP-proxy network fault.
- **6 on separate OS processes** with SIGKILL.

**Harness checks (13):**
- 4 harness self-checks;
- 2 PeerLink regressions;
- 7 recorder controls (`ExpectedTests`): a 5xx, a real NullReferenceException, a real `database is locked`, a convergence
  without the signature and a plain harness timeout all fail a listed gap.

**Skipped in every run:** the RegionProcesses child host, the benchmark's cluster host, the Fly outage test.

**Expected failures (4)**, each failing its own way with its declared signature:
- E11 (R9): `only SELECT statements are supported`;
- E12 (R10a): `sjc's database on gru did not converge`;
- E13 (R11): `/move answered 404`;
- the deleted database (R10a + R7): `the new database on sjc did not converge`.

The commits after candidate-8's code on this branch change only `COVERAGE.md` and `RUNS.md`.

The strict owner's last run in full:

<!-- e2e-summary -->
E2E run (candidate-8 70b55472, run 3 of 3), 2026-10-12T10:08Z on delta

- Commit: `70b55472207fff0ac25fd8ecd5d352b66e04956e`; working tree: clean
- Stacked on: `dea6b364a8b345f652252256b3c2222ee5bc951f` (the harness commit this candidate sits on)

| Category | Tests |
| --- | ---: |
| actual pass | 53 |
| harness check | 13 |
| expected failure | 4 |
| not supported | 0 |
| skip | 3 |
| fail | 0 |
| total | 73 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `A_database_deleted_while_a_region_is_down_does_not_come_back` | expected failure | in-process nodes | NodeStop | the catalog as a lattice database (R10a) and garbage collection (R7); failed as named: NotConvergedException with "the new database on sjc did not converge" |
| `E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add` | expected failure | in-process nodes | none | SQL writes (R9) over peer streams (R4a); failed as named: HttpRequestException with "only SELECT statements are supported" |
| `E12_database_created_in_sjc_is_usable_from_gru_and_one_same_name_database_survives` | expected failure | in-process nodes | none | the catalog as a lattice database (R10a); failed as named: NotConvergedException with "sjc's database on gru did not converge" |
| `E13_move_from_gru_to_sjc_under_write_load_loses_no_write_and_succeeds` | expected failure | in-process nodes | none | home region and live move (R11); failed as named: HttpRequestException with "/move answered 404" |
| `A_5xx_answer_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_convergence_without_the_signature_is_never_the_gap` | harness check | no node (harness) | none |  |
| `A_database_lock_is_never_the_gap` | harness check | no node (harness) | none |  |
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

## Scenarios

| # | Scenario | Old test (deleted in #48) | Replacement (tests/TerraBase.E2E) | On the design-C replicated path? | Last run | Notes |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | Write in one region readable in the others | Fast/CrossRegion/CrossRegionTests: `A_write_in_either_region_is_readable_in_the_other_with_the_same_version` | ReplicationE2ETests: `Write_in_gru_is_readable_in_sjc…` (E1), `Write_in_sjc_is_readable_in_gru…` (E2); ThreeRegionE2ETests: `Write_in_one_region_is_readable_in_the_other_two` ×6 pairs | pass | 70b55472: pass ×8 (E1, E2, all six pairs) | All 3 pairs, both ways |
| 2 | Concurrent same-field writes, 2-way | Fast/Replication/ReplicationTests: `Two_nodes_that_write_at_the_same_time_converge`; CrossRegionTests: `Concurrent_writes_in_both_regions_converge_on_the_same_documents` | ReplicationE2ETests: `Concurrent_writes_to_the_same_field_converge_to_one_identical_value` (E3) | pass | 70b55472: pass | |
| 3 | Concurrent same-field writes, 3-way | (none; the old suite had 2 nodes or 2 regions) | ThreeRegionE2ETests: `Concurrent_writes_to_the_same_field_in_three_regions_converge_to_one_written_value`; `Burst_of_overwrites_from_three_regions_converges_to_a_last_write` | pass | 70b55472: pass (three-way, burst) | The benchmark adds 200 docs × 5 rounds per region (`benchmark/bench`) |
| 4 | Concurrent different-field writes, 2-way | (none; the old engine kept whole-document LWW) | ReplicationE2ETests: `Concurrent_writes_to_different_fields_both_survive…` (E4) | pass | 70b55472: pass | Per-field lattice R1/R2a, replicated by R4a |
| 5 | Concurrent different-field writes, 3-way | (none) | ThreeRegionE2ETests: `Concurrent_writes_to_different_fields_in_three_regions_all_survive` | pass | 70b55472: pass | |
| 6 | Delete vs concurrent edit | ReplicationTests: `A_version_read_on_one_member_deletes_the_document_on_the_other`; Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: `Delete_in_one_region_and_edit_in_the_other…` (E5); ThreeRegionE2ETests: `Delete_in_one_region_and_edits_in_the_other_two…` | pass | 70b55472: pass (E5, three-way) | Many concurrent deletes: the benchmark's 1 % deletes |
| 7 | Partition, then heal | CrossRegionTests: `Partitioned_regions_keep_serving_their_writes_and_converge_after_heal`; Slow/CrossRegion/CrossRegionPartitionTests: `Regions_converge_without_losing_or_corrupting_writes_through_a_lossy_partitioned_link_and_restarts` | ThreeRegionFaultE2ETests: `Partitioned_region_keeps_accepting_writes_and_converges_after_the_partition_heals` | pass (simulated partition) | 70b55472: pass | `Cluster.Partition` cuts each node's PeerLink proxy both ways (resets plus refused connects), not a kernel or 6PN fault. Local only; Fly has no partition switch (skipped there, with the reason) |
| 8 | Region down, then rejoin | Fast/CrossRegion/CrossRegionDeletionTests: `A_restarted_region_removes_…`; (Fly) FlyOutageE2ETests | ThreeRegionFaultE2ETests: `Region_down_then_back_catches_up_on_every_write_made_while_it_was_down`; FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` (E7) | pass (in-process node stop); Fly half skipped | 70b55472: pass; E7 (Fly) skipped | Fly runs only through `make terrabase-e2e-fly-outage` |
| 9 | Node restart mid-replication (receiver); restore to an older state | ReplicationTests: `A_restarted_node_resumes_after_its_peers_applied_prefix`; Slow/ReceiverRestoreTests: `A_caught_up_sender_repairs_a_restarted_receiver_restored_to_an_older_frontier` | ThreeRegionFaultE2ETests: `Restart_in_the_middle_of_replication_loses_no_write`; RepairAndContractE2ETests: `A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes` | pass (in-process node stop, file restore) | 70b55472: restart pass; restore pass | Restore copies sjc's data directory back to a snapshot taken 100 writes earlier |
| 10 | Sender restarts with an unsent backlog | Fast/Replication/HintedHandoffTests: `A_restarted_sender_replays_durable_backlog_when_its_peer_returns_without_new_writes` | ThreeRegionFaultE2ETests: `A_restarted_region_replays_its_backlog_to_a_region_that_returns` | pass | 70b55472: pass | New in this change |
| 11 | Clock skew | Fast/Replication/ReplicatedStampTests: `A_stamp_far_ahead_is_applied_and_counted_but_moves_the_clock_only_the_skew_ahead` | ThreeRegionFaultE2ETests: `Causally_later_write_wins_everywhere_despite_clock_skew` (±2 s), `Concurrent_writes_with_a_clock_2_s_ahead_converge_and_the_writer_ahead_wins` | pass (clock skew; the concurrent case also partitions, simulated) | 70b55472: pass ×3 | Missing: the `terrabase.clock.skew_capped` counter assertion (R4a metrics). Local only (skipped on Fly) |
| 12 | Acknowledged-write durability: ack, then crash | Slow/ReplicationChaosTests: `Three_nodes_preserve_acknowledged_writes_after_partition_skew_storage_stall_and_crash`, `The_history_checker_rejects_a_lost_or_changed_acknowledged_write` | CrashAndStallE2ETests: `Writes_acknowledged_before_a_SIGKILL_survive_the_restart_of_their_node_process` (single node, real SIGKILL of a `terrabase` process, NodeProcess harness); `Writes_acknowledged_before_a_SIGKILL_reach_every_region_once_the_node_is_back` (replicated, separate OS processes per region); ThreeRegionFaultE2ETests: `Writes_acknowledged_just_before_their_region_stops…` (clean stop) | pass: single-node SIGKILL and replicated SIGKILL on **separate OS processes**, with peers up and with the peers killed first (acknowledged writes certainly unshipped); clean stop in-process | 70b55472: SIGKILL single pass; replicated SIGKILL pass ×2; ack-then-stop pass | | Process kill, not power loss: an OS crash can lose the last commits with `synchronous = NORMAL` (design §18), which no local test simulates |
| 13 | Storage stall | Slow/ReplicationChaosTests (storage stall phase); Fast/Replication/CaptureAdmissionTests | CrashAndStallE2ETests: `Writes_through_a_storage_stall_are_either_refused_or_kept_and_writing_resumes_after_it` (single node); `A_storage_stall_on_one_region_loses_no_acknowledged_write_and_the_regions_converge` (sjc stalls 10 s) | pass (harness stall of the node's single writer, in-process) | 70b55472: pass ×2 (single node, replicated) | The harness holds the node's single writer gate (`Cluster.StallStorageAsync`), so commits wait; Turso forbids a second writer. A slow fsync would need an IO-delay shim, which is not built |
| 14 | CDC / replication backlog drain; flow control | Slow/ReplicationSlowTests: `Advancing_ACKs_keep_a_bandwidth_limited_window_alive…`, `A_database_written_without_pause_does_not_hold_back_the_others`; ReplicationTests: `Exhausted_byte_credit_blocks…`, `The_sender_stays_within_the_byte_credit…` | ThreeRegionFaultE2ETests: `Replication_backlog_of_3000_writes_drains_after_the_region_returns`; RepairAndContractE2ETests: `A_hot_database_does_not_hold_back_replication_of_another` | pass (backlog drain, flow control); retention cut untested | 70b55472: pass ×2 | Flow control: Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`). The 24 h / 1 GiB retention cut and `refill_required` cannot run in real time end to end. R6 must ship them as a Fast test with a fake clock; that requirement is tracked here |
| 15 | Large documents | CrossRegionTests: `A_group_larger_than_one_reply_crosses_in_parts` | ThreeRegionE2ETests: `Documents_near_the_size_limit_and_with_many_fields_replicate_intact` (900 KiB, 2000 fields) | pass | 70b55472: pass | |
| 16 | Counters (PN) | (none) | LatticeE2ETests: E8; ThreeRegionE2ETests: `Concurrent_inc_in_three_regions_sums` | pass | 70b55472: pass (E8, three-way) | R5 is #75 |
| 17 | Sets (add-wins) | (none) | LatticeE2ETests: E9; ThreeRegionE2ETests: `Concurrent_set_adds_in_three_regions_union…` | pass | 70b55472: pass (E9, three-way) | |
| 18 | Bulk convergence with counts and digests | Slow/ReplicationSlowTests: `Two_nodes_converge_after_many_concurrent_writes_and_deletes` | ReplicationE2ETests: E6 (1000); ThreeRegionE2ETests: `Bulk_3000_documents…`; benchmark `repl-bench` (1M) | pass | 70b55472: pass (E6, 3000) | The 1M run on design C waits for R4a |
| 19 | Lossy link (drop, delay, reset) | Slow/CrossRegionPartitionTests (lossy link); Fast/Replication/SyncProtocolTests, ResponseLimitTests, PeerTransportTests | CrashAndStallE2ETests: `Writes_converge_exactly_over_lossy_peer_links` (LossyLink TCP proxy: 200 ms delay, 256 KiB/s, 5 % resets per second); `The_lossy_link_delivers_bytes_intact_through_its_delays_and_throughput_cap` (the proxy itself) | pass (TCP-proxy network fault); proxy self-check: harness check | 70b55472: pass; harness check | Byte corruption and frame duplication need fault injection inside the peer protocol (R4a), not a TCP proxy: design §9 refuses a corrupt group at the trust boundary |
| 20 | Anti-entropy repair of rows the streams missed | Fast/Replication/AntiEntropyTests: `Caught_up_members_repair_missing_and_stale_rows_without_new_writes` and 11 others | RepairAndContractE2ETests: `Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it` (deletes the document's rows from sjc's file while it is down; only a repair can bring it back) | pass (in-process node stop, file tampering) | 70b55472: pass | Repaired within the 200 s window (two 60 s rounds), R6 #114 |
| 21 | Unique constraints under partition | Fast/Replication/UniqueReplicationTests: `Partitioned_unique_writes_converge_and_a_deleted_winner_releases_its_key`; Slow/UniqueConstraintReplayTests | RepairAndContractE2ETests: `Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id` | pass (simulated partition) | 70b55472: pass | Needs #112: a replicated `_unique` document re-projects its collection. Missing: a deleted winner releasing its key |
| 22 | Collection, index and schema DDL replication | ReplicationTests: `Collections_and_indexes_created_and_dropped_on_one_member_follow_on_the_other`; CollectionSchemaReplicationTests (2) | RepairAndContractE2ETests: `Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another`; every cluster start (collection create) | pass; **BLOCKED on Fly** | 70b55472: pass | Needs #112: a replicated `_indexes` document builds or drops its index. Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 23 | Database deleted at home not brought back | CrossRegionTests: `A_database_deleted_in_its_home_is_not_brought_back…`; CrossRegionDeletionTests | RepairAndContractE2ETests: `A_database_deleted_while_a_region_is_down_does_not_come_back` (category fly-outage) | expected failure (R10a + R7); **BLOCKED on Fly** | 70b55472: expected failure | Missing: checking fra's file is removed from disk. Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 24 | Catalog: database created in one region usable in another; same-name race | CrossRegionTests: `A_commit_registers_a_database_under_a_chosen_id_once` | CatalogE2ETests: E12 | expected failure (R10a); **BLOCKED on Fly** | 70b55472: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`) |
| 25 | Placement and move | CrossRegionTests: placement tests (5) | CatalogE2ETests: E13 | expected failure (R11); **BLOCKED on Fly** | 70b55472: expected failure | Fly: skipped as BLOCKED, no appropriately-scoped auth (API keys can't hold `databases:*`). `fly-replay` itself is covered by #50's Fast tests, not here |
| 26 | Changes feed across regions | (none: the per-collection feed and shapes were deleted in R2c) | LatticeE2ETests: E10 | pass (in-process; and on separate OS processes) | 70b55472: pass (E10; R8's cursor moves region in-process and after sjc's SIGKILL) | LatticeE2ETests E10 and ChangesFeedE2ETests (R8 #118) |
| 27 | SQL writes merge across regions | (none) | LatticeE2ETests: E11 | expected failure (R9) | 70b55472: expected failure | |
| 28 | ts-auth refresh reuse across regions | (none) | LatticeE2ETests: E15 (storage half) | pass (storage half; the ts-auth half is R12) | 70b55472: pass |  |
| 29 | Replication health | Slow/ReplicationObservabilityTests (2); ReplicationStatusTests (3) | RepairAndContractE2ETests: `Every_region_reports_its_peers_and_replication_lag_on_health` (`/health/replication` status ok with both peers and lag ≤ 5 s) | pass | 70b55472: pass | |
| 30 | Metric: stream send lag | Slow/ReplicationObservabilityTests | RepairAndContractE2ETests: `Every_region_publishes_the_replication_lag_series(terrabase_replication_send_lag)` (a sample line on every region's `/metrics`) | pass | 70b55472: pass | Local only (the metrics listener) |
| 31 | Metric: stream ack lag | Slow/ReplicationObservabilityTests | `Every_region_publishes_the_replication_lag_series(terrabase_replication_ack_lag)` | pass | 70b55472: pass | Local only |
| 32 | Metric: CDC tail lag | (none) | `Every_region_publishes_the_replication_lag_series(terrabase_replication_tail_lag)` | pass | 70b55472: pass | R3 `4c32da5e`: age of the oldest local commit a live peer has not acknowledged. Metering per direction (`Both_regions_meter_encoded_replication_bytes_by_direction…`) is not covered |

## Counts (2026-10-12, candidate-8 70b55472)

| Row status | Rows |
| --- | ---: |
| pass | 28 (1–22, 26, 28–32; rows 12 and 26 also on separate OS processes, the rest in-process) |
| partial | 0 |
| expected failure | 4 (23 R10a + R7, 24 R10a, 25 R11, 27 R9) |
| missing (no replacement) | 0 |

No row passes on Fly. Sub-scenarios still without a test:
- power loss (12);
- a slow fsync (13; the stall holds the writer);
- the 24 h / 1 GiB retention cut end to end (14; R6's Fast suite covers it on a shifted clock);
- corruption and duplication inside the peer protocol (19);
- a deleted unique winner releasing its key (21);
- file removal of a deleted database (23);
- metering per direction (32).

R10a is not in any candidate yet: its product code conflicts with the R7 line, and the owners reconcile it first.

## Test hooks (owned by the R3 owner)

The R3 owner is the sole writer of the shared harness. At candidate-8:
- `Cluster.Partition`/`Heal` cut and reopen each node's PeerLink proxies (simulated partition).
- `Cluster.RouteThrough` sends a node's peer connections through a `LossyLink`.
- `Cluster.StallStorageAsync` holds a node's single writer.
- `RegionProcesses` runs each region as a separate OS process, with a file-backed machine list standing in for Fly DNS, SIGKILL and restart.
- `PeerLink.Abort` tolerates a client already closed (`80c67345`, from `a77a7574`).

## Skipped tests and why

| Test | Skipped when | Reason |
| --- | --- | --- |
| FlyOutageE2ETests: `Sjc_machine_stopped_then_started…` | locally, or on Fly without `TERRABASE_E2E_FLY_OUTAGE=1` | It stops a machine of the shared Fly app, so it runs alone through `make terrabase-e2e-fly-outage` |
| BenchmarkClusterHost: `Hosts_three_local_regions…` | always, unless `TERRABASE_BENCH_CLUSTER_FILE` is set | It is the benchmark's local cluster host, not a scenario |
| RegionProcessHost: `Hosts_one_region_until_its_process_is_killed` | always, unless started by `RegionProcesses` as a child process | It is one region of `RegionProcesses`, not a scenario |
| ThreeRegionFaultE2ETests: partition, backlog, both skew tests | on Fly | Fly has no switch for a partition or a clock skew between machines |
| ThreeRegionFaultE2ETests: region down, restart, ack-then-stop, restarted backlog; RepairAndContractE2ETests: deleted database | on Fly without `TERRABASE_E2E_FLY_OUTAGE=1` | They stop Fly machines |
| CrashAndStallE2ETests (all but the proxy self-check); RepairAndContractE2ETests: anti-entropy repair, restore, unique under partition, lag series | on Fly | They kill local processes, lock or edit local files, or partition; Fly has no such switch |

## Keeping this current

Update a row in the same PR that changes its status: a slice that lands removes its `Gaps.cs` entries and marks the row
"pass" with the commit of the run that proved it. Re-run `make terrabase-e2e` locally and `make terrabase-e2e` plus
`make terrabase-e2e-fly-outage` on Fly at the release commit, and record both in "Last run".
