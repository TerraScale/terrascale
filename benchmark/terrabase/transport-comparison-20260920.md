# Transport comparison: gRPC vs MagicOnion vs custom RUDP vs KCP (+LZ4)

Run the six-lane echo harness from `terrabase/` with a Release build. The
payload set is split into two runs so large payloads get measured with
reduced operation counts:

```text
nice -n 19 dotnet run --project bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release --no-build -- \
  --workload transport-comparison --samples 3 --payload-bytes 1024,65536 \
  --output bench/receipts/transport-comparison-small

nice -n 19 dotnet run --project bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release --no-build -- \
  --workload transport-comparison --samples 3 --warmup-operations 30 --latency-operations 100 \
  --throughput-operations 300 --throughput-workers 4 --payload-bytes 1048576,4194304 \
  --bulk-bytes 33554432 --output bench/receipts/transport-comparison-large
```

Each table value is the median across three samples (lane order rotated per
sample). Receipts: `bench/receipts/transport-comparison-{small,large}/`
(`receipt.json`, `summary.md`, `raw-samples.jsonl`). Every echoed response is
byte-verified.

## Lanes

| Lane | Stack |
|---|---|
| `grpc` | Plain gRPC unary over cleartext HTTP/2 (Kestrel), hand-written `Method<byte[], byte[]>` with the identity marshaller — no protobuf codegen. |
| `magic-onion` | MagicOnion 7.10.2 unary (`ServiceBase`/`IService<>`) over the same stack, adding its MessagePack marshalling and RPC layer. |
| `rudp` | Custom reliable-UDP protocol: 12-byte correlation header over UDP datagrams, client retransmit (50 ms initial, exponential backoff), fragmentation into 60 000-byte fragments above one datagram. |
| `kcp` | Cysharp KcpTransport 0.0.2 (alpha) — pure C# KCP, length-prefixed frames with op byte and correlation id over one reliable stream, library defaults. |
| `rudp+lz4`, `kcp+lz4` | The rudp/kcp echo paths with client-side LZ4 compression (TerraBase's `LZ4DataCompression`, fastest level) of a synthetic structured-data corpus; servers echo compressed bytes verbatim, clients decompress and verify. The LZ4 ratio is reported per row. |

## Sequential round-trip latency (median of 3 samples)

| Lane | Payload | p50 (µs) | p95 (µs) | p99 (µs) | LZ4 ratio |
|---|---:|---:|---:|---:|---:|
| grpc | 1 KiB | 224.4 | 295.4 | 326.6 | — |
| magic-onion | 1 KiB | 262.2 | 372.3 | 534.9 | — |
| rudp | 1 KiB | 54.9 | 85.9 | 113.8 | — |
| kcp | 1 KiB | 79.7 | 130.3 | 154.4 | — |
| rudp+lz4 | 1 KiB | 62.8 | 100.8 | 118.1 | 0.430 |
| kcp+lz4 | 1 KiB | 88.6 | 119.8 | 137.1 | 0.430 |
| grpc | 64 KiB | 702.6 | 851.7 | 1715.3 | — |
| magic-onion | 64 KiB | 745.6 | 1126.1 | 2185.8 | — |
| rudp | 64 KiB | 230.8 | 437.4 | 887.9 | — |
| kcp | 64 KiB | unsupported | | | |
| rudp+lz4 | 64 KiB | 301.2 | 366.0 | 861.1 | 0.226 |
| kcp+lz4 | 64 KiB | 563.2 | 698.1 | 1303.6 | 0.226 |
| grpc | 1 MiB | 5 376.6 | 6 877.8 | 10 601.1 | — |
| magic-onion | 1 MiB | 6 171.4 | 10 813.1 | 13 250.6 | — |
| rudp | 1 MiB | 3 047.8 | 4 835.3 | 7 260.8 | — |
| rudp+lz4 | 1 MiB | 4 926.9 | 6 229.6 | 10 209.9 | 0.212 |
| grpc | 4 MiB | 18 690.2 | 24 176.7 | 25 579.7 | — |
| magic-onion | 4 MiB | 24 046.8 | 34 850.1 | 39 609.6 | — |
| rudp | 4 MiB | 11 551.2 | 15 161.8 | 18 586.4 | — |
| rudp+lz4 | 4 MiB | 19 256.9 | 22 996.3 | 26 865.9 | 0.212 |

## Closed-loop throughput (median of 3 samples; 16 workers ≤64 KiB, 4 workers above)

| Lane | Payload | Ops/s | Payload MB/s | Retransmits | LZ4 ratio |
|---|---:|---:|---:|---:|---:|
| grpc | 1 KiB | 34 463 | 33.7 | 0 | — |
| magic-onion | 1 KiB | 25 472 | 24.9 | 0 | — |
| rudp | 1 KiB | 45 166 | 44.1 | 0 | — |
| kcp | 1 KiB | 21 017 | 20.5 | n/a | — |
| rudp+lz4 | 1 KiB | 55 519 | 54.2 | 0 | 0.430 |
| kcp+lz4 | 1 KiB | 19 995 | 19.5 | n/a | 0.430 |
| grpc | 64 KiB | 3 745 | 234.1 | 0 | — |
| magic-onion | 64 KiB | 3 390 | 211.8 | 0 | — |
| rudp | 64 KiB | 6 597 | 412.3 | 0 | — |
| rudp+lz4 | 64 KiB | 17 669 | 1 104.3 | 0 | 0.226 |
| kcp+lz4 | 64 KiB | 2 901 | 181.3 | n/a | 0.226 |
| grpc | 1 MiB | 268 | 268.3 | 0 | — |
| magic-onion | 1 MiB | 254 | 254.1 | 0 | — |
| rudp | 1 MiB | 354 | 353.7 | 0 | — |
| rudp+lz4 | 1 MiB | 653 | 652.8 | 0 | 0.212 |
| grpc | 4 MiB | 71 | 285.5 | 0 | — |
| magic-onion | 4 MiB | 66 | 262.9 | 0 | — |
| rudp | 4 MiB | 47 | 187.7 | 82 | — |
| rudp+lz4 | 4 MiB | 209 | 834.9 | 0 | 0.212 |

Payload MB/s counts one-way request bytes (the lz4 rows count the
uncompressed corpus size actually delivered per operation).

## Raw one-way bulk throughput (server → client, 128 MiB and 32 MiB totals)

| Lane | Chunk | MB/s | Retransmits |
|---|---:|---:|---:|
| grpc | 65 536 | 439.2 | 0 |
| magic-onion | 65 536 | 403.8 | 0 |
| rudp | 61 440 | 1 769.4 | 0 |
| kcp | 61 400 | 4.0 | n/a |

## Reading the numbers

- The custom RUDP protocol wins latency at every payload (2.4–4.1× vs gRPC) and throughput up to 1 MiB; at 4 MiB its whole-fragment-set retransmit policy loses receive-buffer overflows under concurrent bursts (82 retransmits) and drops below the HTTP/2 lanes.
- LZ4 changes the picture completely for UDP-based transports: `rudp+lz4` delivers 1 104 MB/s effective at 64 KiB (wire carries only 22.6% of the bytes) and 835 MB/s at 4 MiB — 2.9× the best uncompressed lane. The 1 KiB corpus barely compresses (ratio 0.43) and codec CPU dominates any wire saving.
- MagicOnion costs ~10–30% throughput and ~5–20% p50 latency versus plain gRPC at the same payload, and its p99 tails run 1.6–2× worse.
- KCP (alpha KcpTransport 0.0.2, library defaults) is competitive at 1 KiB (79.7 µs p50) but its 32-segment send window with 10 ms-class update pacing caps it near 4 MB/s in bulk; library defects cap payloads at one ~61.4 KiB frame.

## Unsupported claims and limits

- Loopback in one process: this measures RPC stack overhead (framing, marshalling, dispatch), not physical networks, WAN latency, or TLS; production peer traffic adds mTLS equally to both HTTP/2 lanes.
- KcpTransport 0.0.2 is an alpha preview: its stream reader faults internally once a connection carries multi-frame payloads, so the kcp lanes cap payloads at what fits one ~61.4 KiB frame and report larger payload sizes as unsupported; its bulk rate also reflects library defaults (no tuning applied).
- The RUDP lane is stop-and-wait per request with client-side retransmit; payloads above one datagram are retransmitted as whole fragment sets (60 000-byte fragments); no ordering guarantees or congestion control. Both RUDP sockets use 4 MiB buffers; retransmits in the receipt are loopback receive-queue overflows under concurrent bursts, recovered by the protocol.
- The lz4 lanes measure a compressible synthetic corpus (their payload content differs from the uncompressed lanes by design); compression and decompression run at the client inside the measured windows; servers never see plaintext.
- Bulk direction is server → client; the rudp bulk protocol is batched stop-and-wait (64-frame batches, 1 s ack timeout); throughput workers share one connection (HTTP/2 lanes) or one socket (UDP lanes).
- Server processing is a pure echo, so differences isolate transport cost, not application or storage work. Workstation GC and background machine load shape the tails; sample medians mitigate but do not eliminate that noise.
