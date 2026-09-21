# TerraBase transport comparison: gRPC vs MagicOnion vs custom RUDP vs KCP

Status: **complete**  
Receipt: `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/transport-comparison-small/receipt.json`  
Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks --workload transport-comparison --samples 3 --payload-bytes 1024,65536 --output bench/receipts/transport-comparison-small`

Commit: `290a76b2616b22cc1f69f100a730b8bdf9e5415b` (dirty: `True`)
Benchmark artifact SHA-256: `cf74b9a97490dccd846aaff40bebcc26354136c810217464e7659c235fe6589a`
Runtime: .NET 11.0.0-rc.1.26425.128; 11.0.0; NixOS 26.05 (Yarara); X64; build `Release`
CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20,937,965,568 bytes
GC: server `False`, latency mode `Interactive`

Lanes: plain gRPC unary over cleartext HTTP/2 (identity byte[] marshaller, no protobuf codegen); MagicOnion 7 unary over the same stack (adds its MessagePack marshalling and RPC layer); a custom reliable-UDP protocol (12-byte correlation header over UDP datagrams, client retransmit with exponential backoff); QUIC (System.Net.Quic over libmsquic): one bidirectional stream per operation multiplexed over one TLS 1.3 connection — the only lane whose wire is always encrypted. The grpc+lz4, rudp+lz4 and quic+lz4 lanes reuse those echo paths unchanged but send a synthetic structured-data corpus compressed with LZ4 at the client, then decompress and verify the echo against the original corpus; their LZ4 ratio (compressed/original bytes) is reported per row. All lanes echo the same payload over loopback sockets in one process; each table value is the median across samples.

## Sequential round-trip latency

| Lane | Payload (bytes) | p50 (us) | p95 (us) | p99 (us) | Mean (us) | LZ4 ratio |
|---|---:|---:|---:|---:|---:|---:|
| grpc | 1024 | 254.6 | 398.3 | 1065.0 | 271.2 | n/a |
| grpc+lz4 | 1024 | 256.9 | 336.6 | 632.8 | 285.1 | 0.43 |
| magic-onion | 1024 | 287.0 | 411.8 | 868.3 | 328.0 | n/a |
| rudp | 1024 | 83.9 | 109.1 | 173.0 | 84.6 | n/a |
| quic | 1024 | unsupported | unsupported | unsupported | unsupported | n/a |
| rudp+lz4 | 1024 | 65.5 | 133.8 | 178.5 | 115.0 | 0.43 |
| quic+lz4 | 1024 | unsupported | unsupported | unsupported | unsupported | 0.43 |
| grpc | 65536 | 696.8 | 874.9 | 1666.9 | 734.9 | n/a |
| grpc+lz4 | 65536 | 538.0 | 649.5 | 1353.6 | 563.8 | 0.23 |
| magic-onion | 65536 | 760.7 | 946.5 | 1754.2 | 800.1 | n/a |
| rudp | 65536 | 215.1 | 576.9 | 856.2 | 244.7 | n/a |
| quic | 65536 | unsupported | unsupported | unsupported | unsupported | n/a |
| rudp+lz4 | 65536 | 296.4 | 375.8 | 869.3 | 315.3 | 0.23 |
| quic+lz4 | 65536 | unsupported | unsupported | unsupported | unsupported | 0.23 |

## Closed-loop throughput

Concurrent workers: 16; operations per lane and payload: 4000.

| Lane | Payload (bytes) | Ops/s | Payload MB/s | Retransmits | LZ4 ratio |
|---|---:|---:|---:|---:|---:|
| grpc | 1024 | 31608 | 30.9 | 0 | n/a |
| grpc+lz4 | 1024 | 31673 | 30.9 | 0 | 0.43 |
| magic-onion | 1024 | 27282 | 26.6 | 0 | n/a |
| rudp | 1024 | 44502 | 43.5 | 0 | n/a |
| quic | 1024 | unsupported | unsupported | n/a | n/a |
| rudp+lz4 | 1024 | 47785 | 46.7 | 0 | 0.43 |
| quic+lz4 | 1024 | unsupported | unsupported | n/a | 0.43 |
| grpc | 65536 | 3661 | 228.8 | 0 | n/a |
| grpc+lz4 | 65536 | 12230 | 764.4 | 0 | 0.23 |
| magic-onion | 65536 | 3332 | 208.3 | 0 | n/a |
| rudp | 65536 | 6668 | 416.7 | 0 | n/a |
| quic | 65536 | unsupported | unsupported | n/a | n/a |
| rudp+lz4 | 65536 | 16867 | 1054.2 | 0 | 0.23 |
| quic+lz4 | 65536 | unsupported | unsupported | n/a | 0.23 |

## Raw one-way bulk throughput

Server-to-client one-way transfer of 134,217,728 bytes per lane in 65,536-byte frames (RUDP frames capped at 61,440 bytes); one 1 MiB warm pass per lane is unmeasured.

| Lane | Chunk (bytes) | Frames | MB/s | Retransmits |
|---|---:|---:|---:|---:|
| rudp | 61440 | 2185 | 1707.8 | 0 |
| grpc | 65536 | 2048 | 440.9 | 0 |
| magic-onion | 65536 | 2048 | 407.6 | 0 |
| quic | 65536 | 0 | unsupported | n/a |

Bulk caveats:

- Direction is server to client: each lane times one bulk request from send to last verified byte; nothing echoes back, and connect plus the warm pass are excluded.
- The RUDP bulk protocol is batched stop-and-wait: the server sends 64-frame batches and waits up to one second for a batch ack, then resends the batch; the client re-sends its ack when nothing arrives for one second. Bulk frames are capped at 61,440 bytes (larger requested chunks are clamped, so the lane's chunk column shows the effective frame size), and its Retransmits column counts server batch resends plus client ack re-sends.
- The KCP lane runs at library defaults; its send window and update interval bound the transfer rate (measured at roughly 4 MB/s with 64 KiB frames, so the lane runs the full configured total uncapped), and KCP retransmission stays internal to the library (Retransmits n/a).
- The gRPC and MagicOnion lanes bulk over one HTTP/2 server-streaming call each, the same single connection their RPC phases use.

Unsupported claims and limits:

- Loopback in one process: this measures the RPC stack overhead (framing, marshalling, dispatch), not physical network, WAN latency, or TLS; production peer traffic adds mTLS equally to both HTTP/2 lanes.
- The RUDP lane is stop-and-wait per request with client-side retransmit (initial timeout 50ms, exponential backoff); payloads above one datagram are retransmitted as whole fragment sets, with 60000-byte fragments. No ordering guarantees or congestion control. Both RUDP sockets use 4 MiB buffers; retransmits in the receipt are loopback receive-queue overflows under concurrent bursts, recovered by the protocol.
- The QUIC lane always encrypts (TLS 1.3 with a throwaway ECDSA certificate): it pays crypto on every byte while the other lanes run cleartext, and production peer traffic would add mTLS to them equally. Loss recovery is internal to QUIC and not observable, so the Retransmits column reports n/a; the lane requires libmsquic at runtime.
- The +lz4 lanes differ from the uncompressed lanes by design: wire bytes are the compressed frame, not the corpus, so Payload MB/s counts corpus bytes that never crossed the wire uncompressed; the LZ4 codec CPU is client-side and inside every measured window; and the servers never decompress, they echo the compressed bytes verbatim. The corpus compresses below the RUDP datagram cap by construction and the harness fails loudly if a size ever stops fitting. The 64-byte corpora barely compress, so LZ4 frame overhead can leave that ratio at or slightly above 1.0.
- Throughput workers share one connection (gRPC/MagicOnion HTTP/2 multiplexing), one socket (RUDP), or one stream (KCP); multi-connection scaling is not measured.
- Server processing is a pure echo, so differences isolate transport cost, not application or storage work.
- Workstation GC and the machine's background load shape the tails; samples mediate but do not eliminate that noise.
