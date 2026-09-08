# Complete benchmark and diagnostic inventory

Audited coherent source snapshot `/tmp/terrabase-qual-fae32` for `#[ignore]`,
custom timing tests, benchmark drivers, and existing `bench/*.md` diagnostics.

## Benchmark relevant selectors

| Package/artifact | Exact command selector | Classification |
| --- | --- | --- |
| `terrabase` lib | `benchmarks::tests::terrabase_lane_counter_characterization` | Engine read/write/query commissioning counters; diagnostic |
| `terrabase` lib | `benchmarks::optimizer_tests::ix04_adhoc_workload_characterization` | Auto-index selection/cache; diagnostic |
| `terrabase` lib | `benchmarks::optimizer_tests::ix04_write_path_overhead_characterization` | Auto-index write footprint; diagnostic and explicitly inconclusive for bytes written |
| `terrabase` lib | `engine::commit_timing_probe::probe_single_document_commit_timings` | Ten synced commit timings; temporary local probe, diagnostic |
| `terrabase` lib | `query::tests::scale_100k_fts_top_k_is_exact` | 100k FTS exact top-k timing; diagnostic |
| `terrabase-server` lib | `backup::tests::branching_100k_documents_restores_under_ten_seconds` | 100k branch restore threshold; performance gate, historically failed |
| `terrabase-server` lib | `guards::tests::adversarial_neighbour_keeps_victim_p99_within_2x_solo` | Noisy-neighbour guard latency; diagnostic (receipt in `bench/guards-lock-diagnostic.md`) |
| `terrabase-sync-core` integration | `ordered_materializer_cost` | Ordered materializer scaling; ignored release diagnostic |
| `terrabase-member-transport` lib | `production::ack_timing_tests::compare_default_and_one_millisecond_ack_delay` | QUIC ACK timing; ignored diagnostic |
| `terrabase` storage | `repair_summary` module tests | Durable range-summary operation-count/correctness evidence; not latency |
| `terrabase-tbep` integration | `wal_follower::tests::idle_poll_reads_only_header_and_catchup_reads_only_new_frames` | WAL tail byte accounting; correctness/I/O diagnostic, not disk latency |

The seven primary diagnostics previously listed remain complete; this adds
the commit probe, guard, repair-summary, and WAL accounting entries found in
the source/report audit. `bench/storage-footprint-accounting.md` is a
methodology correction consumed by the engine lane, not an independent timing
benchmark. `bench/physical-restore-provenance.md` documents the restore
selector above. `tenant-scale_evidence.rs` validates E-16 fixture structure;
its own module states the 100,000-database live qualification is an explicit
opt-in operational command, not a portable benchmark.

## Ignored tests that are not TerraBase benchmarks

* `tests/simulation_harness.rs::...` — E-5/E-6 replication seams absent;
  unavailable feature coverage.
* `terrabase-server::auth_runtime::tests::...` — separately built Node SDK
  consumer fixture required.
* Two `terrabase-server::api::tests::websocket` tests — TypeScript SDK and/or
  Playwright dependencies required.

These must remain reported as excluded/unavailable and must not be run as
performance evidence. The operator benchmark entrypoints remain the four
point workloads plus local single-node ingest, local auto-index, and local
sync; comparator-only indexed-query/served-auto-index and RF2/CDN/region lanes
require external subjects and cannot be made TerraBase-only by omission.
