# Source-backed benchmark evidence

The benchmark binary has four entry paths in `bench/src/main.rs:54-117`:

* point lane: `--target terrabase --workload read|write|query|mixed`;
* bulk ingest: `--ingest-target terrabase --ingest-topology single-node`;
* local auto-index diagnostic: `--family auto-index --target terrabase`;
* sync family: `--family sync`, normally through `bench/scripts/sync.sh`.

The final orchestrator loops over all four point workloads
(`bench/scripts/run-terrabase-final.sh:43-62`), then runs one single-node
ingest, local auto-index, and single-replication sync. It does not create
comparator services. The family contract in `bench/FAMILIES.md` requires
point lane minimums of 1,000 seed documents, 10,000 operations, 5 samples,
concurrency 16, and 1,000 warmup operations. Ingest requires 100,000
documents/sample, 1,000/document batches, 5 samples. Auto-index is explicitly
diagnostic locally; sync needs a real served endpoint and immutable digests.

The latest retained complete point receipts are diagnostic runs with only 1
sample/500 operations (`bench/results/run-20260907/terrabase-read-1788825971998574795-1480686-0.json` and siblings), so they cannot satisfy the recorded comparison minimum. The earlier full read receipt has 5 samples and 10,000 operations but was marked diagnostic and is not a current release qualification. The latest verification says the prior full write attempt used a retired Go adapter and the corrected server release was not yet qualified (`bench/results/run-20260907/verification.md`).

Confirmed orchestration defect: `run-terrabase-final.sh` exports only the
member-prefixed API/auth variables for `new_database`, while
`auto_index_local::run` reads `TERRABASE_BENCH_TERRABASE_API` and
`TERRABASE_BENCH_TERRABASE_AUTH` (`bench/src/auto_index_local.rs:29-33`).
The same final script sets ingest endpoint, health, version, digest, and PID
but omits `TERRABASE_BENCH_INGEST_AUTHORIZATION`, which
`run_terrabase` requires (`bench/src/ingest.rs:353-372`). Both lanes therefore
fail before meaningful work unless the operator's shell happens to retain
these variables from `local-member.sh env`; the final script's fresh database
selection makes that implicit inheritance unsafe.

Meaningful comparison rules: only same topology and verified 2 OCPU/12 GiB
member evidence may be compared; point results are acknowledged public HTTP
operations with connection reuse; failed/unavailable/diagnostic receipts must
remain visible with no invented numbers. A TerraBase-only report can compare
workloads across the same run group and show medians, p50/p95/p99, throughput,
wall time, and family-specific facts, but must label cross-family metrics as
non-comparable.

The earlier `json_each` collection-field scan hypothesis is disproven for the
current benchmark path: `perf_explorer` confirmed the live lowered query path
does not execute `collection_fields` for the measured workload. Do not assign
an optimization task for that code based on the old hypothesis. The remaining
engine commissioning numbers are diagnostic only until a fresh source-frozen
run identifies a reproducible hot path.

The `bench/results/run-20260907-independent-1714381` process is part of this
same day's current benchmark attempts, not historical evidence or a separate
session; its live write lane must be allowed to finish or be handed off by
bench_owner before source/artifact qualification.

Additional retained TerraBase diagnostics (all historical, no current source
qualification) are listed in `/tmp/terrabase-bench-inventory.txt` and logs:
engine lane characterization (`benchmarks::tests::terrabase_lane_counter_characterization`),
auto-index optimizer characterization (`benchmarks::optimizer_tests::ix04_adhoc_workload_characterization`,
`ix04_write_path_overhead_characterization`), ordered materializer
(`terrabase-sync-core` `ordered_materializer_cost`), QUIC ACK timing
(`terrabase-member-transport` `production::ack_timing_tests::compare_default_and_one_millisecond_ack_delay`),
FTS top-k (`query::tests::scale_100k_fts_top_k_is_exact`), and restore
(`terrabase-server` `backup::tests::branching_100k_documents_restores_under_ten_seconds`,
historically failed at 12.255s versus a 10s bound). These are separate
artifacts and must not be mixed into the public served-lane table.
