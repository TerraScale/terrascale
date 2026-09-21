# Transport comparison: gRPC vs MagicOnion vs custom RUDP vs QUIC (+LZ4)

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
| `quic` | QUIC via System.Net.Quic (libmsquic): one bidirectional stream per operation over one TLS 1.3 connection. Unsupported on this host — see caveats. |
| `grpc+lz4`, `rudp+lz4`, `quic+lz4` | The grpc/rudp/quic echo paths with client-side LZ4 compression (TerraBase's `LZ4DataCompression`, fastest level) of a synthetic structured-data corpus; servers echo compressed bytes verbatim, clients decompress and verify. The LZ4 ratio is reported per row. |

## Sequential round-trip latency (median of 3 samples)

| Lane | Payload | p50 (µs) | p95 (µs) | p99 (µs) | LZ4 ratio |
|---|---:|---:|---:|---:|---:|
| grpc | 1 KiB | 254.6 | 398.3 | 1 065.0 | — |
| grpc+lz4 | 1 KiB | 256.9 | 336.6 | 632.8 | 0.430 |
| magic-onion | 1 KiB | 287.0 | 411.8 | 868.3 | — |
| rudp | 1 KiB | **83.9** | 109.1 | 173.0 | — |
| quic | 1 KiB | unsupported (host) | | | — |
| rudp+lz4 | 1 KiB | **65.5** | 133.8 | 178.5 | 0.430 |
| quic+lz4 | 1 KiB | unsupported (host) | | | 0.430 |
| grpc | 64 KiB | 696.8 | 874.9 | 1 666.9 | — |
| grpc+lz4 | 64 KiB | **538.0** | 649.5 | 1 353.6 | 0.226 |
| magic-onion | 64 KiB | 760.7 | 946.5 | 1 754.2 | — |
| rudp | 64 KiB | **215.1** | 576.9 | 856.2 | — |
| rudp+lz4 | 64 KiB | 296.4 | 375.8 | 869.3 | 0.226 |
| grpc | 1 MiB | 6 700.2 | 7 604.8 | 10 790.9 | — |
| grpc+lz4 | 1 MiB | **5 380.1** | 6 519.3 | 8 666.8 | 0.212 |
| magic-onion | 1 MiB | 6 323.1 | 9 535.9 | 13 449.7 | — |
| rudp | 1 MiB | **3 113.2** | 4 777.6 | 6 668.9 | — |
| rudp+lz4 | 1 MiB | 4 830.8 | 5 630.7 | 7 997.1 | 0.212 |
| grpc | 4 MiB | 18 952.4 | 27 782.9 | 31 738.0 | — |
| grpc+lz4 | 4 MiB | **18 366.5** | 23 603.6 | 24 878.3 | 0.212 |
| magic-onion | 4 MiB | 24 809.6 | 35 299.5 | 37 347.2 | — |
| rudp | 4 MiB | **11 700.6** | 14 816.5 | 15 373.9 | — |
| rudp+lz4 | 4 MiB | 18 655.0 | 20 811.1 | 24 283.1 | 0.212 |

## Closed-loop throughput (median of 3 samples; 16 workers ≤64 KiB, 4 workers above)

| Lane | Payload | Ops/s | Payload MB/s | Retransmits | LZ4 ratio |
|---|---:|---:|---:|---:|---:|
| grpc | 1 KiB | 31 608 | 30.9 | 0 | — |
| grpc+lz4 | 1 KiB | 31 673 | 30.9 | 0 | 0.430 |
| magic-onion | 1 KiB | 27 282 | 26.6 | 0 | — |
| rudp | 1 KiB | 44 502 | 43.5 | 0 | — |
| rudp+lz4 | 1 KiB | **47 785** | 46.7 | 0 | 0.430 |
| grpc | 64 KiB | 3 661 | 228.8 | 0 | — |
| grpc+lz4 | 64 KiB | 12 230 | **764.4** | 0 | 0.226 |
| magic-onion | 64 KiB | 3 332 | 208.3 | 0 | — |
| rudp | 64 KiB | 6 668 | 416.7 | 0 | — |
| rudp+lz4 | 64 KiB | **16 867** | **1 054.2** | 0 | 0.226 |
| grpc | 1 MiB | 268 | 268.4 | 0 | — |
| grpc+lz4 | 1 MiB | 618 | 618.5 | 0 | 0.212 |
| magic-onion | 1 MiB | 263 | 262.8 | 0 | — |
| rudp | 1 MiB | 392 | 391.8 | 0 | — |
| rudp+lz4 | 1 MiB | **650** | 649.8 | 0 | 0.212 |
| grpc | 4 MiB | 71 | 282.5 | 0 | — |
| grpc+lz4 | 4 MiB | 155 | 620.8 | 0 | 0.212 |
| magic-onion | 4 MiB | 65 | 259.7 | 0 | — |
| rudp | 4 MiB | 57 | 229.1 | 112 | — |
| rudp+lz4 | 4 MiB | **209** | **836.4** | 0 | 0.212 |

Payload MB/s counts one-way request bytes (the lz4 rows count the
uncompressed corpus size actually delivered per operation).

## Raw one-way bulk throughput (server → client, 128 MiB total)

| Lane | Chunk | MB/s | Retransmits |
|---|---:|---:|---:|
| grpc | 65 536 | 440.9 | 0 |
| magic-onion | 65 536 | 407.6 | 0 |
| rudp | 61 440 | **1 707.8** | 0 |
| quic | — | unsupported (host) | n/a |

## Reading the numbers

- The custom RUDP protocol wins latency at every payload (2.5–3.7× vs gRPC) and throughput up to 1 MiB; at 4 MiB its whole-fragment-set retransmit policy loses receive-buffer overflows under concurrent bursts (112 retransmits) and slides to near-parity with the HTTP/2 lanes.
- LZ4 dominates the throughput story: `rudp+lz4` delivers 933 MB/s effective at 64 KiB (wire carries 22.6% of the bytes) and 763 MB/s at 4 MiB — 3.2× the best uncompressed lane. The 1 KiB corpus barely compresses (ratio 0.43) and codec CPU offsets the wire saving.
- `grpc+lz4` multiplies plain gRPC throughput by 3.3× at 64 KiB, 2.3× at 1 MiB, and 2.2× at 4 MiB (764 MB/s vs 229 at 64 KiB), and roughly halves its p99 tails; at 1 KiB it is a wash (ratio 0.43, codec CPU offsets the wire saving). With LZ4 on both, gRPC and the custom RUDP converge to the same effective throughput at ≥1 MiB — the wire bytes, not the transport, set the limit.
- MagicOnion costs ~8–22% throughput and ~9–50% p50 latency versus plain gRPC at the same payload.

## Unsupported claims and limits

- Loopback in one process: this measures RPC stack overhead (framing, marshalling, dispatch), not physical networks, WAN latency, or TLS; production peer traffic adds mTLS equally to the HTTP/2 lanes.
- The quic/quic+lz4 lanes are fully implemented (System.Net.Quic, per-op bidirectional streams over one TLS 1.3 connection, throwaway ECDSA certificate, preflight-checked) but report unsupported on this host: System.Net.Quic cannot complete its TLS 1.3 handshake with any locally built libmsquic (2.2.7, 2.5.8, 2.6.1; static and dynamic OpenSSL; on NixOS and in a Debian container; on .NET 10.0.12 and 11 RC) — the server aborts with a `UserCanceled` alert the moment its per-connection credential applies, while msquic's own interop suite passes on the same builds (231/231 core tests, handshake tests green standalone). Microsoft's packaged libmsquic is unreachable from this network (packages.msquic.org and mirrors are DNS-blocked). On any host with a functioning libmsquic the lanes run unchanged and their numbers would include TLS 1.3 costs the cleartext lanes do not pay.
- The RUDP lane is stop-and-wait per request with client-side retransmit; payloads above one datagram are retransmitted as whole fragment sets (60 000-byte fragments); no ordering guarantees or congestion control. Both RUDP sockets use 4 MiB buffers; retransmits in the receipt are loopback receive-queue overflows under concurrent bursts, recovered by the protocol.
- The lz4 lanes measure a compressible synthetic corpus (their payload content differs from the uncompressed lanes by design); compression and decompression run at the client inside the measured windows; servers never see plaintext.
- Bulk direction is server → client; the rudp bulk protocol is batched stop-and-wait (64-frame batches, 1 s ack timeout); throughput workers share one connection (HTTP/2 lanes) or one socket (UDP lanes).
- Server processing is a pure echo, so differences isolate transport cost, not application or storage work. Workstation GC and background machine load shape the tails; sample medians mitigate but do not eliminate that noise.
