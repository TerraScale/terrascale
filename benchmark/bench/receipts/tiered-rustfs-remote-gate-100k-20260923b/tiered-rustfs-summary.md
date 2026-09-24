# Tiered RustFS remote gate: failed diagnostic

- **Status:** failed diagnostic; this run does not qualify the 100K gate and must not be used to qualify the 100M run.
- **Run phase:** `seed-root-loss-recovery` (constructor recovery after the run-owned local root was removed).
- **Started:** `2026-09-23T13:49:17.4734239Z`
- **Finished:** `2026-09-23T13:49:56.30773Z`
- **Exit code:** `1`
- **RustFS endpoint:** `http://127.0.0.1:19000` (credentials are intentionally omitted).

## Exact command

```text
/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks --workload tiered-segment --records 100000 --batch 8192 --payload-bytes 1024 --payload-mode per-key-incompressible --updates 1000 --deletes 1000 --point-reads 32 --checkpoint-records 25000 --cache-bytes 67108864 --min-free-space-bytes 1073741824 --resource-sample-ms 500 --s3-endpoint http://127.0.0.1:19000 --s3-bucket terrabase-tiered --prefix tiered-rustfs-remote-gate-100k-20260923b --output bench/receipts/tiered-rustfs-remote-gate-100k-20260923b
```

## Failure

The workload seeded all `100000` records, then removed the run-owned local `engine` directory and attempted to construct the tiered `StorageEngine`. Recovery stopped with:

```text
System.IO.InvalidDataException: The tiered StorageEngine root has remote segment objects but no MANIFEST or INITIALIZED marker; recovery is refused.
   at TerraBase.StorageEngine.StorageEngine.ValidateAuthorityBeforeRecoveryUnsafe() in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageEngine.cs:line 8447
   at TerraBase.StorageEngine.StorageEngine..ctor(OpenedRoot opened, IStorageTierPlacement placement, StorageWalDurability walDurability, IStorageSegmentStore segmentStore, IRemoteCommitJournal remoteCommitJournal) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageEngine.cs:line 360
   at TerraBase.StorageEngine.StorageEngine..ctor(String path, IStorageTierPlacement placement, StorageWalDurability walDurability, IStorageSegmentStore segmentStore, IRemoteCommitJournal remoteCommitJournal) in /home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/StorageEngine/StorageEngine.cs:line 268
   at TerraBase.Benchmarks.TieredRustFsQualificationWorkload.RunAsync(String[] args) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/TieredRustFsQualificationWorkload.cs:line 219
   at TerraBase.Benchmarks.TieredRustFsQualificationWorkload.RunAsync(String[] args) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/TieredRustFsQualificationWorkload.cs:line 523
   at TerraBase.Benchmarks.TieredRustFsQualificationWorkload.RunAsync(String[] args) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/TieredRustFsQualificationWorkload.cs:line 523
```

This is the expected refusal path when the local root has remote segments but no local authority markers. The later benchmark wiring must rebuild the WAL from the remote commit journal before constructing the engine.

## Subject and binaries

From `run-receipt.json`:

- Subject commit: `67e35f443565c54eb731d6ea2dd989cb5c87b7be`
- Subject tree digest: `69203400f13c7e311cf5fb569a081eeab36e8cbc`
- Dirty working tree: `true`
- Subject working-tree digest: `5dee0c7e56628161135e54bb8af51af9adbc4c62cee82fde39d2612c9da94dd0`
- Benchmark assembly used by the failed run SHA256: `4b941b2323c436ef40a7c949feb4157c453a629a21ade018b3b5c16f93594a9e`
- Subsequent Release benchmark assembly SHA256: `8bab3935ac828727a47c3e8cba1626745b4606df3635cbea75659e775e4dab1a`
- Current verification build benchmark assembly SHA256: `3b25c0a67db55452767dabbeb65b4efb66e3685d7f2d998760f69652395d021b`
- Subsequent Release TerraBase assembly SHA256: `44a34019e8d4b09df4f5267e2f728b30cdca3160860f648794d37c9be2454f03`

The failed run's complete receipts remain under:

`/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/bench/receipts/tiered-rustfs-remote-gate-100k-20260923b/`

## Resource evidence

- Peak local storage: `379,588,931` bytes
- Peak RSS: `556,490,752` bytes
- Minimum observed free space: `370,551,357,440` bytes
- Minimum free-space limit: `1,073,741,824` bytes
- Seed completion: `100,000` records
- Resource samples: `86`
- Resource envelope: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/bench/receipts/tiered-rustfs-remote-gate-100k-20260923b/resource-envelope.json`

Only the run-owned `engine` directory was removed and recreated during the root-loss check. No outside output directory was deleted.
