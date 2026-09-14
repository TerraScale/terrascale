# TerraBase MagicOnion durable replication

Status: **complete**  
Receipt: `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/20260914-magic-onion-final/receipt.json`  
Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll --workload magic-onion-replication --samples 3 --operations 32 --delay-ms 2 --duplicates true --reorder true --output bench/receipts/20260914-magic-onion-final`

Each sample uses two independent HTTPS/mTLS TerraBase hosts with separate durable directories. Source seeding and stream pull use MagicOnion; delivery to the destination uses one unary MagicOnion push per publication and optional duplicate delivery.

The protocol reports `DurableAcknowledged` in the push response and does not expose a separate server-side durable-ack timestamp. Durable-ack latency therefore uses the same client-observed push response boundary and is labelled accordingly.

| Sample | Ops | Send p50 (us) | Send p95 (us) | Send p99 (us) | Ack p50 (us) | Applied p95 (us) | Repair lag (ms) | Deferred responses | Duplicate outcomes | Final durable | Final applied |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 32 | 3603.8 | 8219.2 | 41354.9 | 3603.8 | 406146.7 | 339.258 | 62 | 1 | 32 | 32 |
| 2 | 32 | 3710.4 | 11395.6 | 41556.6 | 3710.4 | 417729.1 | 372.222 | 62 | 1 | 32 | 32 |
| 3 | 32 | 3865.7 | 16820.4 | 28495.5 | 3865.7 | 425555.6 | 376.823 | 62 | 1 | 32 | 32 |

Unsupported claims and limits:

- This harness measures loopback TLS and local filesystem durability; it is not a WAN, cross-region, or object-storage benchmark.
- Delivery delay is an application-controlled delay between unary RPCs, not injected network latency inside Kestrel or gRPC.
- Duplicate outcomes exercise repeated publication dots with distinct idempotency keys; transport-level packet duplication is not observable from this API.
- Applied-frontier latency is reported only after a response exposes a non-zero applied frontier. Repair lag is unsupported when the selected ordering produces no deferred response.
- The benchmark does not claim crash recovery, partition healing, or throughput saturation; those require separate failure and capacity runs.
