# Strict RustFS 10M ingest calibration analysis

## Run identity

- Frozen benchmark assembly: `TerraBase.Benchmarks.dll`
- Assembly SHA256: `2b7c4c92bed0a4555903630139f1031628e006091498121c37d148726897243c`
- Prefix/output: `tiered-rustfs-strict-10m-ingest-20260923T152728Z-rerun`
- Exit: `0`; receipt status: `complete`
- Records requested: `10,000,000`
- Batch: `65,536`; payload: 128-byte `per-key-incompressible`

## Throughput slope

| Million records completed | Elapsed seed seconds | Interval records/s | Local storage bytes |
| ---: | ---: | ---: | ---: |
| 1 | 145.478 | 6,874 | 679,054,297 |
| 2 | 286.149 | 7,109 | 1,362,453,232 |
| 3 | 454.876 | 5,927 | 2,080,530,475 |
| 4 | 651.289 | 5,091 | 2,751,848,389 |
| 5 | 842.970 | 5,217 | 3,423,240,482 |
| 6 | 1,041.729 | 5,031 | 4,095,045,592 |
| 7 | 1,257.952 | 4,625 | 4,767,520,269 |
| 8 | 1,501.677 | 4,103 | 5,442,387,658 |
| 9 | 1,775.071 | 3,658 | 6,119,020,142 |
| 10 | 2,063.996 | 3,461 | 6,846,170,731 |

Overall seed throughput was **4,845 records/s**. The interval rate fell from
6,874 records/s in the first million to 3,461 records/s in the last million,
a slope of approximately **-399 records/s per additional million records**
(linear fit over the million-record interval rates).

Local engine storage grew at approximately **681 bytes per record**. The final
local WAL was 2,013,265,920 bytes. The remote journal contained 153 objects and
4,223,773,992 bytes. RustFS segment objects were zero because ingest-only skips
compaction and segment publication.

## Durability and frontier

- Remote acknowledged batches: 153
- Remote acknowledged physical records: 10,000,001
- Validated remote frontier: 10,000,001
- Frontier matched the journal acknowledgement snapshot.
- Remote frontier validation: 180.813 s
- The requested seed count is 10,000,000 documents. The extra physical record
  is expected from the benchmark's retry-status durability path: every
  non-final seed batch uses an automatic request, while the final batch creates
  an explicit `seedRetryRequest`. `ApplyBatchUnsafe` persists one internal
  request-receipt record for that non-automatic request after the document
  records. `AppendUnsafe` publishes the complete frame to the remote journal
  and then appends the same records to the local WAL. The resulting physical
  frontier is therefore 10,000,000 document records plus one durable request
  receipt, or 10,000,001. This is physical journal accounting and does not
  represent an additional document.

## Resource envelope

- Cgroup: `MemoryMax=4,294,967,296`, `SwapMax=0`
- Peak cgroup memory: 4,295,979,008 bytes
- Final/current cgroup memory: 4,259,565,568 bytes
- `memory.events`: `max=2,156`, `oom=0`, `oom_kill=0`, `oom_group_kill=0`
- Peak process working set: 1,335,169,024 bytes
- Peak sampled local storage: 6,860,884,443 bytes
- Minimum free space: 350,359,121,920 bytes
- Free-space floor: 107,374,182,400 bytes
- Swap used: 0 bytes

During remote frontier validation, cgroup memory was mostly inactive file cache
(about 3.98 GiB) with about 0.24 GiB anonymous memory. The run completed without
an OOM or free-space violation, but the repeated `memory.events max` pressure and
near-cap validation footprint are material risks for larger runs.

## Scope

This qualification covers seed ingestion, remote commit journal durability,
frontier validation, local WAL persistence, and resource bounds. It intentionally
skips compaction, segment reopen scans, point reads, updates, deletes, indexed
queries, backup, and restore.

## 100M recommendation

Do not launch 100M from this result alone. At the measured local slope, 100M
would require roughly 68.6 GB of local engine storage and the remote journal
would be roughly 42.2 GB. Seed time is already about 34.4 minutes at 10M and
is declining with scale. More significantly, 10M frontier validation reached the
4 GiB cgroup cap and recorded 2,156 max-pressure events. Run the current-source
1M/A-B write-hotpath audit and resolve or bound frontier-validation cache use
before approving a 100M calibration.
