# Larger-than-memory diagnostic — 2026-09-23

This run used the sequential physical-index compaction patch. It was stopped
after 20 minutes 31 seconds because the first sample had not completed. Its
receipt is **failed** and it does not qualify larger-than-memory operation.

| Measurement | Observation |
| --- | ---: |
| Records and payload | 40,000 × 8,192 bytes, per-key incompressible |
| Estimated logical dataset | 328,800,000 bytes |
| Seeded physical storage | 1,055,277,533 bytes |
| Memory cap and observed peak | 268,435,456 bytes |
| Swap cap and observed peak | 0 bytes |
| OOM / OOM kills | 0 / 0 |
| Cgroup physical reads / writes before stop | 50,545,561,600 / 34,279,264,768 bytes |
| Completed sample lines | 0 |

The [receipt](larger-memory-receipt.json), [cgroup snapshot](cgroup-live-before-cancel.txt),
and [5-minute](live-stack-5m.txt), [10-minute](live-stack-10m.txt), and
[13-minute](live-stack-13m.txt) stacks are preserved. The stacks place time in
cold reopen state-index replay, mixed-operation compressed point reads, and
compaction current-record membership checks. The controlled SIGINT produced
the receipt's `Cancelled by user` result. The cgroup disappeared after exit;
the captured live snapshot is the resource evidence.
