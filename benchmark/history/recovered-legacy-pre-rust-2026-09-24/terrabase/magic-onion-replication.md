# MagicOnion durable replication benchmark

Run the source-matched two-host harness from `terrabase/` with a Release build:

```text
nice -n 19 dotnet run --project bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release --no-build -- \
  --workload magic-onion-replication --samples 3 --operations 32 --delay-ms 2 \
  --duplicates true --reorder true --output bench/receipts/magic-onion-replication
```

The workload starts two independent `TerraBaseHost` instances. Each host has
its own storage directory, server certificate, and mTLS peer admission. The
source seed and stream pull use the real MagicOnion service; each reordered or
duplicated publication is delivered to the destination by a real unary push
RPC. Every publication is a causal chain, so reverse delivery creates deferred
rows that can be measured until the destination's applied frontier converges.

| Measurement | Meaning |
|---|---|
| Send p50/p95/p99 | Client observed unary push round-trip latency in microseconds. |
| Durable ack p50/p95/p99 | The same push round-trip subset where `DurableAcknowledged` was true. The protocol has no separate server timestamp. |
| Applied frontier p50/p95/p99 | Client elapsed time when a response first exposed a non-zero applied frontier. |
| Repair lag | Time from the first deferred response until the destination applied frontier reached the final source sequence. |
| Duplicate outcomes | Repeated publication dots accepted by the destination as duplicates. |
| Final durable/applied frontier | Independent destination runtime state checked after delivery. |

The output directory contains `receipt.json`, `raw-samples.jsonl`, and
`summary.md`. The harness intentionally does not claim WAN latency, packet
duplication, crash recovery, partition healing, object-storage durability, or
throughput saturation. `--delay-ms` delays delivery between RPCs in the
driver; it does not inject latency into Kestrel or gRPC. When reordering is
disabled and no response is deferred, repair lag is reported as unsupported.
