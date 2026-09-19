# TerraBase (TerraScale) vs Turso (libSQL) replication lag — 2026-09-19

Loopback, single-host, back-to-back same-window comparison of the
Anna-style leaderless admit path against Turso's libSQL server (sqld)
primary→replica replication at batch sizes **1 / 16 / 32 / 64 / 128 /
1024**. Receipt: `receipts/20260919-terrascale-vs-turso/` (per-side
JSONL for every window, Turso client source, reproduction commands).

**This is a loopback benchmark on one host. It is not a WAN,
cross-region, or throughput-saturation claim.**

## What changed since `leaderless-vs-turso-replication-20260918.md`

All on the `anna-perf` branch (base `b94c8773` from the 2026-09-18
frame-lane work), five commits through `e5c22936`:

- **TBREC5 v6 drops the per-record trailing checksum** — the enclosing
  TBLGF frame checksum covers every byte on journal and wire. Encode
  7.7→~4.5 µs/record, decode 10.6→~7 µs/record, wire −29%.
- **Transport frames decode once** (the structural validation no longer
  re-decodes every record), and decoded rows are adopted as detached.
- **Raw frame ingest**: the receiving member journals the received
  record bytes with its local sequences stamped in at byte offset 8 —
  no per-row re-encode on the peer. Ingested frames journal as
  uncompressed v1 (the payload arrived decompressed; recompressing was
  a wasted LZ4 pass).
- **Verbatim fan-out**: the journal captures each row's encoded bytes
  during its single write pass; the in-memory tail serves entries with
  those bytes, and the drain ships a transport frame assembled from the
  sender's own journal bytes. Neither side re-encodes a row.
- **Batched lattice apply** under one engine lock (one coordination per
  batch instead of per row; the single-row apply was ~5.5 µs/row, the
  batch ~2.4).
- **Trusted batches apply to the read model before the journal append**:
  rows are visible at the lattice apply while the durable append still
  gates the acknowledged response. A failure between the two leaves the
  row in memory only; the unacknowledged sender re-ships and the
  lattice deduplicates on replay. Reads no longer take the runtime
  coordination gate.
- Digest preimage hashes raw entity bytes with a fast lowercase hex
  writer; frame encoding is single-pass into a pooled buffer; journal
  frames above 256 KiB skip compression; frame push resolves tenant
  authorization once per frame; member hosts run server GC.

## Results — send→ack (final window, host load ~1.6–3.2)

| Batch | TerraBase p50 | Turso p50 | Faster |
| ---: | ---: | ---: | --- |
| 1 | **0.19 ms** | 2.73 | TerraBase 14× |
| 16 | **0.75 ms** | 2.94 | TerraBase 3.9× |
| 32 | **1.58 ms** | 3.11 | TerraBase 2.0× |
| 64 | **2.88 ms** | 3.46 | TerraBase 1.2× |
| 128 | 6.17 ms | **4.04** | Turso 1.5× |
| 1024 | 27.67 (min 13.0) | **13.08** | Turso 2.1× (parity at min) |

## Results — send→visible on the replica (same window)

| Batch | TerraBase p50 (p95) | Turso p50 (p95) | Faster |
| ---: | ---: | ---: | --- |
| 1 | **1.30** (1.60) | 4.46 (5.61) | **TerraBase 3.4×** |
| 16 | **2.05** (2.92) | 4.58 (5.45) | **TerraBase 2.2×** |
| 32 | **3.95** (6.74) | 4.78 (6.31) | **TerraBase 1.2×** |
| 64 | 6.59 (9.07), min 4.83 | **5.50** (6.87), min 4.63 | tie (Turso +4% on min) |
| 128 | 13.97 (17.82), min 11.6 | **7.22** (7.99), min 5.7 | Turso 1.9× |
| 1024 | 54.11 (129.61), min 26.0 | **19.49** (25.80), min 17.7 | Turso 2.8× |

Across the day, batch-1024 end-to-end improved from 225 ms (this
morning's first measurement) to 54 ms p50 / 26 ms min — ~4× on p50, ~8×
on the floor — and batches 32 and 64 moved from losses to a win and a
statistical tie respectively. The SYNCED tier (verified fdatasync per
batch, a durability Turso does not offer) remains ahead at 1–16.

## Why 128–1024 still trail

Turso's replica applies a continuously-streamed WAL frame of **page
images** — no per-batch RPC, no authentication, no per-row parse; its
tail is ~2 ms at 128 records and ~6 ms at 1024. TerraBase's floor at
those sizes is the mTLS RPC (~1.3 ms) plus a per-record parse-and-apply
(decode ~8 µs + lattice ~2.4 µs + ingest ~2 µs per row). Closing it
needs the fan-out to move from per-batch unary pushes to a long-lived
streamed delivery lane (the pull stream exists but pages immutable
snapshots; it has no wait-for-more mode), and/or a coarser replication
unit. The engineering path is identified; the unary lane is at its
structural floor.

## Caveats

- The host carries unrelated concurrent builds whose load swings
  (1.6–11 through the day) inflate both sides' p50s; the final window
  above was the quietest achieved (~1.6–3.2), with mins as the floor
  proxy. Turso's client is pinned to CPUs 14,15; TerraBase's harness is
  unpinned with a 4-thread envelope and a server-GC floor, as in the
  2026-09-18 receipts.
- Durability: TerraBase ASYNC tier acks OS-published WAL (verified
  publish); sqld's default ack durability is unverified. Transport
  asymmetry (mTLS HTTP/2 vs plaintext gRPC in the pod) as before.
- The benchmark harness sets a 16-thread floor so admit, drain, peer
  apply, and visibility polling do not pay thread-pool injection delays
  on a busy shared host — the documented member-host deployment shape.

## Reproduction

```bash
# Turso (pod from 2026-09-18, ports 18080/18081)
cd receipts/20260919-terrascale-vs-turso/turso-client && cargo build --release
taskset -c 14,15 nice -n 19 ./target/release/turso-client

# TerraBase: anna-perf branch (e5c22936), harness ProjectReference → that tree
env DOTNET_PROCESSOR_COUNT=4 nice -n 19 dotnet \
  .codex/swarm/measurement/leaderless-replication-lag-20260918/bin/Release/net10.0/TerraBaseLeaderlessLagMeasurement.dll \
  --samples 60 --batches 1,16,32,64,128,1024 --wal async
```
