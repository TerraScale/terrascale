# TerraBase transport comparison: gRPC vs MagicOnion vs custom RUDP vs KCP

Status: **complete**  
Receipt: `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/transport-comparison-large/receipt.json`  
Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks --workload transport-comparison --samples 3 --warmup-operations 30 --latency-operations 100 --throughput-operations 300 --throughput-workers 4 --payload-bytes 1048576,4194304 --bulk-bytes 33554432 --output bench/receipts/transport-comparison-large`

Commit: `290a76b2616b22cc1f69f100a730b8bdf9e5415b` (dirty: `True`)
Benchmark artifact SHA-256: `cf74b9a97490dccd846aaff40bebcc26354136c810217464e7659c235fe6589a`
Runtime: .NET 11.0.0-rc.1.26425.128; 11.0.0; NixOS 26.05 (Yarara); X64; build `Release`
CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20,937,965,568 bytes
GC: server `False`, latency mode `Interactive`

Lanes: plain gRPC unary over cleartext HTTP/2 (identity byte[] marshaller, no protobuf codegen); MagicOnion 7 unary over the same stack (adds its MessagePack marshalling and RPC layer); a custom reliable-UDP protocol (12-byte correlation header over UDP datagrams, client retransmit with exponential backoff); QUIC (System.Net.Quic over libmsquic): one bidirectional stream per operation multiplexed over one TLS 1.3 connection — the only lane whose wire is always encrypted. The grpc+lz4, rudp+lz4 and quic+lz4 lanes reuse those echo paths unchanged but send a synthetic structured-data corpus compressed with LZ4 at the client, then decompress and verify the echo against the original corpus; their LZ4 ratio (compressed/original bytes) is reported per row. All lanes echo the same payload over loopback sockets in one process; each table value is the median across samples.

## Sequential round-trip latency

| Lane | Payload (bytes) | p50 (us) | p95 (us) | p99 (us) | Mean (us) | LZ4 ratio |
|---|---:|---:|---:|---:|---:|---:|
| grpc | 1048576 | 6700.2 | 7604.8 | 10790.9 | 6794.8 | n/a |
| grpc+lz4 | 1048576 | 5380.1 | 6519.3 | 8666.8 | 5544.1 | 0.21 |
| magic-onion | 1048576 | 6323.1 | 9535.9 | 13449.7 | 7467.1 | n/a |
| rudp | 1048576 | 3113.2 | 4777.6 | 6668.9 | 3449.9 | n/a |
| quic | 1048576 | unsupported | unsupported | unsupported | unsupported | n/a |
| rudp+lz4 | 1048576 | 4830.8 | 5630.7 | 7997.1 | 4597.3 | 0.21 |
| quic+lz4 | 1048576 | unsupported | unsupported | unsupported | unsupported | 0.21 |
| grpc | 4194304 | 18952.4 | 27782.9 | 31738.0 | 19889.1 | n/a |
| grpc+lz4 | 4194304 | 18366.5 | 23603.6 | 24878.3 | 19447.0 | 0.21 |
| magic-onion | 4194304 | 24809.6 | 35299.5 | 37347.2 | 26703.1 | n/a |
| rudp | 4194304 | 11700.6 | 14816.5 | 15373.9 | 11575.2 | n/a |
| quic | 4194304 | unsupported | unsupported | unsupported | unsupported | n/a |
| rudp+lz4 | 4194304 | 18655.0 | 20811.1 | 24283.1 | 18585.4 | 0.21 |
| quic+lz4 | 4194304 | unsupported | unsupported | unsupported | unsupported | 0.21 |

## Closed-loop throughput

Concurrent workers: 4; operations per lane and payload: 300.

| Lane | Payload (bytes) | Ops/s | Payload MB/s | Retransmits | LZ4 ratio |
|---|---:|---:|---:|---:|---:|
| grpc | 1048576 | 268 | 268.4 | 0 | n/a |
| grpc+lz4 | 1048576 | 618 | 618.5 | 0 | 0.21 |
| magic-onion | 1048576 | 263 | 262.8 | 0 | n/a |
| rudp | 1048576 | 392 | 391.8 | 0 | n/a |
| quic | 1048576 | unsupported | unsupported | n/a | n/a |
| rudp+lz4 | 1048576 | 650 | 649.8 | 0 | 0.21 |
| quic+lz4 | 1048576 | unsupported | unsupported | n/a | 0.21 |
| grpc | 4194304 | 71 | 282.5 | 0 | n/a |
| grpc+lz4 | 4194304 | 155 | 620.8 | 0 | 0.21 |
| magic-onion | 4194304 | 65 | 259.7 | 0 | n/a |
| rudp | 4194304 | 57 | 229.1 | 91 | n/a |
| quic | 4194304 | unsupported | unsupported | n/a | n/a |
| rudp+lz4 | 4194304 | 209 | 836.4 | 0 | 0.21 |
| quic+lz4 | 4194304 | unsupported | unsupported | n/a | 0.21 |

## Raw one-way bulk throughput

Server-to-client one-way transfer of 33,554,432 bytes per lane in 65,536-byte frames (RUDP frames capped at 61,440 bytes); one 1 MiB warm pass per lane is unmeasured.

| Lane | Chunk (bytes) | Frames | MB/s | Retransmits |
|---|---:|---:|---:|---:|
| rudp | 61440 | 547 | 1785.9 | 0 |
| grpc | 65536 | 512 | 409.7 | 0 |
| magic-onion | 65536 | 512 | 347.3 | 0 |
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
