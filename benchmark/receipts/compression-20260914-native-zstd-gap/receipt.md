# Native compression qualification receipt

Status: **bounded partial; expanded matrix blocked by harness scope**

Run date: 2026-09-14 (UTC)

This receipt records the supported compression caller independently. It does
not turn primitive compression timings into a disk segment, WAL, live-backup,
or production capacity claim.

## Source and artifact identity

- Repository base: `d10c9073eaa4955010938e2ccf8958e350e987da`.
- Source snapshot: `/tmp/terrabase-compression-20260914-p3rIuF/source`, made
  with `git archive`; the Git worktree was not used for build or execution.
- Original cohort packet: `20260914-terrabase-cohort`, source revision
  `9c1045ddaf988c9db2b18a5e47ed2c52d6d6fb52`. No packet overlay was needed:
  the compression-relevant packet files were hash-equivalent to the base
  snapshot. The comparison is retained in
  `/tmp/terrabase-compression-20260914-p3rIuF/logs/source-identity.txt`.
- `LZ4DataCompression.cs` SHA-256:
  `14b3840a0e5f1094ed4e912409f167e9e4f428366c727e6d1bd3a3fa6e55ba24`.
- `ZstdDataCompression.cs` SHA-256:
  `aca20ae72aaae3a8f89ffd7e2a4d9dd8a9af3236acc6d90730277629d89fd797`.
- Benchmark artifact SHA-256:
  `ce23a5ee938ffe4c9848b6b3620f0b8631c210acf0e56a7aa2d4308f33f2fb6b`.
- ZoneTree artifact SHA-256:
  `ef824ae85e6bd787c052083f0bc8134bbfbbc05d012a83be95c686bc66f36976`.
- `CompressionWorkload.cs` SHA-256:
  `476c7a315ff03ce47460643457e01a09abe2f1f2cf4f85a2d280d973f9169474`.
- `DataCompression.cs` SHA-256:
  `ac6107768dea22b7707d898fe8380f2c6bcd60886c7d9694c456419d0ac872f1`.
- `ZoneTreeStorageProvider.cs` SHA-256:
  `7a2ef7a756625ea349817928d24226ea8247f1322084b0bc09ad85e735601094`.
- `DurableLog.cs` SHA-256:
  `ca6dfca5cbad6cdd571cc087816c8602d17b2f6e99394d6c3a983e8474330be5`.
- `StorageEngine.cs` SHA-256:
  `dbd3ed75dea3d004f36467d5a25ad5437f58eb3493ad1dc5357232e41bf5999d`.
- `DiskSegmentDefaultValues.cs` SHA-256:
  `21dc7d98d455aa5244adeb81e180c4b4d908c90a15b776609a5d054ebae3f8bb`.
- `LiveBackupOptions.cs` SHA-256:
  `67f72630c5822df65544090822b5179e6fa7217c8e8528f343586c37ccbef7e8`.
- Runtime: .NET 10.0.12, Release; NixOS 26.05 (Yarara), x64; Intel Xeon
  E5-2660 v4 @ 2.00 GHz; workstation GC, Interactive latency mode.
- Execution bound: `taskset -c 0`, `nice -n 19`, one worker; available memory
  at run start was 20,937,965,568 bytes. No cgroup memory floor was available.

The benchmark runner reports `Commit: unavailable` because it was deliberately
executed from the non-Git source snapshot. The base revision above is the
source binding for this receipt.

## Commands and results

The final reproducible restore, build, and execution commands were:

```text
git archive --format=tar d10c9073eaa4955010938e2ccf8958e350e987da | tar -xf - -C /tmp/terrabase-compression-20260914-p3rIuF/source
nice -n 19 taskset -c 0 dotnet restore bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj --nologo --packages /home/mariogk/.nuget/packages -p:BaseOutputPath=/tmp/terrabase-compression-20260914-p3rIuF/build/bin/
nice -n 19 taskset -c 0 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release --nologo --no-restore -p:BaseOutputPath=/tmp/terrabase-compression-20260914-p3rIuF/build/bin/
nice -n 19 taskset -c 0 dotnet /tmp/terrabase-compression-20260914-p3rIuF/build/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload compression --storage memory --samples 5 --warmup 5 --operations 20 --concurrency 1 --seed 1337 --level 3 --output /tmp/terrabase-compression-20260914-p3rIuF/output/primitive
/run/current-system/sw/bin/time -v nice -n 19 taskset -c 0 dotnet /tmp/terrabase-compression-20260914-p3rIuF/build/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload compression --storage memory --samples 5 --warmup 5 --operations 20 --concurrency 1 --seed 1337 --level 3 --output /tmp/terrabase-compression-20260914-p3rIuF/output/primitive-rss
```

Restore exit: `0` (`restore2-status.txt`). Release build exit: `0`
(`build2-status.txt`; 0 warnings, 0 errors). Primitive run exit: `0`
(`primitive-status.txt`). The external RSS run exit was `0`; it reported
97% aggregate CPU, 23.00 s wall time, 21.75 s user time, 0.57 s system time,
and maximum RSS `270,140 KiB` (`primitive-rss-selected.txt`).

The runner's cold value is the first operation for each case. Warm values are
medians over five samples of twenty operations after five warm-up operations.
Times are microseconds per operation; throughput is operations per second;
CPU is process CPU milliseconds per operation and the runner's process CPU
percentage. `Compressed frame bytes` is the case-level compressed length
captured from the cold compression output and reused as decompression input.
`Operation output bytes` is the return length of the timed operation: it is the
compressed frame length for compression and the restored payload length for
decompression. The output ratio is compressed bytes divided by logical input
bytes.

| Payload profile | Method | Operation | Compressed frame bytes | Operation output bytes | Cold p50 | Warm p50 / p95 / p99 | Warm throughput | Warm alloc/op | Warm CPU ms/op | CPU % | Stored ratio |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 KiB JSON document | LZ4 level 3 | Compress | 173 | 173 | 6,935.00 | 35.19 / 52.56 / 64.41 | 24,847.81 | 20,560.00 | 0.040 | 99.38 | 0.042236 |
| 4 KiB JSON document | LZ4 level 3 | Decompress | 173 | 4,096 | 3,775.00 | 10.71 / 19.18 / 23.72 | 96,292.73 | 4,510.00 | 0.011 | 101.96 | 0.042236 |
| 4 KiB JSON document | None | Compress | 4,096 | 4,096 | 0.40 | 0.09 / 0.11 / 0.27 | 1,739,130.43 | 0.00 | 0.001 | 118.18 | 1.000000 |
| 4 KiB JSON document | None | Decompress | 4,096 | 4,096 | 17.10 | 0.56 / 12.53 / 18.22 | 195,121.95 | 4,484.40 | 0.005 | 102.70 | 1.000000 |
| 256 KiB JSON document | LZ4 level 3 | Compress | 1,185 | 1,185 | 270.10 | 378.53 / 905.11 / 2,001.54 | 2,003.61 | 1,310,686.40 | 0.494 | 98.33 | 0.004520 |
| 256 KiB JSON document | LZ4 level 3 | Decompress | 1,185 | 262,144 | 346.20 | 252.78 / 482.36 / 964.50 | 3,112.31 | 262,409.60 | 0.318 | 98.88 | 0.004520 |
| 256 KiB JSON document | None | Compress | 262,144 | 262,144 | 0.60 | 0.09 / 0.09 / 0.24 | 1,960,784.31 | 0.00 | 0.001 | 121.95 | 1.000000 |
| 256 KiB JSON document | None | Decompress | 262,144 | 262,144 | 67.70 | 104.92 / 295.48 / 570.68 | 6,574.84 | 262,246.40 | 0.151 | 99.18 | 1.000000 |
| 4 MiB SHA-256 binary | LZ4 level 3 | Compress | 4,194,335 | 4,194,335 | 191,475.00 | 193,245.99 / 248,171.05 / 249,171.64 | 5.03 | 20,974,044.40 | 194.575 | 97.47 | 1.000007 |
| 4 MiB SHA-256 binary | LZ4 level 3 | Decompress | 4,194,335 | 4,194,304 | 10,270.10 | 5,247.85 / 6,764.71 / 10,853.52 | 170.79 | 4,194,795.20 | 5.525 | 95.70 | 1.000007 |
| 4 MiB SHA-256 binary | None | Compress | 4,194,304 | 4,194,304 | 1.00 | 0.09 / 0.11 / 0.36 | 1,129,943.50 | 0.00 | 0.001 | 125.00 | 1.000000 |
| 4 MiB SHA-256 binary | None | Decompress | 4,194,304 | 4,194,304 | 1,115.50 | 914.77 / 5,047.31 / 7,053.50 | 444.81 | 4,194,643.60 | 2.215 | 98.48 | 1.000000 |

The full generated JSON/JSONL and summaries remain at
`/tmp/terrabase-compression-20260914-p3rIuF/output/primitive/` and
`primitive-rss/`; command logs are under
`/tmp/terrabase-compression-20260914-p3rIuF/logs/`.

The first primitive receipt JSON SHA-256 is
`92402082f395daf02811a2920617512fd5ae42200cbc474d467c775bb8f1dbc6` and its
raw JSONL SHA-256 is
`2b0ff429b91a16290b71ecd02fc76f291adf4ed717eca41825103ed242255d65`.
The RSS invocation used the same artifact and configuration; its raw JSONL
SHA-256 is
`7af4cda60def164914e3767c4310926a83721b1c015269e8716962b0e6eda0a4`.

## Timing and sample unit

Each warm raw row has `operations=20` and `concurrency=1`. The harness starts
one worker and records one `Stopwatch` interval around each direct
`DataCompression` invocation in a 20-entry latency array. For each row,
p50/p95/p99 are order statistics over those twenty per-operation intervals;
the implementation uses `ceil(n * percentile) - 1`, so the indexes are 9, 18,
and 19 (the 10th, 19th, and 20th sorted observations), with no interpolation.
The receipt table then takes the median of the five row-level p50/p95/p99
values. It does not average batch timings into those percentile values.

Throughput is separately calculated as `20 / whole-sample elapsed seconds`.
That elapsed interval includes worker/task setup and scheduling around the
batch, while each percentile is the per-operation interval. Allocated bytes
are the process-wide `GC.GetTotalAllocatedBytes` delta divided by 20; CPU is
the process `TotalProcessorTime` delta divided by 20. Thus allocation and CPU
per operation include the latency array and worker/task harness overhead in
addition to the compressor's own work.

The 4 MiB incompressible LZ4 sample 2 illustrates the units: twenty operations
took `3,975.4292 ms`, giving `5.030903 ops/s`; the sorted-operation p50 was
`193,245.987 µs` (`193.246 ms`), while the arithmetic mean wall time was
`198.77146 ms/op`. The sample's allocation delta was `419,480,888 / 20 =
20,974,044.4 B/op`, and process CPU was `3,891.504 / 20 = 194.5752 ms/op`.
The high p50 is therefore correctly slow compression work per operation, not
a per-batch value.

The timed caller is compression-only or decompression-only. `Execute` calls
`DataCompression.Compress` or `DataCompression.Decompress` directly. The
one-time compressed output `ToArray()` used to seed decompression occurs after
the cold compression timer, and the cold round-trip equality check occurs
after the cold decompression timer. Warm compression does not include output
materialization, WAL, segment, filesystem, or backup framing. Warm
decompression does include the direct decoder's output allocation. No
round-trip or end-to-end speedup claim is made.

## Reopen and durability check

The existing native filesystem backup qualification was also run against the
same immutable Release artifact:

```text
/run/current-system/sw/bin/time -v nice -n 19 taskset -c 0 dotnet /tmp/terrabase-compression-20260914-p3rIuF/build/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload native-restore --samples 5 --operations 128 --log-entries 8 --seed 1337 --output /tmp/terrabase-compression-20260914-p3rIuF/output/native-restore
```

Exit: `0`; maximum RSS `74,152 KiB`; 85% aggregate CPU; 1.43 s wall time.
The run verified checksums over two artifacts and committed manifests, restored
sequence `2` after one PITR log entry, activated a new generation, and opened a
new `StorageEngine` instance. Its local fixture medians were restore `53.763`
ms, clean reopen `5.167` ms, and restore-plus-reopen `58.931` ms, with
restore-plus-reopen p95 `170.431` ms. This is a reopen/durability fixture and
does not measure compression.

In the source snapshot, `ZoneTreeStorageProvider` configures
`WriteAheadLogMode.Sync` (`src/TerraBase/Storage/ZoneTree/ZoneTreeStorageProvider.cs:70`),
`StorageEngine` opens `FileDurableLog` for `wal`
(`src/TerraBase/StorageEngine/StorageEngine.cs:117`), and the durable log has
no `DataCompression` call. Disk segment defaults remain Zstd level 0 with 4 MiB
blocks, while live-backup record batches default to Zstd level 0 with 1 MiB
blocks. No compressed WAL mode was enabled.

## Expanded qualification gap

The requested `None`/`LZ4 level 3`/native `Zstd level 0` matrix is **not
available in the checked-in harness**. The exact caller audit is
`/tmp/terrabase-compression-20260914-p3rIuF/logs/caller-audit.txt` and source
inspection shows:

- `CompressionWorkload` calls `DataCompression` directly and iterates only
  `[CompressionMethod.LZ4, CompressionMethod.None]`.
- `PayloadProfile.All` has only `small-document` (4 KiB JSON),
  `large-document` (256 KiB JSON), and `incompressible-block` (4 MiB binary).
  It has no compressible/incompressible pair at each size.
- `--storage storage-engine` is retained metadata; the primitive loop still
  runs in memory. There is no disk-segment selector, forced segment setup,
  per-file byte accounting, decompressed-block counters, or cold reopen read
  phase.
- There is no live-backup record-batch benchmark selector. The native restore
  runner above exercises `StorageEngine` backup/restore and reopen, not
  `LiveBackupRecordBatchWriter`/`Reader`.

Therefore native Zstd p50/p95/p99, Zstd allocations/CPU/RSS, six-profile
coverage, disk stored ratios, WAL/segment write amplification, live-backup
throughput, and live-backup reopen behavior are **missing**, not zero. Write
amplification is also missing for the measured primitive because no WAL,
segment, filesystem, or backup framing was involved. The single external RSS
value is process-wide for the complete primitive invocation, not a per-case
RSS delta.

## Coverage and next action

Covered: actual checked-in primitive caller, LZ4/None cold and warm compression
and decompression at 4 KiB/256 KiB/4 MiB, deterministic payloads, allocated
bytes, process CPU, external peak RSS, output ratios, Release build identity,
and a separate durable local restore/reopen check.

Gap: native Zstd and the real disk/live-backup callers remain unmeasured; no
production capacity, horizontal scaling, network, or SLO claim is supported.

Next action: add a reviewed benchmark-only selector and instrumentation for
the 3-method six-profile live-backup and forced-segment matrix, keeping
`WriteAheadLogMode.Sync`, then rerun from a new immutable source snapshot.
