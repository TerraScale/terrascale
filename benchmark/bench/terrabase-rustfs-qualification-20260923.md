# TerraBase RustFS storage qualification — 2026-09-23

The receipts in this report are dated evidence from 2026-09-23, mostly from
dirty or intermediate source snapshots. Treat them as historical unless a
section explicitly says current-source; they do not qualify the settled source
or the 100-million-record workload.

## Historical latest completed strict durability run

The [historical strict 1M RustFS receipt](receipts/tiered-rustfs-strict-1m-integrity-fix-20260923T2004Z/tiered-rustfs-summary.md)
passed two complete local-root-loss recoveries, exact scans and point reads,
1,000 updates, 1,000 deletes, two S3 compactions, cache eviction, and final
cold reopen with exactly 999,000 live records. Each acknowledged batch was
published and verified in the remote journal before its local WAL append.

| Measurement | Observed result |
| --- | ---: |
| Seed | 1,000,000 records in 118.079 s (8,469 records/s) |
| First / second complete local-root-loss recovery | 248.736 / 235.850 s |
| Initial / final compaction and S3 publication | 143.849 / 139.500 s |
| Initial / final cold remote reopen and scan | 236.105 / 114.662 s |
| Final live documents verified | 999,000 |
| Remote journal | 19 objects; 1,002,004 acknowledged physical records |
| Published segment objects | 8 objects; 853,771,212 bytes |
| Total interval | 1,331.472 s |
| Peak local engine storage | 1,153,389,915 bytes |
| Peak cgroup memory / limit / swap / OOM | 1,552,277,504 / 4,294,967,296 / 0 bytes / 0 events |

The [detailed receipt](receipts/tiered-rustfs-strict-1m-integrity-fix-20260923T2004Z/tiered-rustfs-receipt.json),
[runner receipt](receipts/tiered-rustfs-strict-1m-integrity-fix-20260923T2004Z/run-receipt.json),
and [resource samples](receipts/tiered-rustfs-strict-1m-integrity-fix-20260923T2004Z/resource-samples.jsonl)
retain the exact workload and cgroup evidence. The frozen benchmark assembly
SHA-256 is `ae5930effd8d32c1e2e25b6f85a2e8703a2abd7124f2e158e46e138fcefa985a`.
An unrelated build overlapped one phase, and this binary predates the later
journal-listing-order fix, so cross-run timing comparisons are indicative.

The earlier [strict 1M diagnostic](receipts/tiered-rustfs-strict-1m-20260923T152728Z/diagnostic-stop.md)
stopped during its first cold reopen and remains an incomplete historical
receipt. A separate [1M end-to-end diagnostic](receipts/e2e-resource-rustfs-1m-fixed-cfd8d1b6-20260923T151400Z/diagnostic-stop.md)
completed full backup after writes, scans, relationships, and point reads,
then stopped during incremental backup. The 1M end-to-end and 100M scale
qualifications still require completed runs.

## Evidence and current result

The [100,000-record incompressible run](receipts/tiered-rustfs-incompressible-100k-20260923T033017Z/tiered-rustfs-summary.md) completed under an isolated 4 GiB cgroup with swap disabled. It used synced WAL batches of 1,000 and 2,048 bytes of deterministic per-key incompressible payload. The first cold reopen verified all 100,000 keys and bodies. After 1,000 updates and 1,000 deletes, the final cold reopen verified 99,000 live documents and the deleted keys' absence. Both reopens read verified segments from RustFS after the SSD cache had been emptied by the tier's idle-cache policy.

| Measurement | Observed result |
| --- | ---: |
| Seed | 100,000 records in 255.232 s (392 records/s) |
| Initial compaction and S3 publication | 72.462 s |
| Initial cold reopen, exact scan, and point reads | 208.476 s |
| Updates / deletes | 1,000 in 22.862 s / 1,000 in 2.533 s |
| Final compaction and S3 publication | 181.494 s |
| Final cold reopen, exact scan, and point reads | 192.649 s |
| S3 objects after the run | 4 objects, 469,867,866 bytes |
| Peak local engine storage | 1,073,249,049 bytes |
| Peak process RSS | 454,189,056 bytes |
| Peak cgroup memory / OOM kills | 1,240,461,312 bytes / 0 |
| Swap / idle SSD cache budget | 0 bytes / 16,777,216 bytes |

The [detailed receipt](receipts/tiered-rustfs-incompressible-100k-20260923T033017Z/tiered-rustfs-receipt.json), [resource samples](receipts/tiered-rustfs-incompressible-100k-20260923T033017Z/resource-samples.jsonl), and [runner receipt](receipts/tiered-rustfs-incompressible-100k-20260923T033017Z/run-receipt.json) preserve exact counts, binary identity, limits, and phases. This run used a dirty source tree and competed with other host work, so its timing is a diagnostic baseline, not a clean throughput claim. The binary predates the subsequent ZoneTree range guard and replay optimization.

The zero explicit cache-deletion count means the tier had already trimmed idle copies before each deletion step. A cold remote refill was still verified. The 16 MiB budget limits idle copies; active reads, uploads, WAL, derived indexes, and compaction need additional local space.

### Interim 100,000-record comparison under 2 GiB

The [interim 100,000-record RustFS run](receipts/tiered-rustfs-final-100k-2k-2g-20260923T052040Z/tiered-rustfs-summary.md) used the current benchmark binary under an isolated 2 GiB cgroup with swap disabled. It used the same synced batch size and deterministic 2,048-byte per-key incompressible payload as the diagnostic baseline above. The initial cold reopen scanned and verified all 100,000 records and 512 point reads after the local segment copies were removed. The final cold reopen verified 99,000 live records and the absence of all 1,000 deleted keys after the updates and deletes.

| Measurement | Observed result |
| --- | ---: |
| Seed | 100,000 records in 39.720 s (2,518 records/s) |
| Initial compaction and S3 publication | 50.923 s |
| Initial cold reopen, exact scan, and point reads | 54.596 s |
| Updates / deletes | 1,000 in 3.216 s / 1,000 in 0.423 s |
| Final compaction and S3 publication | 55.890 s |
| Final cold reopen, exact scan, and point reads | 89.143 s |
| S3 objects after the run | 4 objects, 469,867,866 bytes |
| Peak local engine storage | 1,043,165,259 bytes |
| Peak process working set | 426,000,384 bytes |
| Cgroup memory.max / memory.peak | 2,147,483,648 / 1,325,789,184 bytes |
| Swap.max / cgroup memory events | 0 bytes / all zero |
| Cgroup I/O reads / writes at completion | 30,052,352 / 22,089,980,416 bytes |
| Idle SSD cache budget | 16,777,216 bytes |

The [detailed receipt](receipts/tiered-rustfs-final-100k-2k-2g-20260923T052040Z/tiered-rustfs-receipt.json), [resource samples](receipts/tiered-rustfs-final-100k-2k-2g-20260923T052040Z/resource-samples.jsonl), [resource envelope](receipts/tiered-rustfs-final-100k-2k-2g-20260923T052040Z/resource-envelope.json), and [runner receipt](receipts/tiered-rustfs-final-100k-2k-2g-20260923T052040Z/run-receipt.json) preserve the exact scans, phase timings, cgroup limits, I/O counters, object totals, and provenance. The measured benchmark assembly SHA-256 is `562b2ea0e9adc21fba1439dc8fed9047755503f210180daf5b42260ffca23a3b`. The receipt captured subject commit `67e35f443565c54eb731d6ea2dd989cb5c87b7be`, tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, and a dirty working-tree digest `cc183cd9873f32944df492f73b64ef45f05d48893654e905a0fbe070f2afb795`.

This is an interim binary comparison. The measured assembly predates some source changes present in the captured dirty tree and later correctness fixes that landed during and after the run. Its timing and resource measurements must not be treated as final-source qualification until Release is rebuilt from the settled source.

## Functional coverage

The [strict 1,000-document end-to-end RustFS receipt](receipts/e2e-resource-rustfs-1k-strict-journal-20260923T185200Z/e2e-volume-summary.md)
passed indexed queries, exact scans, point reads, updates, deletes,
relationships, collection listing, snapshot pagination, full and incremental
backup, PITR and final restore, compaction, cold S3 reopen, and loss of the
source root followed by S3 journal recovery. Its process did not run in an
isolated memory cgroup; it is functional evidence. The [earlier 100,000-document
end-to-end receipt](receipts/e2e-rustfs-100k-20260923T001023Z/e2e-volume-summary.md)
covered the same core query and backup/restore paths without relationships.
These smaller runs do not establish 100-million-record correctness for every
operation.

## Restore activation scope

The benchmark receipts above materialize restore generations in local target
roots. They do not exercise remote restore activation. Current source behavior
has two separate paths: same-identity S3 restore activation remains fenced
until coordinated distributed writer fencing is available, while an offline
fresh-identity restore allocates a new database namespace, publishes its
remote generation authority, and can reopen after local-root loss. Focused
restore tests cover the fresh-identity path against RustFS; backups containing
automatic-index decisions are rejected until destination-local authority is
rebuilt. Pre-activation crash recovery, concurrent destination-root
reservation hardening, provider-independent failure domains, and full
100-million-record function coverage remain pending.

## Scale gate

**100 million records are not yet qualified.** A [40,000 × 8 KiB larger-than-memory run](larger-memory-post-range-40k-8k-256m-noswap-20260923T042010Z/larger-memory-summary.md) completed under a 256 MiB cgroup with swap disabled and zero OOM events. It seeded 1,055,272,974 bytes of physical storage for 328,800,000 bytes of estimated logical data, verified the complete collection after seed and reopen, completed warm/cold and mixed reads, 100 sustained writes, compaction, and final verification. The [detailed receipt](larger-memory-post-range-40k-8k-256m-noswap-20260923T042010Z/larger-memory-receipt.json) and [live cgroup log](larger-memory-post-range-40k-8k-256m-noswap-20260923T042010Z/cgroup-live-monitor.log) preserve the result and zero OOM counters.

| Larger-than-memory phase | Measured result |
| --- | ---: |
| Cold reopen | 67.871 s |
| Warm / cold 100-operation reads | 92.37 / 65.57 operations/s |
| Uniform read-heavy 2,000 operations | 20.73 operations/s |
| Sustained 100 writes | 8.64 operations/s |
| Compaction | 231.252 s |
| Cgroup physical reads / writes near completion | 42.4 GB / 34.8 GB |

This proves a completed larger-than-memory correctness path, but the latency and I/O are not fast. The binary predates the later bottom-segment rollover and known-new physical insertion changes. The earlier [20-minute diagnostic](larger-memory-sequential-40k-8k-256m-noswap-20260923T025736Z/diagnostic-stop.md) stopped before a sample and remains a failed run. A finite-memory RustFS ladder, improved I/O measurements, and the actual 100-million-record run remain necessary before a throughput or correctness claim at that scale.

The current S3 tier stores sealed, checksum-bound segments in RustFS and uses SSD as a verified read cache. ZoneTree's file-stream provider requires seekable writes, replacement, truncation, and durable flush; it does not provide a native S3 random-access disk device. WAL, manifests, derived indexes, and compaction workspace remain local. See [storage-tiering.md](../../terrabase/docs/storage-tiering.md) for the storage contract and limits.

## Completed 1.1M strict sequence-fix run

The [strict sequence-fix receipt](receipts/tiered-rustfs-sequence-fix-1100k-20260923T070017Z/tiered-rustfs-summary.md)
completed 1,100,000 records with per-key incompressible 128-byte payloads,
synced WAL batches of 1,000, 1,000 updates, 1,000 deletes, and 512 point reads.
The initial cold remote reopen verified 1,100,000 exact records; the final cold
remote reopen verified 1,099,000 exact live records and all 1,000 deleted keys
absent. Initial and final compaction each processed 1,100,000 records.

| Phase or resource | Observed result |
| --- | ---: |
| Seed | 102.869 s; 10,693 records/s |
| Initial compaction and RustFS publication | 165.704 s |
| Initial cold remote reopen and exact scan | 151.406 s |
| Updates / deletes | 5.460 s / 4.906 s |
| Final compaction and RustFS publication | 162.399 s |
| Final cold remote reopen and exact scan | 140.368 s |
| Total interval | 734.193 s |
| Cgroup memory limit / swap limit | 4,294,967,296 B / 0 B |
| Cgroup memory peak / OOM kills | 1,441,931,264 B / 0 |
| Cgroup device writes | 18,519,907,840 B |
| Peak local engine storage | 1,270,509,224 B |
| Peak process RSS | 385,134,592 B |
| Idle SSD cache budget | 16,777,216 B |
| Peak SSD cache / active staging | 134,217,539 B / 134,217,539 B |
| Remote objects after initial generation | 4 / 461,989,086 B |
| Remote objects after final generation | 8 / 924,148,062 B |

Remote objects are retained across both generations by design. The 16 MiB
value limits idle cache copies; active read leases, upload/download staging,
WAL, derived indexes, and compaction require additional local space. This run
used a 17,179,869,184-byte local-storage cap and stayed below it. The direct
segment run qualifies point reads, collection scans, updates, deletes,
compaction, checksum-bound RustFS publication, and cold remote reopen. It does
not qualify indexed queries or backup/restore, which remain covered by the
separate E2E receipts above.

The benchmark assembly SHA-256 is
`e5534eeb25ee0367c7787cbeb592a19f4b08c2ff506de9d3632ad3c686679861`.
The subject is commit `67e35f443565c54eb731d6ea2dd989cb5c87b7be`, tree
`69203400f13c7e311cf5fb569a081eeab36e8cbc`, `dirty: true`, with dirty
working-tree digest
`bfbb97c3dc6331de50ba1f516113cd3e7b3b3016d5a3f1dffefd936903972c3a`.
The source and benchmark were Release `.NET 11.0.0-rc.1.26425.128` builds.
Throughput and cold-reopen recovery remain low at this scale, and 100 million
records have not yet been run or qualified.

## Completed 1.1M 32K-part run

The [32K-part RustFS receipt](receipts/tiered-rustfs-part32k-1100k-20260923T071654Z/tiered-rustfs-summary.md)
completed the same 1,100,000-record strict sequence, with 1,000 updates, 1,000
deletes, and exact cold remote verification before and after mutation. The
initial scan counted 1,100,000 records; the final scan counted 1,099,000 live
records and found every deleted key absent.

| Phase or resource | Observed result |
| --- | ---: |
| Seed | 93.802 s; 11,727 records/s |
| Initial compaction and RustFS publication | 144.783 s |
| Initial cold remote reopen and exact scan | 158.005 s |
| Updates / deletes | 1,000 each |
| Final compaction and RustFS publication | 152.797 s |
| Final cold remote reopen and exact scan | 153.102 s |
| Total interval | 708.367 s |
| Cgroup memory limit / swap limit | 4,294,967,296 B / 0 B |
| Cgroup memory peak / OOM kills | 1,471,598,592 B / 0 |
| Cgroup device writes | 11,730,688,000 B |
| Peak local engine storage | 1,270,492,989 B |
| Peak process RSS | 395,309,056 B |
| Idle SSD cache budget | 16,777,216 B |
| Peak SSD cache / active staging | 134,217,539 B / 134,217,539 B |
| Remote objects after final generation | 8 / 924,148,062 B |

Compared with the preceding 65K-part strict run, total runtime improved by
25.826 s (about 3.5%) and device writes decreased by about 36.7%. Seed and
compaction were faster, while both cold reopens were slower. The memory peak
increased by 29,667,328 B (about 2.1%). Both runs used dirty source trees and
shared host work, so this is directional evidence rather than a controlled A/B
benchmark. The 16 MiB idle-cache budget and the active staging, WAL, index, and
compaction space limits remain as described above. Indexed query and
backup/restore coverage remains supplied by the separate E2E receipts; this
direct segment receipt does not extend that coverage. One hundred million
records remain unrun and unqualified.

The benchmark assembly SHA-256 is
`8845dff9857eb7ded0521462aa7e28c79dcfe41061f46ae69b4bd6111c91e0ae`.
The subject is commit `67e35f443565c54eb731d6ea2dd989cb5c87b7be`, tree
`69203400f13c7e311cf5fb569a081eeab36e8cbc`, `dirty: true`, with dirty
working-tree digest
`029dd1e567c7b7857f0c594e256e27d50a8d7ff14bdef55b49b910e03cf874ed`.

## Current-source 256 MiB larger-than-memory delete qualification

The [larger-than-memory delete summary](receipts/larger-memory-delete-40k-8k-256m-20260923T074230Z/larger-memory-summary.md)
completed a current-source `StorageEngine` run with 40,000 rows and 8 KiB
per-key incompressible payloads under a 256 MiB memory cap and zero swap. The
estimated logical dataset was 328,800,000 B while seeded physical storage was
1,056,100,778 B, exceeding the memory cap. After sustained upserts, the run
deleted exactly 1,000 deterministically spread keys. It verified 1,000 deleted
key absences and 39,000 survivor bodies before and after a cold reopen.

| Phase or resource | Observed result |
| --- | ---: |
| Warm / cold 100-operation reads | 185.72 / 150.53 operations/s |
| Uniform read-heavy 2,000 operations | 190.55 operations/s |
| Sustained 100 writes | 87.71 operations/s |
| 1,000 deletes | 116.56 operations/s; 8.579 s |
| Delete cold reopen | 65.094 s |
| Compaction | 91.852 s |
| Memory cap / memory peak | 268,435,456 B / 268,435,456 B |
| Swap current / OOM / OOM kill | 0 B / 0 / 0 |
| Final sampled cgroup reads / writes | 40,427,720,704 B / 35,054,989,824 B |
| Final `memory.events max` count | 136,031 |

The [detailed receipt](receipts/larger-memory-delete-40k-8k-256m-20260923T074230Z/larger-memory-receipt.json),
[cgroup monitor](receipts/larger-memory-delete-40k-8k-256m-20260923T074230Z/cgroup-live-monitor.log),
and [run receipt](receipts/larger-memory-delete-40k-8k-256m-20260923T074230Z/run-receipt.json)
record the complete status, exact survivor and absence checks, and resource
envelope. The run receipt reports `exitCode: 0`; the cgroup runner reports
`runner_exit=0`. A wrapper shell status of 1 occurred only after completion
when `/etc/bash_logout` evaluated an unset variable.

Compared with the older 40K larger-than-memory baseline in the scale gate,
the reported sustained-write rate was 87.71 versus 8.64 operations/s and
compaction was 91.852 versus 231.252 s. These timings are directional only:
the source working tree and host conditions differed, so they are not a
controlled A/B result. The nonzero `memory.events max` count shows that the
workload repeatedly reached the configured cap while still completing without
swap or OOM kills. This is a local larger-than-memory delete qualification;
it does not extend RustFS/S3 coverage or qualify 100 million records.

The benchmark summary identifies commit `0f73f7874fd873e5aa2605fd06ad6854d1378372`
with a dirty tree. The run receipt identifies the TerraBase source subject as
commit `67e35f443565c54eb731d6ea2dd989cb5c87b7be`, tree
`69203400f13c7e311cf5fb569a081eeab36e8cbc`, `dirty: true`, with working-tree
digest `aa4748f40b9dd718fff359f8ff6339d10daa4f09ace287c014459686e9d8f876`.
The frozen benchmark DLL SHA-256 is
`002ba1e6f69fa3bd62987904117e1eea3d2cc4873cd4958dd3e62072f8549dd8`.

## Completed 100K indexed root-loss RustFS run

The [indexed root-loss summary](receipts/e2e-root-loss-100k-indexed-20260923T075211Z/e2e-volume-summary.md)
completed with the run receipt reporting `status: complete` and `exitCode: 0`.
It wrote and verified 100,000 documents, exercised relationships, point reads,
full and incremental backup, three restore boundaries, snapshot pagination,
collection listing, an active automatic index, indexed queries, compaction,
and cold RustFS reopen. The [detailed volume receipt](receipts/e2e-root-loss-100k-indexed-20260923T075211Z/e2e-volume-receipt.json)
records the exact counts and phase timings.

| Phase or resource | Observed result |
| --- | ---: |
| Bulk write / exact read-back | 11.967 s / 100,000 documents |
| Relationships | 10,000 writes; 40,000 endpoints verified |
| Point reads | 512; 586.98 operations/s; p50 1.620 ms; p95 2.861 ms |
| Full backup | 719.759 s; 11,138,247 B |
| Updates / deletes | 1,000 / 1,000 |
| Incremental backup | 496.632 s; 2,068,329 B |
| Full / PITR / final restore verification | 100,000 / 100,000 / 99,000 exact; 1,000 absent |
| Snapshot pages / collection pages | 100,000 documents / 3 collections |
| Cold RustFS reopen | 66.040 s; 99,000 exact; SSD cache 0 B; local segments 0 B |
| Automatic index build | 383.136 s; active on sequence |
| Indexed queries | 100 verified; 11.606 operations/s; p50 6.306 ms; p95 22.992 ms; p99 7,876.426 ms |
| Compaction / verification | 14.566 s; 132,501 records and 1,000 tombstones / 99,000 exact |

The [root-loss checkpoint](receipts/e2e-root-loss-100k-indexed-20260923T075211Z/root-loss-recovery-checkpoint.json)
reports `status: complete`, `phase: verified`, `sourceRootDeleted: true`, two
applied log entries, two artifacts verified, and 13,206,576 artifact bytes.
The root-loss receipt verified 99,000 surviving documents, all 1,000 deleted
documents absent, three collections, one remote segment object, zero SSD cache
bytes, and zero restored local segment bytes after eviction. Root-loss restore
took 19.653 s and the cold remote reopen took 50.038 s. This confirms source
root deletion and remote recovery for this indexed 100K path.

The run used the isolated scope
`terrabase-rootloss-indexed-100k-20260923T075211Z` with a 4 GiB memory limit
and zero swap. A live cgroup observation recorded `memory.peak=941,387,776 B`
and all `memory.events` counters at zero. The scope files vanished after exit
and no final cgroup envelope was preserved, so this is retained live evidence
with a retention limit rather than a final-envelope measurement. The
[live cgroup observation](receipts/e2e-root-loss-100k-indexed-20260923T075211Z/cgroup-observed.md)
records that distinction precisely.
The [indexed query samples](receipts/e2e-root-loss-100k-indexed-20260923T075211Z/indexed-query-samples.jsonl)
and [worst-query table](receipts/e2e-root-loss-100k-indexed-20260923T075211Z/indexed-query-worst.md)
preserve the query evidence.
The 7,876.426 ms p99 includes the first cold query, which triggered 12 Gen2
collections. Across queries 1–99, the observed p50/p95/p99 were
6.213/18.042/37.452 ms with no later Gen2 collection. These 100K indexed
query samples do not establish 100M build or query performance.

The run receipt identifies source commit
`67e35f443565c54eb731d6ea2dd989cb5c87b7be`, tree
`69203400f13c7e311cf5fb569a081eeab36e8cbc`, `dirty: true`, with working-tree
digest `ce3d168b52ace142b436979187bc57089642fb959ed012bed969b9571810c419`.
Its receipt SHA-256 is
`76bdfd2d01be5b4974faf75148eab74f3ccef59a802c9fa577ba2efee5b1ea56`.

## Completed 2M direct RustFS tier run

The [2M tier summary](receipts/tiered-rustfs-2m-20260923T080813Z/tiered-rustfs-summary.md)
completed with the run receipt reporting `status: complete` and `exitCode: 0`.
It used synced WAL batches of 1,000, 128-byte per-key incompressible payloads,
an isolated 4 GiB cgroup with zero swap, 1,000 updates, 1,000 deletes, and 512
point reads. The initial cold remote reopen verified 2,000,000 exact records;
the final cold remote reopen verified 1,999,000 exact live records and all 1,000
deleted keys absent.

| Phase or resource | Observed result |
| --- | ---: |
| Seed | 255.875 s; 7,816 records/s |
| Initial compaction and RustFS publication | 365.948 s; 2,000,000 records |
| Initial cold remote reopen and exact scan | 359.025 s; 2,000,000 records |
| Updates / deletes | 3.789 s / 0.078 s; 1,000 each |
| Final compaction and RustFS publication | 369.390 s; 2,000,000 records |
| Final cold remote reopen and exact scan | 362.362 s; 1,999,000 records |
| Final point reads | 512 verified |
| Total interval | 1,718.114 s |
| Cgroup memory max / peak | 4,294,967,296 B / 2,313,854,976 B |
| Cgroup swap max / OOM / OOM kill | 0 B / 0 / 0 |
| Cgroup isolated | Required and observed |
| Final cgroup device reads / writes | 1,245,184 B / 21,943,336,960 B |
| Peak local engine storage | 2,013,127,935 B of 17,179,869,184 B cap |
| Peak process RSS | 463,527,936 B |
| Peak SSD cache / staging | 134,217,609 B / 134,217,609 B |
| RustFS objects after final generation | 14 / 1,681,948,356 B |
| SSD cache after final verification | 0 B |

The [detailed receipt](receipts/tiered-rustfs-2m-20260923T080813Z/tiered-rustfs-receipt.json),
[resource envelope](receipts/tiered-rustfs-2m-20260923T080813Z/resource-envelope.json),
[resource samples](receipts/tiered-rustfs-2m-20260923T080813Z/resource-samples.jsonl),
and [run receipt](receipts/tiered-rustfs-2m-20260923T080813Z/run-receipt.json)
preserve the exact counts, phase measurements, isolated cgroup evidence, and
provenance. The segment manifest was committed after RustFS upload; both cold
reopens therefore exercised SSD cache eviction, remote refill, and checksum
validation. This direct segment run qualifies point reads, collection scans,
updates, deletes, compaction, and RustFS publication/reopen at 2,000,000
records. Indexed queries and backup/restore remain covered by the E2E workload
receipts and are not claimed by this run.

The phase timings and throughput are directional because this run overlapped
the 100K E2E workload and focused TUnit/build activity on the host. The exact
counts, correctness checks, RustFS object totals, and isolated cgroup resource
envelope remain valid for this receipt.

The run receipt identifies source commit
`67e35f443565c54eb731d6ea2dd989cb5c87b7be`, tree
`69203400f13c7e311cf5fb569a081eeab36e8cbc`, `dirty: true`, with working-tree
digest `f4cbba5c3615a3d07ef93081f2451f0ea45e7c933646c26f0a083b07562fc759`.
The benchmark assembly SHA-256 is
`8845dff9857eb7ded0521462aa7e28c79dcfe41061f46ae69b4bd6111c91e0ae`.
The run receipt SHA-256 is
`2076d527aec2b2c4327e279a01f04058de9840b1f3231ca4c25b1060b44cd454`.

## Frozen 10M tier diagnostic stopped at 3.5M

The [10M seed diagnostic](receipts/tiered-rustfs-10m-20260923T083828Z/diagnostic-stop.md)
was deliberately stopped after its 3,500,000-record checkpoint. It did not
complete seeding or publish a RustFS segment, so it is **not** a 10M pass. Its
workload receipt remains in the `running` state because the process was stopped
externally. The frozen binary predates the current block-cache candidate index.

| 500K seed interval | Records/s | Elapsed (s) |
| --- | ---: | ---: |
| 0–0.5M | 11,673 | 42.833 |
| 0.5–1M | 10,683 | 46.803 |
| 1–1.5M | 6,858 | 72.907 |
| 1.5–2M | 4,559 | 109.684 |
| 2–2.5M | 3,036 | 164.671 |
| 2.5–3M | 2,030 | 246.272 |
| 3–3.5M | 1,469 | 340.481 |

At 3.5M, the isolated 4 GiB, zero-swap cgroup had a 2,224,427,008 B memory
peak and zero OOM events. The engine occupied 2,422,449,024 B locally, with
6,695,303,168 B of cgroup device writes. Host build and test contention also
affected wall time. The declining rate is a scale warning, not a clean
performance comparison. A new binary and controlled scale rung are required
before attempting the 100M workload.

## Current-source 2M direct RustFS result

The [current-source 2M summary](receipts/tiered-rustfs-current-2m-20260923T085949Z/tiered-rustfs-summary.md)
completed with `status: complete`, runner `exitCode: 0`, and zero OOM or swap
events. It verified exact bodies and versions for all 2,000,000 records after
the first cold RustFS reopen. It then applied 1,000 updates and 1,000 deletes,
published a second segment generation, and verified all 1,999,000 surviving
records, all deleted keys absent, and 512 final point reads after a second
cold RustFS reopen. The segment cache was zero bytes after both verifications.

| Phase or resource | Older 2M binary | Current-source 2M binary |
| --- | ---: | ---: |
| Synced seed | 255.875 s; 7,816 records/s | 252.300 s; 7,927 records/s |
| First compaction and RustFS publication | 365.948 s | 351.188 s |
| First cold reopen and exact verification | 359.025 s | 361.531 s |
| Updates / deletes | 3.789 / 0.078 s | 3.991 / 0.082 s |
| Second compaction and RustFS publication | 369.390 s | 351.870 s |
| Final cold reopen and exact verification | 362.362 s | 349.047 s |
| Total interval | 1,718.114 s | 1,671.485 s |
| Cgroup memory peak | 2,313,854,976 B | 2,213,838,848 B |
| Peak local engine storage | 2,013,127,935 B | 2,012,380,993 B |
| RustFS objects after second generation | 14; 1,681,948,356 B | 14; 1,681,948,356 B |

The first current-source engine open took 310.636 s and its exact scan took
47.286 s; the final open took 294.953 s and exact scan 49.827 s. Rebuilding
the disposable derived indexes during open dominates the cold-read time.
This run used a 4,294,967,296 B cgroup memory cap and zero swap; final cgroup
device writes were 21,792,755,200 B. Peak process RSS was 390,901,760 B.
The first cold reopen left 964,500,201 B of local derived indexes. Its exact
scan temporarily raised local usage by approximately 592,590,890 B for a
snapshot spool. S3 is the sealed-segment authority after compaction, but this
implementation still needs corpus-sized local index and scan working space;
the 16 MiB segment-cache setting is not a total SSD limit.

The [workload receipt](receipts/tiered-rustfs-current-2m-20260923T085949Z/tiered-rustfs-receipt.json),
[resource envelope](receipts/tiered-rustfs-current-2m-20260923T085949Z/resource-envelope.json),
and [runner receipt](receipts/tiered-rustfs-current-2m-20260923T085949Z/run-receipt.json)
preserve exact counts and provenance. This binary was SHA-256
`35739f36cd01e0d256a48368272107d49f22dbe2b28bca5d350e3914339a21a0`.
It predates the reviewed cache-candidate and pinned-boundary optimizations.
Both 2M runs used dirty source trees and shared host activity, so the timing
comparison is directional. Neither run qualifies 10M or 100M records; this
direct workload also does not exercise indexed queries or backup/restore.

## Candidate cache-index 2M RustFS result

The [candidate cache-index 2M summary](receipts/tiered-rustfs-current-cachefix-2m-20260923T093026Z/tiered-rustfs-summary.md)
also completed with runner `exitCode: 0`. It verified 2,000,000 exact records
and 512 point reads after its first cold remote reopen, then 1,000 updates,
1,000 deletes, 1,999,000 exact surviving records, deleted-key absence, and
512 final point reads after a second cold remote reopen. The cgroup had zero
swap and OOM events. The binary contained an initial indexed block-cache
candidate implementation; it predates the final ownership corrections,
pinned multipart boundary reads, and clean-close checkpoint work.

| Phase or resource | Pre-candidate 2M | Candidate cache-index 2M |
| --- | ---: | ---: |
| Synced seed | 252.300 s; 7,927 records/s | 179.830 s; 11,122 records/s |
| First compaction and RustFS publication | 351.188 s | 288.175 s |
| First cold reopen and exact scan | 361.531 s | 298.970 s |
| Second compaction and RustFS publication | 351.870 s | 279.000 s |
| Final cold reopen and exact scan | 349.047 s | 261.985 s |
| Total interval | 1,671.485 s | 1,313.036 s |
| Cgroup memory peak | 2,213,838,848 B | 2,247,114,752 B |
| Peak local engine storage | 2,012,380,993 B | 2,012,415,120 B |
| Final RustFS objects | 14; 1,681,948,356 B | 14; 1,681,948,356 B |

The [candidate workload receipt](receipts/tiered-rustfs-current-cachefix-2m-20260923T093026Z/tiered-rustfs-receipt.json),
[resource envelope](receipts/tiered-rustfs-current-cachefix-2m-20260923T093026Z/resource-envelope.json),
and [runner receipt](receipts/tiered-rustfs-current-cachefix-2m-20260923T093026Z/run-receipt.json)
preserve the exact source and cgroup evidence. Its Release benchmark DLL was
SHA-256 `6c66af4d7be540dde31d305e25bb7122a746a90e0185ab7e4e1831f27f53ede3`.
The run overlapped other host builds and tests, so the timing difference is
directional; the identical counts and byte totals establish functional parity
at 2M for these binaries. The next gate is a new-source 10M run under an
isolated memory and SSD envelope. One hundred million records remain unrun.

## Strict 100K end-to-end RustFS result

The [100K end-to-end summary](receipts/e2e-resource-rustfs-100k-strict-20260923T110100Z/e2e-volume-summary.md)
completed with [runner `exitCode: 0`](receipts/e2e-resource-rustfs-100k-strict-20260923T110100Z/run-receipt.json).
It used a 4 GiB isolated cgroup with swap disabled and a 25,769,803,776 B
local storage cap. The workload verified 100,000 exact documents, paginated
listing, point reads, full and incremental backups, full/PITR/final restores,
1,000 updates, 1,000 deletions, 1,000 indexed queries, compaction, and a cold
RustFS reopen with 99,000 live documents and zero restored local segments.

| Phase or resource | Measured result |
| --- | ---: |
| Synced bulk write | 14.895 s; 6,714 documents/s |
| Full / incremental backup | 23.252 / 25.261 s |
| Full / PITR / final restore | 21.094 / 23.998 / 24.846 s |
| Automatic index build | 24.949 s |
| Indexed queries | 1,000 verified; 72 operations/s |
| Cold RustFS reopen | 44.379 s; 99,000 exact; SSD cache 0 B |
| Total interval | 405 s |
| Peak local including RustFS data root | 16,277,192,704 B of 25,769,803,776 B cap |
| RustFS data-root baseline / growth | 15,959,748,608 / 96,067,584 B |
| Peak RSS / cgroup memory cap | 744,996,864 / 4,294,967,296 B |
| Swap / OOM / OOM kills | 0 / 0 / 0 |

The [detailed receipt](receipts/e2e-resource-rustfs-100k-strict-20260923T110100Z/e2e-volume-receipt.json),
[resource envelope](receipts/e2e-resource-rustfs-100k-strict-20260923T110100Z/resource-envelope.json),
and [resource samples](receipts/e2e-resource-rustfs-100k-strict-20260923T110100Z/resource-samples.jsonl)
retain the actual limits and phase counts. The receipt SHA-256 is
`e042f7c351e1476ba70c90d35b6416a4f66c2ea196b6d5fd86899859bc6c16ba`.
The source remained dirty at run time, with working-tree digest
`3a4e92c914d0427956e24685de598f4f6ed4127de173dfa6662977000393124c`.
This rung used zero relationships and did not enable root-loss recovery; both
have separate smaller receipts above. It also predates the final restore-target
cleanup fixture, so its peak includes retained restore roots.

The measured concurrent 100K peak, after subtracting the existing RustFS
baseline and scaling linearly, projects about 333.4 GB for 100M records. The
host had about 371.7 GB free at this run's minimum, leaving only 38.3 GB
projected headroom before nonlinear compaction/backup growth. Later target
cleanup should reduce the peak, but has been measured only in a small root-loss
fixture. The full 100M end-to-end run is not admitted under the current host
disk envelope until a larger cleanup rung establishes enough reserve. This is
a capacity projection, not a 100M correctness or throughput result.

## Completed 10M RustFS candidate

The [10M direct tier summary](receipts/tiered-rustfs-cachecandidate-10m-20260923T095551Z/tiered-rustfs-summary.md)
and [runner receipt](receipts/tiered-rustfs-cachecandidate-10m-20260923T095551Z/run-receipt.json)
report `status: complete`, exit code 0. The run used deterministic per-key
incompressible 128-byte payloads, synced WAL batches of 1,000, a 4 GiB
isolated cgroup with swap disabled, and a 17,179,869,184 B local engine cap.
After the first cold RustFS reopen it verified 10,000,000 exact bodies and
versions. It then applied 1,000 updates and 1,000 deletes, published another
generation, and verified 9,999,000 exact survivors plus every deleted key's
absence after a second cold reopen.

| Phase or resource | Measured result |
| --- | ---: |
| Synced seed | 1,315.050 s; 7,604 records/s |
| First compaction and RustFS publication | 2,277.694 s; 32 objects, 4,208,890,458 B |
| First cold open / exact scan | 1,685.076 / 261.433 s |
| Updates / deletes | 46.274 / 3.441 s; 1,000 each |
| Second compaction and RustFS publication | 2,235.587 s |
| Final cold open / exact scan | 1,715.129 / 240.678 s |
| Total interval | 9,793.655 s (2 h 43 min 14 s) |
| Final RustFS objects | 64; 8,417,950,806 B across two retained generations |
| Peak local engine storage / cap | 9,540,806,297 / 17,179,869,184 B |
| Peak RSS / cgroup memory peak | 557,187,072 / 4,296,261,632 B |
| Swap / OOM / OOM kills | 0 / 0 / 0 |
| Cgroup device reads / writes | 29,129,007,104 / 114,397,917,696 B |
| Idle cache budget / peak active cache and staging | 16,777,216 / 134,217,609 B |

The [detailed receipt](receipts/tiered-rustfs-cachecandidate-10m-20260923T095551Z/tiered-rustfs-receipt.json),
[resource samples](receipts/tiered-rustfs-cachecandidate-10m-20260923T095551Z/resource-samples.jsonl),
and [resource envelope](receipts/tiered-rustfs-cachecandidate-10m-20260923T095551Z/resource-envelope.json)
preserve exact counts and limits. The frozen benchmark assembly SHA-256 was
`6c66af4d7be540dde31d305e25bb7122a746a90e0185ab7e4e1831f27f53ede3`;
the receipt records a dirty source working-tree digest
`6117fd05f286dde09f610cc450a48ee81af4c73537e2f4ff201dd4304d9a9706`.
This binary predates the clean-close checkpoint, range-only S3 compatibility
probe, and later correctness fixes. The long cold-open times are diagnostic
for this candidate, not final-source performance. This direct workload does
not cover indexed queries or backup/restore; the separate 100K end-to-end
result above covers those functions. One hundred million records remain unrun.

## Provisional 1M RustFS diagnostic stopped during full backup

The [aborted diagnostic receipt](receipts/e2e-resource-rustfs-1m-final-rootloss-20260923T125600Z/ABORTED.md)
records a real authenticated AWS SDK to local RustFS path for 1,000,000
documents and 10,000 relationships. It is retained as diagnostic evidence and
is **not a passing qualification**.

| Phase or resource | Measured result |
| --- | ---: |
| Bulk write | 1,000,000 records reached; progress stream ended at `12:46:30.128Z` |
| Relationship write / verify | Both phases reached before backup; 10,000 relationships configured |
| Initial RustFS publication | 4 objects; 482,237,361 B |
| Exact scan / snapshot paging / point reads | 1,000,000 / 1,000,000 / 10,000 verified checkpoints |
| Full backup | Began `12:58:23Z`; no completion after about 35 min; run stopped |
| Cgroup memory peak / limit | 4,047,474,688 / 4,294,967,296 B |
| Process RSS peak | 3,400,101,888 B |
| OOM / OOM kill events | 0 / 0 |
| Peak local storage sample | 21,788,221,440 B |
| Peak RustFS data-root usage | 20,751,941,632 B |
| Last RustFS sample | 5 objects; 482,237,644 B |
| Systemd runtime | 48 min 20.717 s wall; 1 h 1 min 1.016 s CPU |

The exact payload, endpoint, bucket, service journal evidence, source identity,
and binary hashes are preserved in the receipt. The transient service ended
without a timeout, OOM, or exception message, but no normal run receipt or
summary was emitted; the exact stop signal is therefore unresolved. This rung
does not qualify updates, deletes, indexed queries, backup completion, restore,
total SSD-loss recovery, or 100M records. No 100M workload is admitted.

## 1M functional/resource run failed at S3 backup barrier

The [failure receipt](receipts/e2e-resource-rustfs-1m-extsort-temproot-20260923T142602Z/FUNCTIONAL-RESOURCE-FAILURE.md)
records the fresh Release run with an isolated 4 GiB/zero-swap cgroup and a
run-owned absolute temp root. It reached 1,000,000 writes, relationship
verification, exact scan, snapshot paging, and 10,000 point reads. Full backup
then failed before external sort began with
`System.ObjectDisposedException: Cannot access a closed Stream` in
`S3NativeBackupObjectStore.PutSmallIfAbsentAsync` while acquiring the source
chain barrier.

| Measure | Result |
| --- | ---: |
| Run duration | 15m43.595s wall; 18m16.730s CPU |
| Peak cgroup / limit | 1,256,841,216 / 4,294,967,296 B |
| Peak RSS | 356,732,928 B |
| OOM / OOM kill | 0 / 0 |
| Peak local accounting / cap | 23,011,581,952 / 25,769,803,776 B |
| Peak RustFS objects / bytes | 4 / 482,237,361 B |
| Failure phase | Full backup; updates, deletes, restore, and indexed queries not reached |

This is functional/resource evidence, not strict every-acknowledged-write S3
durability evidence. The run's source path has no remote commit journal, and the
backup stream failure prevented exercising the corrected external-sort path.
The failed bucket and output remain preserved; the next rerun must use a new
benchmark-generated bucket and a new output root.
