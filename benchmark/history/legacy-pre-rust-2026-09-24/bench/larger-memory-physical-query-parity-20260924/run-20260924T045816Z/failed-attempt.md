# Failed 25-row physical-query attempt

This second attempt is a separate failed workload, not a warmup. The exact runner source is `Program-attempt.cs.txt` (SHA-256 `11b70c9ee9f0f35287e94b052ec9d06e98fb33eaf5ebffb5e24e8f86f7810af7`). `failed-attempt.json` records the command, source and binary hashes; `failure.txt` retains both exception stacks.

| Dataset | Batch | Memory cap | Swap cap | GC heap cap | Outcome | Partial TerraBase file bytes |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| 3,000 base + 100 maintenance, 98,304 raw payload B/document | 25 | 256 MiB | 0 | 128 MiB | ZoneTree Zstd compression OOM during a segment merge, followed by document encoding OOM in the `status` index seed | 125,023,683 |

The process exited 134. No success receipt was written. The collected cgroup scope no longer exposes its exact peak memory or events; the same-envelope smoke's peak is explicitly not substituted for this attempt. A later periodic close/reopen variant is a distinct ingestion mode with additional recovery and verification work.
