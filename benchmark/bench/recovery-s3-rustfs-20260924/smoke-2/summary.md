# Local RustFS native S3 recovery drill

Held subject: `/tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase`; raw source manifest SHA-256 `684cabe6b8cff1865500e9eb2fd3b486fadb5f37d98125aef88629688738395f` (1641 verified files, 0 mismatches). Snapshot JSON declares `sourceFilesManifestSha256=557e52137b009e04408343e01cef86b94ab0bf6156d7d88d43ec18787aa211fb`; its encoding semantics were not documented, and this value differs from the raw file hash.
Runner DLL SHA-256 `c408a866c749ff9093b80d0cc7090bcc80736792c267c4a60fc37b90600133ea`; loaded TerraBase DLL SHA-256 `50b37d8becfbedd596205d72804c436d2194b2bc89188084b06b6f715329e51c`.
Provider: RustFS container `terrabase-rustfs-20260922`, image ID `1e81764caba3be454540c061eb50beeb7706b3c46ecec9b4243498b585760c1b`, loopback endpoint `http://127.0.0.1:19000`, isolated bucket `tb-recovery-39cc872843b24a63` (removed after the run). Credentials came from that local container and were not written to receipts.
Backup fixture: `NoBackupEncryption` with HMAC-SHA256 signed manifests; configured RPO 5 minutes and RTO 30 minutes. Local sample values are observations, not a production guarantee.
Runtime: .NET 11.0.0; NixOS 26.05 (Yarara); X64; CPU Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 visible logical processors; host MemTotal 32826780 KiB; cgroup cpu.max `max 100000`; memory.max `max`.
Fault: declare source loss after an unarchived durable write and 250 ms injected archival delay, then close the source engine and delete its directory. Backup objects live in RustFS over loopback; restore targets are local filesystems. RTO starts at fault declaration and ends after restore, clean reopen, exact document scan, schema registration, and authority import.

| Sample | Recoverable age at fault (RPO) | Lost write age at fault | Verified RTO | Restored frontier | Exact data | Schema and authority |
| ---: | ---: | ---: | ---: | ---: | --- | --- |
| 1 | 463.4 ms | 249.8 ms | 6217.3 ms | 36 | 32 documents; value 2; 1 lost write | fingerprint, checksum, import verified |
| 2 | 524.0 ms | 249.7 ms | 3937.8 ms | 36 | 32 documents; value 2; 1 lost write | fingerprint, checksum, import verified |
| p50 | 463.4 ms | — | 3937.8 ms | — | — | — |
| p95 | 524.0 ms | — | 6217.3 ms | — | — | — |

Backup chain per sample: full + 2 incrementals; manifest and artifact hashes, parent IDs, frontiers, source-at-fault frontier, and raw timestamps are in `samples.jsonl`. Restore plan and execution verify the signed manifest chain and artifact integrity. The source had one durable write after the tip, intentionally never archived; the restored value is 2 and lost value is 3. Schema fingerprint is rehydrated into a fresh registry. One automatic index decision authority checksum is verified and imported as evidence requiring local reprofile before activation.

- `corrupt-full-artifact`: restore refused with `TerraBase.Backup.BackupChecksumException`; target inactive: `True`; object key `backups/recovery-s3-8b3da00c8ab644058880207e2537f44c/bp-20260924052135601-f1c5b04bb38449d0a1c3a11ef847aab9/snapshot.bin`.
- `missing-tip-artifact`: restore refused with `System.IO.FileNotFoundException`; target inactive: `True`; object key `backups/recovery-s3-8e9fa0499b3147a9a91a85262fcbf5fe/bp-20260924052144681-ee98612b27f0414cb73e5a8dcd02fd04/incremental.log`.
- Standalone native-store multipart upload: a transient timeout was injected on part 2 after one 5242880 byte part was acknowledged. A fresh native S3 store resumed from the persisted checkpoint in 328.7 ms, verified SHA-256 `5996b2f794f909810967c4a94d861a96a7f822ab96fbea84836755ae2de4d84d`, and cleared the checkpoint. Raw gate: `upload-resume.json`.

These are single-host loopback RustFS measurements. The controlled fault includes clean engine disposal and source-directory removal; it does not model abrupt power loss, WAN/object-store outage, region failure, detection time, or human response. Percentiles are nearest-rank across the listed independent samples.
