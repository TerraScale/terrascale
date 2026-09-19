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

## Results — send→ack (final window, host load ~4.4–5.0, back-to-back)

| Batch | TerraBase p50 | Turso p50 | Faster |
| ---: | ---: | ---: | --- |
| 1 | **0.15 ms** | 2.82 | TerraBase 19× |
| 16 | **0.57 ms** | 3.00 | TerraBase 5.3× |
| 32 | **1.20 ms** | 3.24 | TerraBase 2.7× |
| 64 | **3.56 ms** | 3.41 | parity (TB wins on min) |
| 128 | 5.61 ms | **4.15** | Turso 1.4× |
| 1024 | 25.63 (min 17.7) | **14.06** | Turso 1.8× |

## Results — send→visible on the replica (same window)

| Batch | TerraBase p50 (min) | Turso p50 (min) | Faster |
| ---: | ---: | ---: | --- |
| 1 | **1.44** (1.16) | 4.48 (3.77) | **TerraBase 3.1×** |
| 16 | **2.15** (1.74) | 4.66 (4.06) | **TerraBase 2.2×** |
| 32 | **3.32** (2.69) | 5.02 (4.35) | **TerraBase 1.5×** |
| 64 | **5.84** (4.98) | 6.22 (4.84) | **TerraBase 1.1×** (floor parity) |
| 128 | 11.91 (8.99) | **7.56** (6.21) | Turso 1.6× |
| 1024 | 45.43 (31.15) | **20.53** (18.51) | Turso 2.2× |

Across the day, batch-1024 end-to-end improved from 225 ms to 45 ms p50
(31 ms min, ~4–7×), batch 64 moved from a 1.5× loss to a same-window
win, and batch 32 from a loss to a 1.5× win. The final-window numbers
above are from the busiest sustained window; the quietest window
(load ~1.5) put TerraBase at 1.44 / 2.19 / 3.38 / **4.37** / 11.40 /
82.90 p50 — batch 64 at 0.79× of Turso's quiet-window p50. The SYNCED
tier (verified fdatasync per batch, a durability Turso does not offer)
remains ahead at 1–16.

The last two structural changes after the mid-day standings:
- **The fan-out is signalled from the journal append** (inside the
  apply), not after the whole admit returns — the shipping path and the
  acknowledgement path overlap from the moment the rows are durable.
- **Received batches apply outside the runtime coordination gate**:
  the engine's own lock serializes the lattice, the runtime gate orders
  journal appends and bookkeeping, and a following batch's read-model
  apply no longer queues behind the previous batch's journal phase.
  Together these removed the fixed serialization ahead of the peer
  (batch 64 p50: 6.6→4.4 ms in the quiet window).

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
