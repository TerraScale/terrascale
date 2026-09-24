# TerraBase larger-than-memory benchmark

Status: **failed**

- Commit: `57d12ed6ae6db472a26318a9b2671b3aa08e26b0` (dirty: `True`)
- Artifact SHA-256: `9b0cbb1c7ac623977964d1468465150bc28c42c864a5177b920706e12d7668e0`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 2 logical processors; available memory 201326592 bytes

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 150000 --payload-size 2048 --samples 5 --operations 500 --writes 500 --warmup 500 --output ../bench/larger-memory-cgroup-20260922T1740Z`

## Configuration

Backend: `StorageEngine`; dataset: `150,000` rows; payload: `2,048` bytes; memory cap: `268,435,456` bytes.

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| — | — | — | failed | — | — | — | — | — | — | — | — | — | — |

Error: `System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.    at TerraBase.StorageEngine.StorageRecordCodec.EncodePublished(StorageLogRecord record) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageRecord.cs:line 1045    at TerraBase.StorageEngine.StorageEngine.AppendUnsafe(List`1 records, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageEngine.cs:line 3753    at TerraBase.StorageEngine.StorageEngine.ApplyBatchUnsafe(IReadOnlyList`1 observations, IReadOnlyList`1 internalObservations, IReadOnlyList`1 mutations, IReadOnlyList`1 internalMutations, StorageCommitRequest request, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageEngine.cs:line 3544    at TerraBase.DocumentStore.UpsertAsync(DocumentKey key, JsonElement body, DocumentPrecondition precondition, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/Storage/DocumentStore/DocumentStore.cs:line 72    at LargerThanMemoryWorkload.SeedAsync(DocumentStore store, IReadOnlyList`1 keys, JsonElement payload, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 520    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 83    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 115`
