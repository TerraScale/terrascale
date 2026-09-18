# TerraBase vs Turso (libSQL) replication lag — 2026-09-18

Loopback, single-host, same-core-discipline comparison of durable
write→replica-visible latency: TerraBase MagicOnion inter-node replication vs
Turso's libSQL server (sqld) primary→replica replication. Receipt:
`receipts/20260918-terrabase-vs-turso-replication/` (raw JSONL samples,
logs, Turso client source, environment notes).

**This is a loopback benchmark on one host. It is not a WAN, cross-region,
or throughput-saturation claim.**

## Method

Both sides measured the same shape: ~250 B JSON documents, one round trip
per batch (TerraBase: one `ReplicationPushRequest` over authenticated
loopback mTLS HTTP/2 MagicOnion; Turso: one multi-row `INSERT` over HTTP to
the sqld primary), then a tight read-poll of the replica until the last row
is visible. Batch sizes 1 / 16 / 128 records, 5 discarded warmups, 60
samples each, nearest-rank percentiles. Clients pinned to CPUs 14,15 at
`nice -n 19` for both sides, run sequentially (no cross-contamination).

| Item | TerraBase | Turso |
| --- | --- | --- |
| Source | current tree, commit `2351b1c3` (harness built 10:06 before the tree drifted) | — |
| Server | in-process `TerraBaseHost` target + `StorageEngine` source, pinned to 2 cores | sqld 0.24.33 primary + replica (`ghcr.io/tursodatabase/libsql-server` @ `sha256:134f3a46…`), podman pod, inter-node pod-localhost gRPC (plaintext) |
| Client | .NET 10 harness (C#), pinned | Rust, libsql crate 0.9.30, pinned |
| Transport | loopback mTLS HTTP/2 | loopback HTTP (client) + loopback gRPC (replication) |
| Durability of ack | `DurableAcknowledged` — target WAL append before response | sqld 0.24.33 exposes no fsync knobs; ack durability is the build default (unverified — see caveats) |

## Results — send→ack (write acknowledged)

| Batch | TerraBase p50 | TerraBase p95 | Turso p50 | Turso p95 | Faster |
| --- | --- | --- | --- | --- | --- |
| 1 | 3.89 ms | 4.75 ms | 2.77 ms | 3.29 ms | Turso 1.4× |
| 16 | 17.45 ms | 26.94 ms | 2.91 ms | 3.29 ms | Turso 6.0× |
| 128 | 86.20 ms | 134.37 ms | 4.13 ms | 5.24 ms | Turso 20.9× |

## Results — send→visible on the replica (end-to-end)

| Batch | TerraBase p50 | TerraBase p95 | Turso p50 | Turso p95 | Faster |
| --- | --- | --- | --- | --- | --- |
| 1 | 3.93 ms | 4.77 ms | 4.63 ms | 7.18 ms | **TerraBase 1.2×** |
| 16 | 17.52 ms | 26.97 ms | 4.66 ms | 6.28 ms | Turso 3.8× |
| 128 | 86.21 ms | 134.39 ms | 7.49 ms | 9.65 ms | Turso 11.5× |

TerraBase applies each publication into the target read model inline before
the push response returns: visible-on-first-probe 60/60 at every batch size,
ack→visible ≤ 0.03 ms. Turso replicates asynchronously after the commit ack:
ack→visible p50 1.8 / 1.7 / 3.4 ms (mean polls 1.1 / 1.2 / 2.0).

## Reading

- **Single-record end-to-end, TerraBase wins**: 3.93 vs 4.63 ms p50 — its
  apply-before-ack design makes the ack the visibility boundary, while Turso
  adds 1.8 ms of async propagation on top of a (faster) commit ack.
- **Batched writes, Turso wins decisively.** TerraBase's push cost is
  ~0.67 ms/record at batch 128 (17.5 ms → 86.2 ms for 8× the records);
  Turso is nearly flat at ~0.03 ms/record (2.9 → 4.1 ms). TerraBase's
  per-record apply path, not the transport, dominates — the two sides sit on
  the same loopback and the client pinning is identical.
- Current TerraBase is faster than its own 2026-09-15 receipt (p50 5.86 →
  3.89 ms at M=1; 121.6 → 86.2 ms at M=128), consistent with the async-WAL
  work in the current tree.

## Caveats

- **Durability asymmetry (favors Turso)**: TerraBase's ack is a verified WAL
  append (`DurableAcknowledged`, 60/60 per batch). sqld 0.24.33's CLI has no
  fsync/sync-mode knobs and its default ack durability could not be verified;
  if it acks before fsync, the ack-column comparison overstates Turso.
- **CPU asymmetry (favors Turso)**: TerraBase client+source+target were all
  confined to cores 14,15 (matching the 2026-09-15 receipt discipline);
  rootless podman rejected `--cpuset-cpus`, so the two sqld processes ran
  unpinned across the host's 46 CPUs. Only the clients were identically
  pinned.
- **Transport asymmetry (favors Turso)**: TerraBase replicates over mTLS
  HTTP/2; Turso's inter-node channel is plaintext gRPC inside the pod.
- Different client stacks (C#/.NET 10 vs Rust); both loopback single-host;
  neither side measures throughput saturation, WAN latency, or failover.
- TerraBase working tree was dirty at run time; the measured DLL was built
  from commit `2351b1c3` at 10:06 and the tree stopped compiling later in
  the same window (unrelated `DurableIndexStore.cs` edits).

## Reproduction

```bash
# TerraBase: harness copies .codex/swarm/measurement/restore-replication-lag-20260915
#   ProjectReference → terrabase/src/TerraBase/TerraBase.csproj
env DOTNET_PROCESSOR_COUNT=2 DOTNET_ROOT=~/.local/share/mise/dotnet-root \
  taskset -c 14,15 nice -n 19 dotnet <harness>/bin/Release/net10.0/TerraBaseRestoreLagMeasurement.dll \
  lag --samples 60

# Turso: receipt dir has turso-client/ (Cargo.toml + main.rs) and sqld flags
podman pod create --name tursobench -p 8080:8080 -p 8081:8081
podman run -d --pod tursobench --name turso-primary ghcr.io/tursodatabase/libsql-server:latest \
  sqld --db-path /var/lib/sqld/db.sqlite --http-listen-addr 0.0.0.0:8080 --grpc-listen-addr 0.0.0.0:5001
podman run -d --pod tursobench --name turso-replica ghcr.io/tursodatabase/libsql-server:latest \
  sqld --db-path /var/lib/sqld/replica.sqlite --http-listen-addr 0.0.0.0:8081 \
  --primary-grpc-url http://127.0.0.1:5001
taskset -c 14,15 nice -n 19 ./turso-client
```
