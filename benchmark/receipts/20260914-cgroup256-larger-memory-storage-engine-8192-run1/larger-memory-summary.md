# TerraBase larger-than-memory benchmark

Status: **failed**

- Commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (dirty: `True`)
- Artifact SHA-256: `bb2dde0ca26db0c408039d16ff5d8ebaf50dcf31b3ceb7d583ee5a720819dbdb`
- Runtime: .NET 10.0.12; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 201326592 bytes
- Cgroup memory/cpu limits: unavailable in the receipt; the `cgroup256` directory name does not prove an enforced cgroup.

Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload larger-memory --backend storage-engine --memory-cap-bytes 4194304 --dataset-size 8192 --payload-size 2048 --samples 5 --operations 50 --writes 50 --warmup 20 --concurrency 1 --seed 1337 --zipf-exponent 1.2 --read-ratio 0.8 --output bench/receipts/20260914-cgroup256-larger-memory-storage-engine-8192-run1`

## Configuration

Backend: `StorageEngine`; dataset: `8,192` rows; payload: `2,048` bytes; configured memory threshold: `4,194,304` bytes (not an enforced cgroup limit).

## Phase results

| Sample | Phase | Kind | Status | Operations | Throughput ops/s | p50 µs | p95 µs | p99 µs | Allocated bytes/op | CPU % | RSS after bytes | WAL bytes | Write amplification |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| — | — | — | failed | — | — | — | — | — | — | — | — | — | — |

Error: `System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.    at System.IO.MemoryStream.set_Capacity(Int32 value)    at System.IO.MemoryStream.EnsureCapacity(Int32 value)    at System.IO.MemoryStream.Write(Byte[] buffer, Int32 offset, Int32 count)    at System.IO.Stream.CopyTo(Stream destination, Int32 bufferSize)    at TerraBase.StorageEngine.FileDurableLog.RecoverUnsafe() in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/DurableLog.cs:line 400    at TerraBase.StorageEngine.FileDurableLog..ctor(String path) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/DurableLog.cs:line 105    at TerraBase.StorageEngine.StorageEngine..ctor(String path, IStorageTierPlacement placement) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageEngine.cs:line 116    at LargerThanMemoryWorkload.Open(String path, Backend backend) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 534    at LargerThanMemoryWorkload.EngineHandle.ReopenAsync(CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 819    at LargerThanMemoryWorkload.RunSampleAsync(EngineHandle handle, Configuration configuration, IReadOnlyList`1 keys, JsonElement payload, KeyDistribution distribution, Int32 sampleIndex, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 169    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 102    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 115    at LargerThanMemoryWorkload.RunAsync(Configuration configuration, String outputDirectory, CancellationToken cancellationToken) in /home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/LargerThanMemoryWorkload.cs:line 115`
