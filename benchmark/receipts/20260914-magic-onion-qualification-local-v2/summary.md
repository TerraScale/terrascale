# TerraBase MagicOnion replication qualification

Status: **complete**  
Receipt: `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/20260914-magic-onion-qualification-local-v2/receipt.json`  
Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload magic-onion-replication-qualification --operations 4 --output bench/receipts/20260914-magic-onion-qualification-local-v2`

Topology: the driver launched two independent TerraBase benchmark child processes. `member-a` and `member-b` each ran a real HTTPS/HTTP2 MagicOnion peer host with mTLS admission and separate storage-engine WAL directories.
Seed: database `01ARZ3NDEKTSV4RRFFQ69G5FAV`, tenant `01ARZ3NDEKTSV4RRFFQ69G5FB0`, origin `member-a`, collection `magic-onion-qualification`, sequences `4`, body SHA-256 `70b168a68a99e3920e742e370c2c43bef7beae0f34e1bd0b67f15e9c4c703ea6`.

| Seed durable | Seed applied | Reordered deferred | Duplicate outcomes | Final durable | Final applied | Heartbeat applied | Repair lag (ms) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| True | 4 | 6 | 3 | 4 | 4 | 4 | 2207.245 |

The partition is represented by a stopped destination process and a failed authenticated push, followed by a restart over the same destination directory. Reordering and duplicates are driver-controlled unary RPC deliveries. Anti-entropy uses a real source `StreamAsync` journal pull and replays rows against the destination frontier; the retained raw event log records every outcome.

Retained artifacts include both node storage directories, child stdout/stderr logs, certificates and fingerprints, `raw-events.jsonl`, and this receipt. The run does not claim WAN packet loss, cross-region latency, or production deployment behavior.

Next diagnostic step: Repeat this exact receipt under the project VM or Lightcracker network-fault harness, preserving the same seed and frontier assertions, before any OCI run.
