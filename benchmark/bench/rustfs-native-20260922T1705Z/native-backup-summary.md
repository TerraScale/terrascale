# TerraBase native backup qualification

Status: **invalid**

## Measurement

- Source commit: `57d12ed6ae6db472a26318a9b2671b3aa08e26b0`; dirty `True`
- Benchmark artifact SHA-256: `77942c8de1347abfe034f00fe86f86e9a1fa4b1d122134b59f95d16f79cfceec`
- TerraBase artifact SHA-256: `6825d0074b40ac7e308fe3da814987fe1776b32a0d3cb21cc18f2e60bd23cd98`
- Runtime: .NET 11.0.0-rc.1.26425.128; build `Release`; NixOS 26.05 (Yarara); X64
- CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 8 logical processors; available memory 20,937,965,568 bytes
- GC: server `True`; latency mode `Interactive`
Error: `System.IO.InvalidDataException: Expected two committed manifests, got 4.
   at TerraBase.Benchmarks.NativeBackupQualificationWorkload.RunQualificationAsync(NativeOptions options, String outputDirectory) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/NativeBackupQualificationWorkload.cs:line 197
   at TerraBase.Benchmarks.NativeBackupQualificationWorkload.RunQualificationAsync(NativeOptions options, String outputDirectory) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/NativeBackupQualificationWorkload.cs:line 217
   at TerraBase.Benchmarks.NativeBackupQualificationWorkload.RunAsync(String[] args) in /home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/NativeBackupQualificationWorkload.cs:line 73`

## Limits

- Object store: `unavailable`. This qualification measures the selected provider only; it does not qualify multi-region recovery or production network durability.
- The PITR cut replays the first incremental publication and verifies a new StorageEngine instance after activation. It does not simulate power loss during upload, manifest commit, generation materialization, or pointer replacement.
- Clean reopen is a new StorageEngine instance in the benchmark process. It is evidence of durable files and activation metadata, not a host reboot or process-crash drill.
- The observed local end-to-end time is a qualification fixture result; no production RTO/SLO is inferred from this receipt.

Command: `/home/mariogk/Projects/TerraScale/ts-benchmark/terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll --workload native-backup --store s3 --s3-endpoint http://127.0.0.1:19000 --s3-bucket terrabase-native-20260922 --s3-access-key [redacted] --s3-secret-key [redacted] --samples 3 --operations 256 --log-entries 4 --output ../bench/rustfs-native-20260922T1705Z`
