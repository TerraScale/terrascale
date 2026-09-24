# TerraBase native backup qualification

Status: **invalid**

## Measurement

- Source commit: `57d12ed6ae6db472a26318a9b2671b3aa08e26b0`; dirty `True`
- Benchmark artifact SHA-256: `9fae1d4aa9082031d22a6c752323896963dc5bcb768245f03e23d967b59af2ec`
- TerraBase artifact SHA-256: `8c5c4ca4a41e3f33c11afa2313b8ebe889116dcfbf7363c84b6ab6897e262993`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20,937,965,568 bytes
- GC: server `True`; latency mode `Interactive`
Error: `System.IO.InvalidDataException: Backup artifact checksum mismatch: backups/native-qualification/bp-20260922165300821-58e835116c294bc9b94e2284139c4863/snapshot.bin
   at TerraBase.Benchmarks.NativeBackupQualificationWorkload.VerifyChecksumsAsync(IBackupObjectStore store, BackupManifest full, BackupManifest incremental) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/NativeBackupQualificationWorkload.cs:line 313
   at TerraBase.Benchmarks.NativeBackupQualificationWorkload.RunQualificationAsync(NativeOptions options, String outputDirectory) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/NativeBackupQualificationWorkload.cs:line 198
   at TerraBase.Benchmarks.NativeBackupQualificationWorkload.RunQualificationAsync(NativeOptions options, String outputDirectory) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/NativeBackupQualificationWorkload.cs:line 217
   at TerraBase.Benchmarks.NativeBackupQualificationWorkload.RunAsync(String[] args) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/NativeBackupQualificationWorkload.cs:line 73`

## Limits

- Object store: `unavailable`. This qualification measures the selected provider only; it does not qualify multi-region recovery or production network durability.
- The PITR cut replays the first incremental publication and verifies a new StorageEngine instance after activation. It does not simulate power loss during upload, manifest commit, generation materialization, or pointer replacement.
- Clean reopen is a new StorageEngine instance in the benchmark process. It is evidence of durable files and activation metadata, not a host reboot or process-crash drill.
- The observed local end-to-end time is a qualification fixture result; no production RTO/SLO is inferred from this receipt.

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload native-backup --store s3 --s3-endpoint http://127.0.0.1:19000 --s3-bucket terrabase-native-20260922 --s3-access-key [redacted] --s3-secret-key [redacted] --samples 3 --operations 256 --log-entries 4 --output ../bench/rustfs-native-20260922T1655Z`
