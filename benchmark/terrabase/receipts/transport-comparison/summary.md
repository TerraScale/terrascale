# TerraBase transport comparison: gRPC vs MagicOnion vs custom RUDP

Status: **complete**  
Receipt: `/home/mariogk/Projects/TerraScale/terrabase/bench/receipts/transport-comparison/receipt.json`  
Command: `/home/mariogk/Projects/TerraScale/terrabase/bench/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks --workload transport-comparison --output bench/receipts/transport-comparison`

Commit: `9bbddbef31e189e6294521a2410b30b45b9c36d2` (dirty: `True`)
Benchmark artifact SHA-256: `219a7d98d0a5598833ef049469723b3b32270f44e992ad1983a86da25a555ca5`
Runtime: .NET 11.0.0-rc.1.26425.128; 11.0.0; NixOS 26.05 (Yarara); X64; build `Release`
CPU: Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz; 46 logical processors; available memory 20,937,965,568 bytes
GC: server `False`, latency mode `Interactive`

Lanes: plain gRPC unary over cleartext HTTP/2 (identity byte[] marshaller, no protobuf codegen); MagicOnion 7 unary over the same stack (adds its MessagePack marshalling and RPC layer); a custom reliable-UDP protocol (12-byte correlation header over UDP datagrams, client retransmit with exponential backoff). All lanes echo the same payload over loopback sockets in one process; each table value is the median across samples.

## Sequential round-trip latency

| Lane | Payload (bytes) | p50 (us) | p95 (us) | p99 (us) | Mean (us) |
|---|---:|---:|---:|---:|---:|
| grpc | 64 | 246.9 | 328.7 | 375.3 | 256.4 |
| magic-onion | 64 | 278.9 | 332.6 | 490.5 | 281.9 |
| rudp | 64 | 54.5 | 83.4 | 116.5 | 58.8 |
| grpc | 1024 | 292.1 | 347.2 | 372.1 | 292.5 |
| magic-onion | 1024 | 266.3 | 328.7 | 373.7 | 276.2 |
| rudp | 1024 | 83.2 | 127.0 | 167.1 | 88.8 |
| grpc | 16384 | 348.5 | 434.3 | 471.4 | 358.6 |
| magic-onion | 16384 | 343.2 | 432.3 | 1630.5 | 363.1 |
| rudp | 16384 | 84.6 | 128.9 | 299.6 | 95.7 |
| grpc | 61440 | 496.4 | 610.1 | 1269.3 | 518.1 |
| magic-onion | 61440 | 528.2 | 653.5 | 1415.1 | 565.8 |
| rudp | 61440 | 161.3 | 256.3 | 770.2 | 183.0 |

## Closed-loop throughput

Concurrent workers: 16; operations per lane and payload: 4000.

| Lane | Payload (bytes) | Ops/s | Payload MB/s | Retransmits |
|---|---:|---:|---:|---:|
| grpc | 64 | 37234 | 2.3 | 0 |
| magic-onion | 64 | 32535 | 2.0 | 0 |
| rudp | 64 | 54281 | 3.3 | 0 |
| grpc | 1024 | 35676 | 34.8 | 0 |
| magic-onion | 1024 | 30571 | 29.9 | 0 |
| rudp | 1024 | 40866 | 39.9 | 0 |
| grpc | 16384 | 14436 | 225.6 | 0 |
| magic-onion | 16384 | 12569 | 196.4 | 0 |
| rudp | 16384 | 26509 | 414.2 | 0 |
| grpc | 61440 | 5145 | 301.5 | 0 |
| magic-onion | 61440 | 4306 | 252.3 | 0 |
| rudp | 61440 | 12450 | 729.5 | 0 |

Unsupported claims and limits:

- Loopback in one process: this measures the RPC stack overhead (framing, marshalling, dispatch), not physical network, WAN latency, or TLS; production peer traffic adds mTLS equally to both HTTP/2 lanes.
- The RUDP lane is stop-and-wait per request with client-side retransmit (initial timeout 50ms, exponential backoff); it has no fragmentation (payloads are capped below the UDP datagram limit), no ordering guarantees, and no congestion control. Both RUDP sockets use 4 MiB buffers; retransmits in the receipt are loopback receive-queue overflows under concurrent bursts, recovered by the protocol.
- Throughput workers share one connection (gRPC/MagicOnion HTTP/2 multiplexing) or one socket (RUDP); multi-connection scaling is not measured.
- Server processing is a pure echo, so differences isolate transport cost, not application or storage work.
- Workstation GC and the machine's background load shape the tails; samples mediate but do not eliminate that noise.
