# Failed 256 MiB physical-query attempt

This attempt is retained as a regression and does not contribute a latency row to later runs. The exact runner source is `Program-attempt.cs.txt` (SHA-256 `272e54f76582e9f0201e34b5c14fdb6bca62d42d3090afcbac1c260d8f55d0bb`). `failed-attempt.json` records the command, dataset, cgroup settings, commit IDs, binary hashes and limits; `failure.txt` retains the full exception stack.

| Dataset | Batch | Memory cap | Swap cap | GC heap cap | Outcome | Partial TerraBase file bytes |
| --- | ---: | ---: | ---: | --- | --- | ---: |
| 3,000 base + 100 maintenance, 98,304 raw payload B/document | 100 | 256 MiB | 0 | none explicit | `OutOfMemoryException` in `RuntimeIndexCodec.EncodeDocumentValue` during `status` index seed | 128,698,997 |

The process exited 134. The SQLite `status` lane completed and deleted its disposable files before the failure; no successful raw receipt was written for this attempt. The collected scope no longer exposes its exact peak memory or `memory.events`, so the failure is attributable to the observed `OutOfMemoryException` under the configured cap, without an exact peak claim. A later 25-row/explicit-GC-cap attempt is a **different workload** and must be reported separately.
