# Native RustFS abrupt source-loss recovery drill

Held subject: /tmp/terrabase-heldout-snapshot-20260924T0340Z/terrabase; source manifest SHA-256 684cabe6b8cff1865500e9eb2fd3b486fadb5f37d98125aef88629688738395f; 1641 verified files and 0 mismatches. Runner DLL SHA-256 5583191496e02070f2c714fb31a2cbeeb7e713285aedf7874a8ff12015b251a3; TerraBase DLL SHA-256 1e42aec6849a095b9224db8db66f4f4baf41928f1133985c771fc5333056ef03.
Provider: RustFS container terrabase-rustfs-20260922, image 1e81764caba3be454540c061eb50beeb7706b3c46ecec9b4243498b585760c1b, loopback http://127.0.0.1:19000; isolated bucket tb-abrupt-7e418efe8ddb4c0a removed after the run. Credentials were read from the local container at runtime and are absent from receipts.
Runtime: .NET 11.0.0; NixOS 26.05 (Yarara); X64; 46 visible logical processors.
Fixture settings: 5 minute RPO, 30 minute RTO, NoBackupEncryption, and HMAC-SHA256 signed manifests. The observed values are local measurements.
Fault: a child process left the source storage engine open after one durable, unarchived write and 250 ms delay. The parent observed its ready barrier, sent SIGKILL via Process.Kill, waited for a nonzero exit, deleted the source directory, then restored from native RustFS objects into a fresh filesystem target.
RTO starts when the parent invokes the kill and ends after restore, fresh reopen, exact document scan, schema registration, and decision authority import. Recoverable age is kill time minus archived tip source timestamp.

| Sample | Child exit | Tip / source / restored frontier | Recoverable age | Lost write age | Kill wait | Source deletion | List | Plan | Restore | Verify | Verified RTO | Result |
| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 137 | 36 / 37 / 36 | 504.7 ms | 283.5 ms | 16.1 ms | 5.0 ms | 196.3 ms | 195.9 ms | 2554.2 ms | 1772.8 ms | 4769.4 ms | 32 exact documents; control 2; 1 lost write; schema and authority match |
| p50 | — | — | 504.7 ms | — | — | — | — | — | — | — | 4769.4 ms | — |
| p95 | — | — | 504.7 ms | — | — | — | — | — | — | — | 4769.4 ms | — |

Failures: 0 of 1 samples. All restored frontiers matched their source's committed full plus two incremental chain. Raw timestamps, manifest identities and SHA-256 values, and stage durations are in samples.jsonl; sample-N-kill.json files record child PID, exit code, and barrier output.
This is a single-host process-kill drill against a loopback S3-compatible provider. It does not model host power loss, WAN or region failure, detection time, or human response. NoBackupEncryption and local HMAC signing are fixture settings, not production security evidence. Percentiles use nearest rank over these samples.
