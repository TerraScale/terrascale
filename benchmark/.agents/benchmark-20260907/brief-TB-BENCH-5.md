# Inventory brief: historical TerraBase diagnostic selectors

These selectors are accounted for separately from the current served public
benchmark. `bench_owner` owns release/build execution; use one artifact per
group and record source hash, command, exit code, and whether the result is
diagnostic or threshold-gated. Do not run them concurrently with the final
served lane.

| Artifact / package | Exact selector | Evidence / meaning |
| --- | --- | --- |
| `terrabase` lib test | `benchmarks::tests::terrabase_lane_counter_characterization` | Engine commissioning read/write/query counters and engineering throughput; `/tmp/terrabase-engine-bench.log`; not a public comparison |
| `terrabase` lib test | `benchmarks::optimizer_tests::ix04_adhoc_workload_characterization` | Auto-index selection/cache characterization; same engine artifact; diagnostic |
| `terrabase` lib test | `benchmarks::optimizer_tests::ix04_write_path_overhead_characterization` | Auto-index write footprint characterization; bytes are explicitly inconclusive in retained log |
| `terrabase-sync-core` integration test | `ordered_materializer_cost` | Ordered materializer scaling; `/tmp/terrabase-materializer-bench.log` or newer artifact; diagnostic |
| `terrabase-member-transport` lib test | `production::ack_timing_tests::compare_default_and_one_millisecond_ack_delay` | QUIC ACK timing diagnostic; `/tmp/terrabase-ack-bench.log`; diagnostic |
| `terrabase` lib test | `query::tests::scale_100k_fts_top_k_is_exact` | 100k FTS top-k timing and exactness; `/tmp/terrabase-fts-bench.log`; diagnostic |
| `terrabase-server` lib test | `backup::tests::branching_100k_documents_restores_under_ten_seconds` | Restore performance gate; retained evidence failed at 12.255s vs 10s and needs a source-frozen rerun before claiming fixed |

Acceptance for inventory is a nonzero selected test count and preserved output
classification. A zero-test Cargo result is invalid evidence. Any newly
observed threshold failure becomes TB-BENCH-4 follow-up with its real caller;
do not relax the assertion or turn a diagnostic timing into a served-lane
number. Existing six-family final run remains the user's requested execution
scope; these selectors are included only so the report can state what was
excluded and why.
