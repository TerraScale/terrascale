# Strict 1M RustFS diagnostic stop

Status: **incomplete diagnostic; not a qualification pass**. The frozen Release run was stopped at 2026-09-23 17:52:23 UTC after the first cold S3 reopen remained CPU busy without a completed scan checkpoint. The live stack identified repeated ZoneTree lookups in `ValidateRemoteJournalMatchesCompactedStateUnsafe` while comparing the compacted local state with the remote commit journal. The [JSON diagnostic receipt](diagnostic-stop-receipt.json), [stack trace](/tmp/terrabase-evidence/strict-1m-stack.txt), resource samples, progress log, source root, and RustFS objects are retained.

| Completed phase | Records verified | Duration | Result |
| --- | ---: | ---: | --- |
| Synced seed writes, S3 journal before local WAL | 1,000,000 | 127.686 s | 7,832 writes/s |
| Complete local-root loss, remote journal replay, exact scan and point reads | 1,000,000 | 237.198 s | Passed |
| Initial compaction and S3 segment publication | 1,000,000 | 194.456 s | 4 objects, 426,417,309 B |
| First cold S3 reopen and exact scan | 0 scan checkpoints | stopped | CPU hotspot; no pass |

| Resource or evidence | Observed value |
| --- | ---: |
| Cgroup memory limit / peak | 4,294,967,296 / 1,539,842,048 B |
| Swap limit / OOM / memory.max events | 0 B / 0 / 0 |
| Peak sampled local engine storage | 1,075,866,870 B |
| Minimum observed free space | 361,817,444,352 B |
| Benchmark DLL SHA-256 | `2b7c4c92bed0a4555903630139f1031628e006091498121c37d148726897243c` |
| Stack SHA-256 | `5d3f462c3cfc378d801946c2576ebf7829697e1145b5ddc63def4912fcca4d3c` |

Payload: 128 incompressible bytes per record; batch size 65,536; isolated 4 GiB zero-swap cgroup. The run did **not** complete cold reopen, updates, deletes, the second SSD-loss drill, final compaction, or final exact verification. No 10M or 100M strict durability result follows from this diagnostic.
