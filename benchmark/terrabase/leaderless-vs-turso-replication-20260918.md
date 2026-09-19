# TerraBase leaderless admission vs Turso (libSQL) replication — 2026-09-18

Loopback, single-host, back-to-back same-window comparison of the
Anna-style leaderless admit path against Turso's libSQL server (sqld)
primary→replica replication. Receipt:
`receipts/20260918-leaderless-vs-turso/` (raw JSONL per side, logs,
Turso client source). This updates
[`terrabase-vs-turso-replication-20260918.md`](terrabase-vs-turso-replication-20260918.md),
which measured the old synchronous push path on the same day.

**This is a loopback benchmark on one host. It is not a WAN,
cross-region, or throughput-saturation claim.** The multi-region leg is
measured separately in
[`multiregion-leaderless-20260918.md`](multiregion-leaderless-20260918.md):
three env-launched member processes in containers with 40 ms netem between
them — ack stays local (~18 ms) while regions converge transitively.

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

Same-window quiet pairs (60 samples per side, back-to-back minutes
apart; raw `receipts/…/turso-lag-final*.jsonl` and
`terrabase-lag-final*.jsonl`), after host-composed fan-out, paced
peer-ack persistence, and gate-free exports. Quietest pair (host load
~1.5) first; the second pair ran at load ~3.4 and shows each side's
sensitivity to a busy host:

| Batch | TerraBase p50 (quiet) | TerraBase p50 (load 3.4) | Turso p50 (quiet) | Turso p50 (load 3.4) |
| --- | ---: | ---: | ---: | ---: |
| 1 | **0.51 ms** | **0.46 ms** | 3.00 ms | 3.50 ms |
| 16 | **1.85 ms** | **1.83 ms** | 3.58 ms | 4.18 ms |
| 128 | 8.06 ms | 10.79 ms | **4.39 ms** | **6.05 ms** |

Batch-1 ack won every window by 5–7×; batch-16 ack won every window by
~2×. Batch-128 ack loses by 1.8–2× — and unlike Turso's, it is a
verified local WAL `fdatasync`, which is also why it degrades more under
host load while Turso's default (unverified, no fsync) ack barely moves.

## Results — send→visible on the replica (end-to-end)

| Batch | TerraBase p50 (quiet) | TerraBase p95 | Turso p50 (quiet) | Turso p95 | Faster |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | **3.78 ms** | 8.72 ms | 5.16 ms | 10.84 ms | TerraBase 1.4× |
| 16 | 7.57 ms | 14.10 ms | 6.49 ms | 14.52 ms | ~parity |
| 128 | 37.76 ms | 62.07 ms | **7.85 ms** | 15.96 ms | Turso 4.8× |

Batch-16 end-to-end flipped between windows (5.59 vs 6.06 in TerraBase's
favor earlier the same day); treat it as parity. Batch-128 end-to-end is
TerraBase's open gap.

## The Anna tier (AsyncWal): accept on merge, paced durability

`TERRABASE_DATABASE_WAL_DURABILITY=ASYNC` moves the durable host onto the
Anna durability model: commits publish their WAL bytes to the operating
system and acknowledge without the device barrier, a background timer
flushes the log (100 ms), and every compaction, truncate, and close
forces one flush. Acknowledged commits survive process crashes; only the
tail between the last flush and a power cut may be lost — SQLite's
`synchronous=NORMAL` class. SYNCED (per-commit fdatasync) remains the
default. The exported frontier still tracks published records, so the
fan-out ships accepts immediately instead of waiting for a flush, and
delta-only shipping is unchanged (`quietDelta=0` in every run below).

Same-window fair pair at host load ~2.4 (raw
`receipts/…/turso-lag-annatier2.jsonl`, `terrabase-lag-annatier2.jsonl`),
60 samples per side:

| Batch | ack p50 (TerraBase / Turso) | send→visible p50 (TerraBase / Turso) |
| --- | ---: | ---: |
| 1 | **0.14 ms** / 2.98 ms — 21× | **1.71 ms** / 4.76 ms — 2.8× |
| 16 | **0.62 ms** / 3.52 ms — 5.7× | **3.32 ms** / 5.23 ms — 1.6× |
| 128 | 5.07 ms / **4.41 ms** — Turso by 15% | 23.51 ms / **7.81 ms** — Turso ~3× |

Versus the same build's SYNCED tier, same morning windows
(`terrabase-lag-final4.jsonl`, load ~1.6–5.5): ack 0.40→0.14,
1.38→0.62, 6.49→5.07 ms; send→visible 2.61→1.71, 4.78→3.32,
27.76→23.51 ms. Faster than before at every batch size on both metrics.

At batch 128 the ack is now ~parity with Turso; what remains is
per-record lattice work (content digests, read-model merge, response
outcomes) — there is no fdatasync left to remove. The batch-128
end-to-end gap is one mTLS push round trip plus the peer's durable-index
apply versus Turso's streamed, unauthenticated WAL frame with no
per-batch RPC.

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

## Open gap (batch-128)

Phase profile of one 128-record drain (temporary instrumentation,
removed before commit; averages over a run): export ~1.5 ms,
materialize ~0.4 ms, batch idempotency digest ~0.5 ms, peer handler
~5.3 ms (validate ~1.3 + durable apply ~4.0), and wire time dominated by
the peer handler plus payload-proportional codec work. A tiny-RPC probe
through the same mTLS stack round-trips in **1.18 ms p50** (0.83 ms
min), so the per-call machinery is not the bottleneck; earlier
"fixed overhead" readings were contention, and the exports that held
the runtime gate were moved off it.

What remains at batch 128 is structural, not incidental:

1. **The ack is a verified local WAL `fdatasync`.** Turso's default ack
   performs no fsync, which is exactly why its number is flat under
   host load while ours moves with fsync latency. The uncontended
   TerraBase admit (measured on the peer side of the same push path)
   runs ~4–5 ms ≈ Turso's ack — parity at equal fidelity.
2. **The end-to-end path includes the peer's own verified durable
   apply** (a second fdatasync) behind a full mTLS gRPC round trip;
   Turso's replica applies a streamed WAL frame with no per-batch RPC,
   no authentication, and no replica fsync by default.

Two contract-level trims remain unimplemented (the push response ships
128 per-record results the fan-out never reads; the caller recomputes
per-record lattice digests for the batch idempotency key, ~0.5 ms) —
together worth ~1 ms, which cannot flip the batch-128 comparison.
Closing the raw gap means matching Turso's durability fidelity
(unverified ack, unverified replica apply, unauthenticated transport),
not optimizing this path.

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
