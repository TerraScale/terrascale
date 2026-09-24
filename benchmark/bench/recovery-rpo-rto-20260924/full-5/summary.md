# Local backup and restore RPO/RTO drill

Held subject: `/tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase`; raw source manifest SHA-256 `684cabe6b8cff1865500e9eb2fd3b486fadb5f37d98125aef88629688738395f` (1641 verified files, 0 mismatches). Snapshot JSON declares `sourceFilesManifestSha256=557e52137b009e04408343e01cef86b94ab0bf6156d7d88d43ec18787aa211fb`; its encoding semantics were not documented, and this value differs from the raw file hash.
Runner DLL SHA-256 `286fb272f6477dc4e8798a394bc972b5274930ba9c1a5e89055e681e1f9ca926`; loaded TerraBase DLL SHA-256 `28c5f8589845d24135fa908dadd5923aa6afd77d2690a2ba26bd0d8810e2ed13`.
Runtime: .NET 11.0.0; NixOS 26.05 (Yarara); X64; CPU Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 visible logical processors; host MemTotal 32826780 KiB; cgroup cpu.max `max 100000`; memory.max `max`.
Fault: declare source loss after an unarchived durable write and 250 ms injected archival delay, then close the source engine and delete its directory. Object store and restore target are local filesystems. RTO starts at fault declaration and ends after restore, clean reopen, exact document scan, schema registration, and authority import.

| Sample | Recoverable age at fault (RPO) | Lost write age at fault | Verified RTO | Restored frontier | Exact data | Schema and authority |
| ---: | ---: | ---: | ---: | ---: | --- | --- |
| 1 | 284.8 ms | 250.4 ms | 4318.9 ms | 36 | 32 documents; value 2; 1 lost write | fingerprint, checksum, import verified |
| 2 | 296.1 ms | 250.6 ms | 4617.3 ms | 36 | 32 documents; value 2; 1 lost write | fingerprint, checksum, import verified |
| 3 | 281.5 ms | 249.3 ms | 4014.9 ms | 36 | 32 documents; value 2; 1 lost write | fingerprint, checksum, import verified |
| 4 | 322.3 ms | 250.7 ms | 4877.9 ms | 36 | 32 documents; value 2; 1 lost write | fingerprint, checksum, import verified |
| 5 | 358.5 ms | 252.8 ms | 3972.8 ms | 36 | 32 documents; value 2; 1 lost write | fingerprint, checksum, import verified |
| p50 | 296.1 ms | — | 4318.9 ms | — | — | — |
| p95 | 358.5 ms | — | 4877.9 ms | — | — | — |

Backup chain per sample: full + 2 incrementals; manifest and artifact hashes, parent IDs, frontiers, source-at-fault frontier, and raw timestamps are in `samples.jsonl`. Restore plan and execution verify the signed manifest chain and artifact integrity. The source had one durable write after the tip, intentionally never archived; the restored value is 2 and lost value is 3. Schema fingerprint is rehydrated into a fresh registry. One automatic index decision authority checksum is verified and imported as evidence requiring local reprofile before activation.

These are single-host local filesystem measurements. The controlled fault includes clean engine disposal and source-directory removal; it does not model abrupt power loss, remote object-store delay, region failure, detection time, or human response. Percentiles are nearest-rank across the listed independent samples.
