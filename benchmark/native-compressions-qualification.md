# NativeCompressions qualification

This is a source-matched performance audit of the NativeCompressions 0.6.1
adapters used by TerraBase.ZoneTree. It records measured results and keeps
inferences about storage and live backup separate from those measurements.

## Source and environment identity

- Source commit: `9e84cc83827d1603bb307d23c6722761f1d029b4` (`dirty=True`; the
  working tree contains unrelated parent-task edits).
- Package: `NativeCompressions` 0.6.1, with LZ4 and Zstandard native runtime
  assets resolved for `linux-x64`.
- Runtime: .NET 10.0.12, SDK 10.0.401, Release build.
- Host: NixOS 26.05 (Yarara), x64, Intel Xeon E5-2660 v4 @ 2.00 GHz, 46
  logical processors, 20,937,965,568 bytes available memory.
- GC: workstation GC, interactive latency mode.
- ZoneTree Release assembly SHA-256:
  `4790dfa4b9385d3f32bfbfd4716e6ad9a1c06880eef84576f08270d33c48b0f9`.

At measurement time, the compression adapter source was unchanged in the
dirty tree. The LZ4
adapter uses a 4 MiB frame block, content size, and content/block checksums.
It rejects declared or expected decompressed lengths over 64 MiB and checks
that the decoder consumes the entire frame and writes the expected length
([`LZ4DataCompression.cs`](../src/TerraBase.ZoneTree/Compression/LZ4DataCompression.cs:5)).
The legacy no-content-size path allocates a bounded 64 MiB decode buffer before
it knows the actual length ([`LZ4DataCompression.cs`](../src/TerraBase.ZoneTree/Compression/LZ4DataCompression.cs:37)).
That is a bounded resource floor, but its production frequency was not
measured.

## Measured primitive boundary

Command:

```text
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release --no-restore
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload compression --storage memory --samples 9 --warmup 8 --operations 50 --concurrency 1 --seed 1337 --level 3 --output /tmp/terrabase-native-compression-current-20260914
```

The harness directly measures `DataCompression`, in memory, with no WAL,
segment, file-system, or backup framing. Warm values are medians across nine
samples; p50/p95/p99 are microseconds per operation and allocations are bytes
per operation.

| Payload | Method | Operation | Output bytes | p50 / p95 / p99 (µs) | Allocated B/op |
| ---: | --- | --- | ---: | ---: | ---: |
| 4 KiB | LZ4 | Compress | 173 | 38.52 / 60.44 / 185.78 | 20,470.72 |
| 4 KiB | LZ4 | Decompress | 4,096 | 6.64 / 12.93 / 35.28 | 4,227.52 |
| 4 KiB | None | Compress | 4,096 | 0.09 / 0.11 / 0.34 | 0 |
| 4 KiB | None | Decompress | 4,096 | 2.63 / 3.61 / 5.94 | 4,215.20 |
| 256 KiB | LZ4 | Compress | 1,185 | 472.20 / 1,466.13 / 1,995.80 | 1,311,117.44 |
| 256 KiB | LZ4 | Decompress | 262,144 | 245.74 / 824.83 / 1,308.64 | 262,409.92 |
| 256 KiB | None | Compress | 262,144 | 0.08 / 0.08 / 0.22 | 0 |
| 256 KiB | None | Decompress | 262,144 | 56.88 / 400.02 / 1,139.76 | 262,232.80 |
| 4 MiB incompressible | LZ4 | Compress | 4,194,335 | 223,621.13 / 315,803.09 / 408,111.20 | 20,973,888 |
| 4 MiB incompressible | LZ4 | Decompress | 4,194,304 | 5,089.04 / 6,827.37 / 10,213.69 | 4,194,786.88 |
| 4 MiB incompressible | None | Compress | 4,194,304 | 0.11 / 0.14 / 0.59 | 0 |
| 4 MiB incompressible | None | Decompress | 4,194,304 | 1,509.31 / 5,854.37 / 8,044.92 | 4,194,566.08 |

The LZ4 size result is strong for the generated document profiles (173 bytes
from 4 KiB and 1,185 bytes from 256 KiB), but it is a poor choice for the
incompressible 4 MiB profile: it adds 31 bytes and costs about 224 ms median
compression time with about 20 MiB allocated per operation. This is evidence
for keeping LZ4 opt-in at this boundary, not for changing the current Zstd
defaults. It does not establish an LZ4-versus-Zstd primitive result because
this harness compares LZ4 with `CompressionMethod.None`.

The runner records `--concurrency 1` as metadata; the primitive implementation
does not fan work out to concurrent workers. Horizontal scaling, vertical
scaling, and compression behavior under contention were not measured.

## Storage and FullTextSearch measurements

The source-matched provider receipts below use the default disk segment and WAL
method (`CompressionMethod.Zstd`, level `Zstd0`). They all use commit
`9e84cc83827d1603bb307d23c6722761f1d029b4` with a dirty tree, but they are
separate build artifacts. The receipt and runner artifact are included for
each row so these baseline numbers are not mistaken for one binary or for the
later `v2`/`v3` batches.

| Path | Workload | Warm p50 / p95 / p99 (µs) | Throughput | Allocated B/op | Receipt / runner artifact SHA-256 |
| --- | --- | ---: | ---: | ---: |
| ZoneTree | PointRead | 21.24 / 29.33 / 47.31 | 41,067 ops/s | 9,101.46 | [`receipt`](receipts/20260914-source-matched-zonetree-point-read-both/receipt.json) / `ef6f743504666478a6df2d856afa00ddff9e5c5754538e742ce3630c50eaab4a` |
| ZoneTree | PointWrite | 9,035.23 / 17,839.56 / 22,987.14 | 98 ops/s | 14,360.48 | [`receipt`](receipts/20260914-source-matched-zonetree-point-write-both/receipt.json) / `8213a13f6759c92b8dc19a19d3a8c9e291880c5a5110cbd27cea7e7eceba60fd` |
| StorageEngine | PointRead | 5.20 / 9.15 / 23.04 | 129,645 ops/s | 1,172.60 | [`receipt`](receipts/20260914-source-matched-storage-engine-point-read-both/receipt.json) / `ef6f743504666478a6df2d856afa00ddff9e5c5754538e742ce3630c50eaab4a` |
| StorageEngine | PointWrite | 1,393.32 / 3,317.77 / 7,345.81 | 581 ops/s | 79,449.84 | [`receipt`](receipts/20260914-source-matched-storage-engine-point-write-both/receipt.json) / `ef6f743504666478a6df2d856afa00ddff9e5c5754538e742ce3630c50eaab4a` |

These receipts are at
`bench/receipts/20260914-source-matched-*/summary.md`; they are not
compression A/B runs, so they locate the end-to-end baseline but cannot assign
cost to Zstd or LZ4.

The recorded FullTextSearch Release run used 1,000 records, eight generated
tokens per record, seed 1337, 100 warm-up operations, 200 operations per
sample, and seven requested samples. Its benchmark artifact SHA-256 was
`7ca37785e09403489c1f8f3b7f8fbcf92f7ecb5b0c4c2346ecf303f3ceec66f7`; the
loaded TerraBase.ZoneTree assembly SHA-256 was the value listed above. The
command was:

```text
nice -n 19 dotnet build bench/TerraBase.FullTextBenchmarks/TerraBase.FullTextBenchmarks.csproj -c Release --no-restore
nice -n 19 dotnet bench/TerraBase.FullTextBenchmarks/bin/Release/net10.0/TerraBase.FullTextBenchmarks.dll --records 1000 --tokens 8 --samples 7 --warmup 100 --operations 200 --seed 1337
```

| Compression | Operation | p50 / p95 / p99 (µs) | Allocated B/op | Other result |
| --- | --- | ---: | ---: | --- |
| Zstd | AddRecord | 40.70 / 62.36 / 114.157 | 522.7 | flush 147.767 ms, 17,647 bytes |
| None | AddRecord | 19.80 / 35.99 / 53.313 | 489.0 | flush 152.690 ms, 171,965 bytes |
| Zstd | SimpleSearch | 6,631.80 / 9,673.455 / 12,265.419 | 1,737,792.6 | 2,500 results |
| None | SimpleSearch | 6,605.65 / 10,146.100 / 11,643.050 | 1,737,792.6 | 2,500 results |

On this corpus, Zstd reduces flushed segment bytes by 89.7% while search
allocations are identical. The measured read bottleneck is result processing
for the 2,500-result query, not compression. The FullTextSearch benchmark
accepts only `zstd` and `none`; no source-matched LZ4 FullTextSearch run exists
yet. Also, its `Measurement.ToString` currently prints `samples=5` even when
the command requests seven samples, so the table uses the command and receipt
configuration rather than that label.

## Backup path audit

Live backup copies persisted disk segment files as-is. Only in-memory record
batches pass through `DataCompression`: the current default is Zstd level 0,
with 1 MiB uncompressed blocks ([`LiveBackupOptions.cs`](../src/TerraBase.ZoneTree/Backup/LiveBackupOptions.cs:73)).
The writer materializes each block with `MemoryStream.ToArray()`, compresses it,
then writes a two-int length header and the compressed bytes
([`LiveBackupRecordBatchWriter.cs`](../src/TerraBase.ZoneTree/Backup/LiveBackupRecordBatchWriter.cs:69)).
The reader allocates the decompressed block and copies it again with
`ToArray()` ([`LiveBackupRecordBatchReader.cs`](../src/TerraBase.ZoneTree/Backup/LiveBackupRecordBatchReader.cs:105)).
These are source-backed allocation hypotheses; no end-to-end writer/reader
latency or allocation receipt exists in this qualification.

LZ4 is therefore not selected by any current default in storage, WAL, live
backup, or FullTextSearch. Its current implicit compatibility use is the
legacy multipart metadata fallback, while new multipart metadata uses Zstd
([`MultiPartMetadataCodec.cs`](../src/TerraBase.ZoneTree/Segments/MultiPart/MultiPartMetadataCodec.cs:7)).

## Decision, confidence, and smallest next measurement

The measured evidence supports retaining Zstd as the default for disk segments,
WAL, live-backup record batches, and FullTextSearch segments. LZ4 should remain
an explicit profile until a workload demonstrates that its CPU and allocation
cost is acceptable for the target data shape. Confidence is high for the
primitive and FullTextSearch observations, medium for the storage baseline,
and low for any live-backup throughput conclusion because its framed path was
not measured.

The smallest next measurement is one source-matched backup harness using a
1 MiB block and fixed serialized records, run with `Zstd0`, `LZ4` level 3, and
`None`. It should report writer and reader p50/p95/p99, throughput, allocated
bytes per record, and stored/uncompressed bytes at concurrency 1, then repeat
at a small contention point (for example four workers). The same harness can
also add `LZ4` to the FullTextSearch compression sweep, resolving the two
remaining selection gaps without changing product code.

## Validation

The Release builds of `TerraBase.Benchmarks` and
`TerraBase.FullTextBenchmarks` passed with zero warnings and errors. The
ZoneTree compression/integrity test project passed 19/19 tests, and the
FullTextSearch unit project passed 72/72 tests. The full
`TerraBase.ZoneTree.UpstreamTests` project could not compile in the dirty
working tree because the unrelated untracked `BoundaryFuzzTests.cs` currently
has `ReadOnlyMemory<byte>` collection-expression/type errors and CA2012
`ValueTask` diagnostics.
