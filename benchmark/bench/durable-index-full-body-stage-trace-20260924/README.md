# DurableIndexStore full-body query stage trace

This is a separate, opt-in diagnostic for the direct public `DurableIndexStore.QueryAsync` path. It leaves the existing full-body parity campaign and receipts unchanged. Source instrumentation lives in TerraBase and is owned by the TerraBase instrumentation worker; this project listens to the agreed `TerraBase.Indexing.Query` ActivitySource contract without depending on internal TerraBase diagnostic types.

The workload mirrors the qualified full-body query shape: 3,000 base documents, 100 maintenance inserts, 100 maintenance updates, 98,304 deterministic payload bytes per body, one status equality index, and the `status = s17` query returning 31 complete documents. Every query verifies the ordered IDs, full body, payload, and version outside its timed interval.

For each pair, the runner records one plain query and one query with the ActivityListener attached. The plain and traced warm samples are reported separately. Their paired elapsed-time difference is an observed instrumentation/listener overhead estimate; it is not subtracted from stage totals. Query-level phases are captured as aggregates rather than per-document Activities so tracing does not allocate one span per candidate:

- entry candidate seek/build;
- committed document read, including key encoding;
- decode and result-body materialization/clone;
- predicate recheck;
- final sort;
- total query and gate wait when exposed by the producer.

The JSON receipt includes every sample and median stage totals, phase counts, listener-overhead pairs, the fixture and query definition, source commit and relevant source hashes, harness hashes, loaded assembly hashes, and the runtime/machine identity. The Markdown report summarizes the medians. A missing stage span/tag is an error; the runner does not silently treat unsupported instrumentation as a zero-duration phase.

## Build and run

Do not start the qualified workload while other tests or storage benchmarks share the CPU. Build only after the TerraBase instrumentation changes are present and the shared build environment is clear.

The full qualification uses a 256 MiB memory cap, zero swap, and a 128 MiB GC heap cap:

```sh
dotnet build DurableIndexQueryStageTrace.csproj -c Release
systemd-run --user --scope --collect --quiet \
  -p MemoryMax=256M -p MemorySwapMax=0 -p OOMPolicy=stop \
  env DOTNET_GCHeapHardLimit=8000000 \
  dotnet bin/Release/net11.0/DurableIndexQueryStageTrace.dll --trace-stages --output=run-<timestamp>
```

The runner requires `--trace-stages`; without it, it prints usage and exits without creating a database. Output belongs in a unique `run-*` subdirectory here. A smaller `--smoke` mode is only a functionality check and is not comparable to the full-body parity run.
