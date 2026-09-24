# Larger-than-memory compaction diagnostic

The one-sample run was deliberately interrupted after 22 minutes 18 seconds
while its first compaction was still performing repeated compressed physical
index point reads. It is **not** a completed larger-than-memory qualification.

| Measurement | Observation |
| --- | ---: |
| Rows and payload | 40,000 × 8,192 bytes, per-key incompressible |
| Estimated logical dataset | 328,800,000 bytes |
| Seeded physical storage | 1,055,283,074 bytes |
| Cgroup memory cap and peak | 268,435,456 bytes |
| Swap cap and peak | 0 bytes |
| OOM / OOM kills | 0 / 0 |
| Local storage immediately before interruption | 1.5 GiB |
| Completed sample lines | 0 |

The [failed workload receipt](larger-memory-receipt.json),
[cgroup envelope](cgroup-envelope.txt), [11-minute stack](live-stack-11m.txt),
and [21-minute stack](live-stack-21m.txt) are preserved. The current benchmark
binary SHA-256 and TerraBase library SHA-256 are in [binary-sha256.txt](binary-sha256.txt).
The stack samples locate the slow path in `StorageEngine.CompactAsync` through
ZoneTree compressed block lookup and LZ4 decompression. The controlled SIGINT
produced the receipt's `Cancelled by user` status.
