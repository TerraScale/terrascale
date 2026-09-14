# TerraBase larger-than-memory benchmark

Status: **failed**

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `8213a13f6759c92b8dc19a19d3a8c9e291880c5a5110cbd27cea7e7eceba60fd`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20937965568 bytes

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 67108864 --dataset-size 32768 --payload-size 2048 --samples 5 --operations 200 --writes 200 --warmup 100 --concurrency 1 --seed 1337 --zipf-exponent 1.2 --read-ratio 0.8 --output bench/receipts/20260914-source-matched-larger-memory-storage-engine`

## Configuration

Backend: `StorageEngine`; dataset: `32,768` rows; payload: `2,048` bytes; memory cap: `67,108,864` bytes.

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| — | — | — | failed | — | — | — | — | — | — | — | — | — | — |

Error: `TerraBase.DocumentSchemaValidationException: The document does not satisfy its schema: No schema is registered for tenant 'larger-than-memory-tenant' and collection 'larger-than-memory-collection'.    at TerraBase.DocumentStore.ValidateSchema(DocumentKey key, JsonElement body) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/Storage/DocumentStore/DocumentStore.cs:line 197    at TerraBase.DocumentStore.UpsertAsync(DocumentKey key, JsonElement body, DocumentPrecondition precondition, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/Storage/DocumentStore/DocumentStore.cs:line 70    at LargerThanMemoryWorkload.SeedAsync(DocumentStore store, IReadOnlyList`1 keys, JsonElement payload, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 520    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 83    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 115`
