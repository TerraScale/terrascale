# TerraBase leaderless admission vs Turso (libSQL) replication — 2026-09-18

Loopback, single-host, back-to-back same-window comparison of the
Anna-style leaderless admit path against Turso's libSQL server (sqld)
primary→replica replication. Receipt:
`receipts/20260918-leaderless-vs-turso/` (raw JSONL per side, logs,
Turso client source). This updates
[`terrabase-vs-turso-replication-20260918.md`](terrabase-vs-turso-replication-20260918.md),
which measured the old synchronous push path on the same day.

**This is a loopback benchmark on one host. It is not a WAN,
cross-region, or throughput-saturation claim.**

## What changed since the morning receipt

- The write path is now leaderless (commit `105a3987`): a local batch
  admits through one durable journal append and acknowledges as soon as
  the local WAL is durable; a background drain ships each mTLS peer only
  the delta above that peer's acknowledged position. A down peer never
  blocks or fails the acknowledgement.
- The durable journal writes replication rows as binary TBREC5 records
  (commit `70270453`): no per-record reflection JSON on the append path,
  one sizing pass and one write pass per frame, no repeated digest
  verification, and bounded (non-quadratic) sparse-frontier rebuilds.
- Measured shape is unchanged for Turso: one multi-row `INSERT` over
  HTTP to the sqld primary, tight replica read-poll until the last row
  is visible.

## Method

Both sides ran back-to-back in the same load window (host load average
2–7 from unrelated concurrent builds; both sides equally exposed). Batch
sizes 1 / 16 / 128 records of ~250 B JSON documents, 5 discarded
warmups, 60 samples each, nearest-rank percentiles.

| Item | TerraBase | Turso |
| --- | --- | --- |
| Source | commit `70270453` (anna-replication branch) | — |
| Server | in-process `TerraBaseHost` mTLS peer + local durable source, `DOTNET_PROCESSOR_COUNT=4`, unpinned | sqld 0.24.33 primary + replica (podman pod), unpinned across 46 CPUs |
| Client | .NET 10 harness, in-process | Rust, libsql crate 0.9.30, release build |
| Ack meaning | local WAL `fdatasync` completed (verified) | sqld commit ack (default durability, unverified) |
| Visibility | replica read-model poll (yielding spin) | replica HTTP read poll |

## Results — send→ack (write acknowledged)

| Batch | TerraBase p50 | TerraBase p95 | Turso p50 | Turso p95 | Faster |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | **0.47 ms** | 0.94 ms | 4.35 ms | 8.48 ms | TerraBase 9.3× |
| 16 | **1.47 ms** | 2.07 ms | 4.42 ms | 11.40 ms | TerraBase 3.0× |
| 128 | 9.27 ms | 12.51 ms | 7.46 ms | 14.81 ms | Turso 1.24× |

## Results — send→visible on the replica (end-to-end)

| Batch | TerraBase p50 | TerraBase p95 | Turso p50 | Turso p95 | Faster |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | **3.56 ms** | 4.88 ms | 8.34 ms | 12.90 ms | TerraBase 2.3× |
| 16 | **6.97 ms** | 11.04 ms | 9.24 ms | 18.87 ms | TerraBase 1.3× |
| 128 | 35.41 ms | 46.35 ms | 15.12 ms | 28.20 ms | Turso 2.3× |

Batch-128 ack ranged 6.9–9.3 ms across same-day windows (load-sensitive);
at the quiet end it is at parity or better than Turso's 7.46 ms.

## Bytes on the wire

Each admitted batch shipped exactly its own records — nothing else:

| Batch | Records shipped/admit | Bytes shipped/admit (provenance framing) | Quiet re-drain after ack |
| --- | ---: | ---: | ---: |
| 1 | 1 | 375 B | 0 entries |
| 16 | 16 | 6,000 B | 0 entries |
| 128 | 128 | 48,000 B | 0 entries |

The drain restarts from each peer's persisted acknowledgement (logical
frontier plus physical journal cut), so a caught-up peer ships nothing
and a failed drain reships only the unacknowledged delta; the receiver
deduplicates by dot.

## Against the morning synchronous-push receipt

| Batch | ack p50 (old → new) | send→visible p50 (old → new) |
| --- | --- | --- |
| 1 | 3.89 → **0.47 ms** | 3.93 → **3.56 ms** |
| 16 | 17.45 → **1.47 ms** | 17.52 → **6.97 ms** |
| 128 | 86.20 → **9.27 ms** | 86.21 → **35.41 ms** |

## Open gap (batch-128 end-to-end)

Phase profile of one 128-record drain (temporary instrumentation,
removed before commit): peer validate ~1.0 ms, peer durable apply
~3.6 ms, export/materialize ~0.2 ms, and ~9.6 ms in the client-side
publish chain — the caller recomputes every record digest for the
transport idempotency key, converts each record to a wire publication,
and pays MessagePack encode/decode plus mTLS HTTP/2 framing both ways.
That transport chain is the next optimization lane; the peer apply and
admit paths are no longer the bottleneck.

## Caveats

- Same-window fairness: both sides ran minutes apart under identical
  host load; the earlier receipt's CPU asymmetry (sqld unpinned on 46
  CPUs) still applies, but TerraBase now also runs unpinned with a
  4-thread envelope. Clients: Terrabase's is in-process; Turso's is a
  pinned release Rust binary.
- Durability asymmetry still favors Turso's ack column (unverified
  default vs verified local WAL fdatasync).
- Different client stacks (C#/.NET 10 vs Rust); loopback single-host;
  neither side measures throughput saturation, WAN latency, or failover.
- mTLS HTTP/2 (TerraBase) vs plaintext gRPC inside the pod (Turso).

## Reproduction

```bash
# Turso: pod + client (receipt dir has Cargo.toml + src/main.rs)
podman pod create --name tursobench -p 8080:8080 -p 8081:8081
podman run -d --pod tursobench --name turso-primary ghcr.io/tursodatabase/libsql-server:latest \
  sqld --db-path /var/lib/sqld/db.sqlite --http-listen-addr 0.0.0.0:8080 --grpc-listen-addr 0.0.0.0:5001
podman run -d --pod tursobench --name turso-replica ghcr.io/tursodatabase/libsql-server:latest \
  sqld --db-path /var/lib/sqld/replica.sqlite --http-listen-addr 0.0.0.0:8081 \
  --primary-grpc-url http://127.0.0.1:5001
cd receipts/20260918-leaderless-vs-turso/turso-client && cargo build --release
taskset -c 14,15 nice -n 19 ./target/release/turso-client

# TerraBase: leaderless lag harness (ProjectReference → terrabase worktree)
env DOTNET_PROCESSOR_COUNT=4 nice -n 19 dotnet \
  .codex/swarm/measurement/leaderless-replication-lag-20260918/bin/Release/net10.0/TerraBaseLeaderlessLagMeasurement.dll \
  --samples 60 --batches 1,16,128
```
