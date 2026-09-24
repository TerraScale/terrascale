# Native filesystem backup qualification

This qualification measures the native `StorageEngineBackupSource` and
`StorageEngineRestoreTarget` path with a durable local filesystem object store.
It creates one full snapshot, appends an incremental publication log, verifies
the serialized manifest and artifact checksums, restores to a PITR timestamp,
activates a new generation, and opens that generation with a fresh
`StorageEngine` instance.

Run the Release benchmark with the repository's lowest available CPU priority:

```text
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj --configuration Release
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload native-restore --samples 5 --operations 128 --log-entries 8 \
  --seed 1337 --output bench/receipts/<utc-run>
```

The retained 2026-09-14 receipt is
[`native-backup-receipt.json`](receipts/native-backup-20260914/native-backup-receipt.json),
with raw samples, local object-store artifacts, target generations, and the
rendered summary beside it in
[`native-backup-20260914`](receipts/native-backup-20260914/).

The retained JSON is a correctness fixture only until it is rerun with the
current Release benchmark. Its `environment` records runtime, OS,
architecture, processor count, memory, and GC mode, but omits the required
commit/dirty state, benchmark artifact path/digest, and CPU model. The current
workload emits those fields; a new source-bound receipt is required before
admitting the measurements as a complete benchmark claim.

The 2026-09-18 rerun is retained at
[`native-backup-20260918`](receipts/native-backup-20260918/native-backup-receipt.json)
with current fields (source commit, artifact digests, CPU model); all five
samples restored, activated, and reopened (restore p50 159.6 ms). That rerun
required the durable journal identity initialization fix described in
[`e2e-volume-100k-20260918.md`](e2e-volume-100k-20260918.md); the workload had
been failing on every fresh engine since the identity requirement landed.

The run used .NET 10.0.12 on NixOS 26.05 x64 with 46 logical processors and
128 snapshot documents. The full boundary was sequence 1 and the incremental
boundary was sequence 9. PITR stopped at sequence 2 after one incremental log
entry. All five target generations activated and reopened successfully.

| Measurement | p50 | p95 |
| --- | ---: | ---: |
| Restore and native activation | 68.820 ms | 180.010 ms |
| Fresh `StorageEngine` reopen | 6.762 ms | — |
| Restore, activation, and reopen | 76.270 ms | 189.350 ms |

The 2026-09-19 rerun with the v2 line-delimited snapshot artifacts is retained
at [`native-backup-20260919`](receipts/native-backup-20260919/native-backup-receipt.json)
(source commit `98a083df`, artifact digests recorded, 8 logical processors
pinned for server GC). The full boundary was sequence 2, the incremental
boundary sequence 10, and PITR stopped at sequence 3 after one log entry; all
five generations activated and reopened.

| Measurement | p50 | p95 |
| --- | ---: | ---: |
| Restore and native activation | 134.256 ms | 378.513 ms |
| Fresh `StorageEngine` reopen | 74.433 ms | — |
| Restore, activation, and reopen | 207.421 ms | 452.559 ms |

These are local fixture observations, not a production RTO or SLO. The object
store and target are local filesystem directories on one host; the receipt does
not qualify S3/OCI/Tigris network transfer, remote durability, multi-region
recovery, or provider latency. The clean reopen creates a new engine instance
in the benchmark process and does not simulate a process crash, host reboot, or
power loss during upload, manifest commit, generation materialization, or
active-pointer replacement.
