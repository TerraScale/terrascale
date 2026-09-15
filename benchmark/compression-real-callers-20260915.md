# Real compression caller measurements

This report records the retained 2026-09-15 real-caller receipts for the
forced disk-segment path and the live-backup record-batch path. Both receipts
are `complete` with 18/18 cases, five configured warm samples, zero warmup
operations, one operation per sample, concurrency 1, and seed 1337:

| Caller | Receipt | Raw samples | Artifact identity |
| --- | --- | --- | --- |
| `forced-disk-segment` | [`compression-real-receipt.json`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/benchmark-disk-output/compression-real-receipt.json) | [`compression-real-raw-samples.jsonl`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/benchmark-disk-output/compression-real-raw-samples.jsonl) | runner `c52af7495a4211a4793f96d73f566b93019e421005f047b7e9cea2285a240e03`; ZoneTree `de0367e9eb2dc4048dd2987a15ceda66ff7acffacfbb8b888ea3e75e5b8dfd65` |
| `live-backup-record-batch` | [`compression-real-receipt.json`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/benchmark-live-backup-output/compression-real-receipt.json) | [`compression-real-raw-samples.jsonl`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/benchmark-live-backup-output/compression-real-raw-samples.jsonl) | runner `c52af7495a4211a4793f96d73f566b93019e421005f047b7e9cea2285a240e03`; ZoneTree `de0367e9eb2dc4048dd2987a15ceda66ff7acffacfbb8b888ea3e75e5b8dfd65` |

The receipts record `gitCommit: null` and no source revision. The recorded
`gitDirty: false` value does not supply a source identity, so no commit is
inferred. The artifact and source-file identities are retained exactly as
reported by the evidence bundle.

Both callers use `None` as `DataCompression` passthrough, LZ4 level 3 with a
declared content size plus content and block checksums, and Zstandard level 0
with the native default options. The disk caller uses a direct ZoneTree with a
controlled mutable threshold, 4 MiB blocks, and synchronous WAL. The live
backup caller uses the record-batch writer and reader with 1 MiB blocks.

The phase cells below use this format:
`ops/s; p50/p95/p99 µs; allocated B/op; process CPU ms/op / process CPU %`.
`ElapsedMilliseconds` and `ThroughputOpsPerSecond` are the phase elapsed time
and operations divided by that elapsed time; the latency values are in
microseconds. For warm samples, the phase timer starts before the worker task
is launched, while each latency timer starts inside the worker around the
operation. With one operation per sample, worker scheduling and join overhead
can therefore make `ops/s` non-reciprocal with the reported latency. Warm cells
are component-wise medians across the five retained warm samples, so the
reported throughput and latency medians are not inverted from one another.
Setup, reopen, and cold cells are the single observations recorded by each
case; their one-operation p50, p95, and p99 values are the same elapsed
observation. Per-case RSS is absent from both receipts and is shown as `—`.

The receipt profile names describe the generated value size and intended
per-value shape. The harness calls `CreatePayload` once per case and reuses
that value for every disk upsert ([`CompressionCallerWorkload.cs`](TerraBase.Benchmarks/CompressionCallerWorkload.cs:190)); the live-backup case copies the same value bytes into every record ([`CompressionCallerWorkload.cs`](TerraBase.Benchmarks/CompressionCallerWorkload.cs:755)). Thus `4KiB-incompressible` means a 4 KiB generated value, not an incompressible
8.45/8.49 MB record batch: its 2,050 records contain the same 4 KiB value with
distinct deterministic keys. The 256 KiB and 4 MiB profiles repeat their value
in the same way at the record counts shown below. The small compressed sizes
for the 4 KiB caller rows are therefore aggregate repetition results and do not
qualify an incompressible batch; specifically, the retained LZ4 rows are
82,580 file bytes for disk and 87,325 stored bytes for live backup.

`Cold` is also a harness phase label, not an OS-cache state. For disk, cold
reopen opens files created by the same case and cold read performs the first
measured lookup after that reopen; the run does not flush or evict the OS page
cache. The subsequent warm reads use the same open tree and key sequence. For
live backup, cold write and cold read operate on in-process `MemoryStream` and
`byte[]` data, with no filesystem or network cache boundary; warm reads reuse
the same stored byte array and warm writes recreate an in-memory batch from the
same records.

The warm timer mismatch is a concrete harness correction handoff: the disk and
live-backup warm methods start the phase stopwatch before launching `Task.Run`
workers, while each operation latency starts after a worker begins
([`CompressionCallerWorkload.cs`](TerraBase.Benchmarks/CompressionCallerWorkload.cs:421), [`CompressionCallerWorkload.cs`](TerraBase.Benchmarks/CompressionCallerWorkload.cs:530)). A future harness revision should synchronize worker start with the measured window or explicitly expose scheduling-inclusive throughput beside operation-local latency, then rerun the receipt. This documentation preserves the retained values and does not modify the active harness.

## Forced disk-segment caller

`File bytes` is the sum of the receipt's `data.z`, `head.z`, `sparse`, `multi`,
`wal`, and `metadata` `FileInfo.Length` values. It is a file-byte observation,
not device-sector allocation. The setup phase is retained because this caller
creates the disk segment during setup; the read phases are the caller's direct
read observations.

| Profile | Method | Logical input B | File bytes B | Setup | Cold reopen | Cold read | Warm read (median) | RSS |
| --- | --- | ---: | ---: | --- | --- | --- | --- | --- |
| 4KiB-compressible | None | 8456250 | 8540384 | 0.65; 1535356.30/1535356.30/1535356.30; 21673168; 576.234/0.82 | 17.39; 57515.47/57515.47/57515.47; 190760; 54.107/2.05 | 196.92; 5078.10/5078.10/5078.10; 0; 4.415/1.89 | 17667.84; 19.42/19.42/19.42; 0; 0.143/4.91 | — |
| 4KiB-compressible | LZ4 | 8456250 | 66667 | 0.70; 1421049.10/1421049.10/1421049.10; 30086560; 412.348/0.63 | 115.59; 8661.91/8661.91/8661.91; 145240; 6.535/1.64 | 30395.14; 32.90/32.90/32.90; 0; 0.036/2.38 | 17605.63; 24.89/24.89/24.89; 8200; 0.137/5.32 | — |
| 4KiB-compressible | Zstd | 8456250 | 19333 | 0.79; 1259337.50/1259337.50/1259337.50; 30037640; 396.786/0.68 | 191.27; 5235.63/5235.63/5235.63; 141952; 3.324/1.38 | 31746.03; 31.50/31.50/31.50; 0; 0.034/2.35 | 22471.91; 6.96/6.96/6.96; 0; 0.119/4.48 | — |
| 4KiB-incompressible | None | 8456250 | 8540384 | 0.80; 1249040.40/1249040.40/1249040.40; 21476624; 411.896/0.72 | 268.86; 3725.22/3725.22/3725.22; 180656; 1.406/0.82 | 55865.92; 17.90/17.90/17.90; 0; 0.020/2.43 | 26595.74; 5.12/5.12/5.12; 0; 0.093/5.38 | — |
| 4KiB-incompressible | LZ4 | 8456250 | 82580 | 0.68; 1460242.10/1460242.10/1460242.10; 30101064; 431.320/0.64 | 151.23; 6619.25/6619.25/6619.25; 140576; 4.752/1.56 | 80645.16; 12.40/12.40/12.40; 0; 0.016/2.81 | 30769.23; 6.31/6.31/6.31; 0; 0.076/5.27 | — |
| 4KiB-incompressible | Zstd | 8456250 | 45475 | 0.77; 1295031.40/1295031.40/1295031.40; 30075480; 293.012/0.49 | 264.32; 3789.52/3789.52/3789.52; 156080; 1.542/0.89 | 62111.80; 16.10/16.10/16.10; 0; 0.018/2.43 | 30769.23; 4.88/4.88/4.88; 0; 0.091/5.38 | — |
| 256KiB-compressible | None | 8389536 | 8916716 | 16.80; 59533.60/59533.60/59533.60; 21078672; 23.958/0.87 | 240.27; 4167.72/4167.72/4167.72; 2202120; 2.585/1.35 | 23696.68; 42.20/42.20/42.20; 0; 0.045/2.32 | 14577.26; 22.61/22.61/22.61; 0; 0.165/4.68 | — |
| 256KiB-compressible | LZ4 | 8389536 | 40666 | 13.66; 73186.40/73186.40/73186.40; 30549744; 44.854/1.33 | 238.37; 4199.90/4199.90/4199.90; 1161720; 2.762/1.43 | 13869.63; 72.10/72.10/72.10; 0; 0.046/1.39 | 20161.29; 28.50/28.50/28.50; 0; 0.116/4.68 | — |
| 256KiB-compressible | Zstd | 8389536 | 3451 | 19.26; 51916.00/51916.00/51916.00; 30517488; 73.415/3.07 | 366.13; 2736.13/2736.13/2736.13; 1154248; 2.982/2.37 | 31152.65; 32.10/32.10/32.10; 0; 0.035/2.37 | 25125.63; 28.16/28.16/28.16; 0; 0.085/4.68 | — |
| 256KiB-incompressible | None | 8389536 | 8916716 | 19.75; 50623.90/50623.90/50623.90; 21072104; 22.493/0.97 | 349.03; 2870.28/2870.28/2870.28; 1155416; 1.540/1.17 | 36764.71; 27.20/27.20/27.20; 0; 0.029/2.32 | 20746.89; 20.93/20.93/20.93; 336; 0.094/4.85 | — |
| 256KiB-incompressible | LZ4 | 8389536 | 8916233 | 2.29; 437187.00/437187.00/437187.00; 39432872; 739.586/3.68 | 297.47; 3366.24/3366.24/3366.24; 2204424; 5.100/3.30 | 24271.84; 41.20/41.20/41.20; 0; 0.044/2.32 | 29325.51; 18.84/18.84/18.84; 0; 0.080/4.67 | — |
| 256KiB-incompressible | Zstd | 8389536 | 799491 | 17.01; 58790.30/58790.30/58790.30; 31310568; 84.450/3.12 | 267.61; 3743.25/3743.25/3743.25; 1680376; 6.242/3.63 | 35971.22; 27.80/27.80/27.80; 0; 0.031/2.42 | 30211.48; 18.69/18.69/18.69; 0; 0.080/5.18 | — |
| 4MiB-compressible | None | 33554664 | 41945524 | 9.11; 109809.00/109809.00/109809.00; 54631488; 107.372/2.13 | 101.40; 9870.77/9870.77/9870.77; 25269648; 16.405/3.62 | 1188.21; 841.60/841.60/841.60; 0; 1.759/4.54 | 3081.66; 305.82/305.82/305.82; 0; 0.500/2.89 | — |
| 4MiB-compressible | LZ4 | 33554664 | 168529 | 9.29; 107651.10/107651.10/107651.10; 63187600; 89.646/1.81 | 35.50; 28179.81/28179.81/28179.81; 25276624; 34.139/2.63 | 1038.64; 962.80/962.80/962.80; 0; 0.961/2.17 | 3151.59; 302.27/302.27/302.27; 0; 0.441/3.17 | — |
| 4MiB-compressible | Zstd | 33554664 | 7304 | 9.26; 108042.70/108042.70/108042.70; 63026560; 90.503/1.82 | 43.23; 23138.75/23138.75/23138.75; 25276760; 28.858/2.71 | 1179.52; 847.80/847.80/847.80; 0; 0.842/2.16 | 3302.51; 271.96/271.96/271.96; 0; 0.410/2.84 | — |
| 4MiB-incompressible | None | 33554664 | 41945524 | 11.33; 88287.70/88287.70/88287.70; 54634240; 90.753/2.23 | 33.54; 29825.66/29825.66/29825.66; 29469984; 56.193/4.10 | 992.36; 1007.70/1007.70/1007.70; 0; 1.989/4.29 | 2859.59; 323.66/323.66/323.66; 0; 0.441/2.71 | — |
| 4MiB-incompressible | LZ4 | 33554664 | 41945771 | 0.53; 1895390.10/1895390.10/1895390.10; 104964672; 1830.814/2.10 | 49.66; 20145.90/20145.90/20145.90; 33672096; 22.063/2.38 | 1192.04; 838.90/838.90/838.90; 0; 0.835/2.16 | 3653.64; 263.04/263.04/263.04; 368; 0.383/3.01 | — |
| 4MiB-incompressible | Zstd | 33554664 | 41946449 | 8.14; 122923.70/122923.70/122923.70; 104971656; 87.370/1.55 | 100.47; 9962.20/9962.20/9962.20; 25275984; 17.183/3.75 | 1141.81; 875.80/875.80/875.80; 0; 1.827/4.53 | 3293.81; 288.31/288.31/288.31; 0; 0.396/2.82 | — |

## Live-backup record-batch caller

`Stored bytes` is the receipt's serialized record-batch output length. The
live-backup receipt has no file-byte or device-byte field, so it cannot provide
a physical-byte measurement. The input is the serialized record-batch input;
the 4 KiB profile has 2,050 records, the 256 KiB profile 32 records, and the
4 MiB profile 8 records.

| Profile | Method | Input B | Stored B | Cold write | Cold read | Warm write (median) | Warm read (median) | RSS |
| --- | --- | ---: | ---: | --- | --- | --- | --- | --- |
| 4KiB-compressible | None | 8491100 | 8491100 | 30.75; 32515.10/32515.10/32515.10; 34194200; 33.173/2.22 | 68.92; 14509.30/14509.30/14509.30; 8689416; 14.457/2.17 | 36.20; 27541.62/27541.62/27541.62; 34193768; 27.294/2.43 | 126.71; 7832.81/7832.81/7832.81; 8694704; 6.614/2.19 | — |
| 4KiB-compressible | LZ4 | 8491100 | 51494 | 23.22; 43074.60/43074.60/43074.60; 14282312; 42.257/2.13 | 29.81; 33546.90/33546.90/33546.90; 25742656; 34.833/2.26 | 28.66; 34813.71/34813.71/34813.71; 12052288; 36.960/2.16 | 67.22; 14822.44/14822.44/14822.44; 25737128; 16.855/2.44 | — |
| 4KiB-compressible | Zstd | 8491100 | 11800 | 54.00; 18519.10/18519.10/18519.10; 11972296; 19.915/2.34 | 51.04; 19593.50/19593.50/19593.50; 25689416; 21.831/2.42 | 84.32; 11804.71/11804.71/11804.71; 11956576; 13.614/2.43 | 72.45; 13733.18/13733.18/13733.18; 25696328; 16.184/2.55 | — |
| 4KiB-incompressible | None | 8491100 | 8491100 | 51.16; 19547.70/19547.70/19547.70; 34212904; 23.354/2.60 | 88.71; 11272.40/11272.40/11272.40; 8679056; 21.942/4.23 | 43.38; 22979.66/22979.66/22979.66; 34192288; 23.783/2.17 | 159.63; 6200.10/6200.10/6200.10; 8696576; 10.430/3.24 | — |
| 4KiB-incompressible | LZ4 | 8491100 | 87325 | 22.83; 43801.40/43801.40/43801.40; 12313312; 43.436/2.16 | 51.79; 19306.90/19306.90/19306.90; 25767864; 21.317/2.40 | 27.46; 36346.28/36346.28/36346.28; 12318008; 58.043/3.10 | 73.14; 13602.18/13602.18/13602.18; 25771800; 20.574/3.32 | — |
| 4KiB-incompressible | Zstd | 8491100 | 56932 | 97.29; 10278.30/10278.30/10278.30; 12168712; 11.271/2.38 | 46.44; 21535.20/21535.20/21535.20; 25735856; 22.871/2.31 | 107.46; 9245.95/9245.95/9245.95; 12160000; 11.038/2.52 | 122.11; 8123.20/8123.20/8123.20; 25736264; 10.271/2.51 | — |
| 256KiB-compressible | None | 8390080 | 8390080 | 204.95; 4879.30/4879.30/4879.30; 16528080; 5.486/2.44 | 228.82; 4370.30/4370.30/4370.30; 8399032; 4.921/2.45 | 156.07; 6365.31/6365.31/6365.31; 16526240; 6.415/2.22 | 213.06; 4642.85/4642.85/4642.85; 8396600; 4.484/2.22 | — |
| 256KiB-compressible | LZ4 | 8390080 | 36939 | 37.67; 26546.20/26546.20/26546.20; 11724064; 26.214/2.15 | 54.63; 18305.20/18305.20/18305.20; 25235232; 37.528/4.46 | 37.14; 26871.37/26871.37/26871.37; 11723744; 40.896/3.46 | 72.09; 13822.38/13822.38/13822.38; 25223280; 27.980/4.37 | — |
| 256KiB-compressible | Zstd | 8390080 | 2135 | 161.16; 6205.20/6205.20/6205.20; 11558440; 13.501/4.73 | 59.63; 16770.20/16770.20/16770.20; 25178120; 33.008/4.28 | 108.15; 9185.35/9185.35/9185.35; 11555016; 17.748/4.17 | 111.75; 8894.59/8894.59/8894.59; 25184832; 18.190/4.46 | — |
| 256KiB-incompressible | None | 8390080 | 8390080 | 211.15; 4736.00/4736.00/4736.00; 16513752; 9.850/4.52 | 255.87; 3908.30/3908.30/3908.30; 8391944; 7.850/4.37 | 141.02; 7027.10/7027.10/7027.10; 16523392; 15.814/4.31 | 252.53; 3934.46/3934.46/3934.46; 8394960; 8.025/2.69 | — |
| 256KiB-incompressible | LZ4 | 8390080 | 8390392 | 2.58; 387781.90/387781.90/387781.90; 35665392; 390.482/2.19 | 39.79; 25134.60/25134.60/25134.60; 33565704; 24.650/2.13 | 2.67; 374826.66/374826.66/374826.66; 35664848; 373.386/2.15 | 47.02; 21159.66/21159.66/21159.66; 33568040; 31.821/2.42 | — |
| 256KiB-incompressible | Zstd | 8390080 | 2103684 | 86.47; 11564.60/11564.60/11564.60; 21788712; 17.704/3.33 | 70.24; 14237.20/14237.20/14237.20; 27280344; 29.404/4.49 | 93.78; 10617.77/10617.77/10617.77; 21798032; 14.214/2.82 | 82.19; 12104.16/12104.16/12104.16; 27280920; 20.216/2.78 | — |
| 4MiB-compressible | None | 33554800 | 33554800 | 20.88; 47887.20/47887.20/47887.20; 62917192; 48.232/2.19 | 27.79; 35988.20/35988.20/35988.20; 33556224; 35.495/2.14 | 18.09; 55232.33/55232.33/55232.33; 62917816; 52.981/2.16 | 33.66; 29618.71/29618.71/29618.71; 33556792; 30.194/2.17 | — |
| 4MiB-compressible | LZ4 | 33554800 | 133278 | 7.49; 133482.70/133482.70/133482.70; 47843264; 130.737/2.13 | 14.38; 69555.80/69555.80/69555.80; 100799904; 69.143/2.16 | 8.33; 120042.57/120042.57/120042.57; 39450984; 121.421/2.11 | 17.56; 56921.06/56921.06/56921.06; 100803616; 64.147/2.35 | — |
| 4MiB-compressible | Zstd | 33554800 | 4287 | 40.45; 24719.60/24719.60/24719.60; 38814568; 34.165/3.00 | 11.07; 90361.30/90361.30/90361.30; 100672840; 85.388/2.05 | 24.41; 40922.63/40922.63/40922.63; 38814384; 39.677/2.15 | 12.10; 82621.71/82621.71/82621.71; 100673912; 97.520/2.44 | — |
| 4MiB-incompressible | None | 33554800 | 33554800 | 26.73; 37407.00/37407.00/37407.00; 62899264; 38.227/2.22 | 68.82; 14529.90/14529.90/14529.90; 33562288; 15.066/2.25 | 40.99; 24358.28/24358.28/24358.28; 62917312; 29.412/2.51 | 70.99; 14060.23/14060.23/14060.23; 33556360; 13.810/2.17 | — |
| 4MiB-incompressible | LZ4 | 33554800 | 33555176 | 0.65; 1530864.40/1530864.40/1530864.40; 135270040; 1505.539/2.14 | 8.12; 123156.40/123156.40/123156.40; 134224464; 124.666/2.20 | 0.66; 1525699.22/1525699.22/1525699.22; 135272064; 1504.277/2.15 | 8.54; 117057.95/117057.95/117057.95; 134224912; 118.104/2.32 | — |
| 4MiB-incompressible | Zstd | 33554800 | 33555736 | 8.26; 121077.80/121077.80/121077.80; 135271928; 96.666/1.74 | 12.55; 79672.50/79672.50/79672.50; 134222560; 79.667/2.17 | 10.33; 96769.60/96769.60/96769.60; 135273984; 102.620/2.15 | 14.24; 70181.37/70181.37/70181.37; 134223168; 77.505/2.08 | — |

## Method and evidence boundary

The evidence bundle records these source-file hashes in
[`compression-source-identities.tsv`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/compression-source-identities.tsv);
the accompanying assembly and test-artifact hashes are in
[`compression-assembly.sha256`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/compression-assembly.sha256):

| File | SHA-256 |
| --- | --- |
| [`LZ4DataCompression.cs`](../src/TerraBase.ZoneTree/Compression/LZ4DataCompression.cs) | `6f4779726aabe85d90782098fe86dd7b4af327b8ffb25364f93d13b7f2f254f4` |
| [`CompressionCallerWorkload.cs`](TerraBase.Benchmarks/CompressionCallerWorkload.cs) | `013002f2ee27b87320ec3c32cc64241554f1862620471e57734a7270d551bf3e` |
| [`CompressionIntegrityTests.cs`](../tests/TerraBase.ZoneTree.Tests/CompressionIntegrityTests.cs) | `cbc54eb83aa206e5b88166051b700f3f14861a98a957e93a7999cd4217e081bc` |

The two 18-case tables are new real-caller measurements from
`CompressionCallerWorkload`; they are separate from the unchanged 14
compression-adapter integrity tests. The retained test evidence reports 14/14
passed in [`compression-integrity-tests.log`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/compression-integrity-tests.log).
Those adapter tests validate framing, checksums, lengths, trailing data, and
bounded declarations; they do not provide caller throughput, allocation, CPU,
RSS, or file-byte measurements.

The caller receipts are local component evidence from the retained Release
artifact. They do not establish TerraBase provider throughput, sustained
service capacity, device-sector allocation, network transfer cost, backup RTO,
or any production claim. Host available memory was recorded as 20,937,965,568
bytes, but no per-case RSS field was emitted, so it is not substituted into the
tables. Historical compression receipts and the existing adapter qualification
documents remain unchanged.

Evidence status files report `build_exit=0`, `exit=0` for both caller runs, and
the 14 adapter tests passed. The retained logs are
[`benchmark-build.log`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/benchmark-build.log),
[`benchmark-disk-run.log`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/benchmark-disk-run.log),
[`benchmark-live-backup-run.log`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/benchmark-live-backup-run.log),
and [`compression-integrity-tests.log`](../../.codex/evidence/terrabase-next9-coherent-integration-20260915-v1/logs/fork-compression-verifier/compression-integrity-tests.log).
