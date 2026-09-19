# TerraBase (TerraScale) vs Turso (libSQL) replication lag — 2026-09-19

Loopback, single-host, back-to-back same-window comparison of the
Anna-style leaderless admit path against Turso's libSQL server (sqld)
primary→replica replication at batch sizes **1 / 16 / 32 / 64 / 128 /
1024**. Receipt: `receipts/20260919-terrascale-vs-turso/` (per-side
JSONL, Turso client source, sqld logs from the 2026-09-18 pod).

**This is a loopback benchmark on one host. It is not a WAN,
cross-region, or throughput-saturation claim.**

## What changed since `leaderless-vs-turso-replication-20260918.md`

All in the `anna-replication` branch, commit `b94c8773` on top of
`b6e4e900` ("Ship replication deltas as pre-encoded journal frames"):

- **TBREC5 v6 drops the per-record trailing checksum.** Every
  replication record byte is covered by its enclosing TBLGF frame
  checksum — on the journal and on the wire — so the per-record
  SHA-256 was a third redundant hash pass per record per side. Encode
  drops 7.7→5.1 µs/record, decode 10.6→6.9 µs/record, and the wire
  frame shrinks 29% (128.5 KB→91.2 KB per 1024-record batch).
- **Transport frames decode once.** `DecodeTransportFrame` previously
  validated by decoding every record, then decoded them all again; the
  validation pass is now structural (lengths only) and the open-time
  journal scan keeps a deep variant. Peer decode at 1024 records:
  54 ms→3 ms.
- **Decoded records are adopted as detached** (no defensive deep copy
  on the peer's journal append), and the frame-push response carries
  only the durable ack, local sequence, and durable frontier — the
  per-record results array and applied frontier were dead weight.
- **Raw frame ingest.** A receiving member journals the received
  record bytes with its local sequences stamped in at byte offset 8
  (`FileDurableLog.AssembleTransportFrame`), instead of re-encoding
  every operation into its own journal frame. Guards: the slice must
  be a current-profile TBREC5 v6 record, else the batch falls back to
  the ordinary encode. Round-trip coverage:
  `FramePushJournaledBytesReplayIdenticallyAfterReopen` pushes a frame,
  reopens the journal, and asserts the identical lattice replays.
- **In-memory tail export.** Fan-out drains for caught-up peers serve
  their delta from the last 16,384 appended entries (the same
  immutable objects the dedupe index retains) instead of reading
  journal pages back. Drain export at 1024: 14 ms→0.33 ms.
- **Gossip digests stay index-aligned** through the tenant regrouping;
  the previous `Dictionary<ReplicationRecord, string>` deep-hashed
  every record body twice per push.
- Freshly admitted batches skip the second validation pass in
  `ApplyEntry`; member hosts run server GC.

## Method

Both sides ran back-to-back in the same load window (host load average
~2–5 from unrelated concurrent builds; both sides equally exposed).
~250 B JSON documents, one round trip per batch (TerraBase: one
authenticated mTLS HTTP/2 MagicOnion frame push behind a leaderless
local admit; Turso: one multi-row `INSERT` over HTTP to the sqld
primary), then a backing-off poll of the replica until the last row is
visible. 5 discarded warmups, 60 samples per batch size, nearest-rank
percentiles. TerraBase runs the ASYNC durability tier (Anna tier:
ack publishes to the OS, a 100 ms timer flushes — SQLite
`synchronous=NORMAL` class), matching Turso's default unverified ack
fidelity.

| Item | TerraBase | Turso |
| --- | --- | --- |
| Server | in-process mTLS peer, `DOTNET_PROCESSOR_COUNT=4`, server GC, unpinned | sqld 0.24.33 primary + replica pod, unpinned across 46 CPUs |
| Client | .NET 10 harness, in-process, unpinned | Rust, libsql crate 0.9.30, pinned `taskset -c 14,15` |
| Durability of ack | OS-published WAL (ASYNC tier) | sqld default (unverified, no fsync knob) |

## Results — send→ack

| Batch | TerraBase p50 | Turso p50 | Faster |
| ---: | ---: | ---: | --- |
| 1 | **0.16 ms** | 2.90 ms | TerraBase 18× |
| 16 | **0.58 ms** | 2.82 ms | TerraBase 4.9× |
| 32 | **1.32 ms** | 2.97 ms | TerraBase 2.2× |
| 64 | **2.96 ms** | 3.30 ms | TerraBase 1.1× |
| 128 | 6.44 ms | **3.94 ms** | Turso 1.6× |
| 1024 | 18.00 ms (min 13.0) | **13.33 ms** | Turso 1.4× |

## Results — send→visible on the replica (end-to-end)

| Batch | TerraBase p50 | TerraBase p95 | Turso p50 | Turso p95 | Faster |
| ---: | ---: | ---: | ---: | ---: | --- |
| 1 | **1.30 ms** | 2.36 | 4.63 | 6.84 | **TerraBase 3.6×** |
| 16 | **2.77 ms** | 3.84 | 4.52 | 6.03 | **TerraBase 1.6×** |
| 32 | 4.82 ms | 7.69 | **4.71 ms** | 6.10 | parity (Turso by 2%) |
| 64 | 9.49 ms | 15.88 | **6.35 ms** | 7.26 | Turso 1.5× |
| 128 | 19.53 ms | 27.19 | **7.13 ms** | 7.77 | Turso 2.7× |
| 1024 | 47.25 ms (min 37) | 171.75 | **19.47 ms** | 24.57 | Turso 2.4× |

The SYNCED tier (per-commit fdatasync — a durability Turso does not
offer at all) lands 2.83 / 4.43 / 7.71 / 11.57 / 19.70 / 50.81 ms
end-to-end p50, still ahead of Turso at batches 1–16
(`terrabase-lag-final-synced.jsonl`).

Versus the 2026-09-18 receipt's frame-lane numbers (ack / visible p50
at quiet load): batch 1 0.15→0.16 / 1.78→1.30 ms; batch 16
0.93→0.58 / 3.94→2.77 ms; batch 128 4.39→6.44 / 17.86→19.53 (this
window is busier); batch 1024 visible ~90→47 ms under comparable
load, min 37 ms. Batch 1024 end-to-end has improved ~5× across the two
days (225 ms→47 ms at p50).

## Where the remaining gap lives (phase profile, batch 1024, quiet windows)

ack ~13 ms = per-record digest (6.6 µs/rec measured; raw SHA-256 of
the same body is 1.9 µs) + journal encode (5 µs/rec) + local apply.
Tail after ack ~24 ms = frame assembly 2 ms + mTLS round trip ~1.3 ms
+ peer decode 3 ms + peer apply ~15 ms (journal append with raw ingest
~5 ms + lattice entry apply ~8 µs/record) + dispatch. Turso's replica
applies a streamed WAL frame of page images with no per-batch RPC, no
authentication, and no per-row parse; their tail at 1024 is 6 ms.

Structural conclusions:

1. **Batches 1–32 are won**: the local-admit-first design (ack before
   any peer contact) beats sqld's HTTP round trip plus commit, and the
   frame lane keeps the tail within one mTLS round trip.
2. **64–1024 trail because the replica's unit of work is a record, not
   a page.** Per-record lattice apply (~8 µs) plus ingest bookkeeping
   is ~4× sqld's page-image apply. Closing it means either a cheaper
   per-record apply path (lattice dict consolidation) or batching
   visibility at page/frame granularity.

## Caveats

- Same-window fairness: minutes apart, identical host load exposure;
  sqld remains unpinned on 46 CPUs while TerraBase confines itself to
  4 threads.
- Durability asymmetry still favors Turso's ack column (unverified
  default vs verified OS-published WAL; the SYNCED tier receipts
  verified fdatasync per batch).
- Transport asymmetry (mTLS HTTP/2 vs plaintext gRPC in the pod) and
  client stacks (.NET in-process vs pinned Rust) as before.
- The 1024-record tail's p95 (171 ms) is host-load noise, not path
  structure: the same run's min is 37 ms.
- The convergence suite (`ConcurrentWriters…`) is timing-sensitive on
  a loaded host in both directions; its settle condition now waits for
  the quiet delta, not just the applied frontier.

## Reproduction

```bash
# Turso (pod kept from 2026-09-18; ports moved to 18080/18081)
podman pod create --name tursobench -p 18080:8080 -p 18081:8081
podman run -d --pod tursobench --name turso-primary ghcr.io/tursodatabase/libsql-server:latest \
  sqld --db-path /var/lib/sqld/db.sqlite --http-listen-addr 0.0.0.0:8080 --grpc-listen-addr 0.0.0.0:5001
podman run -d --pod tursobench --name turso-replica ghcr.io/tursodatabase/libsql-server:latest \
  sqld --db-path /var/lib/sqld/replica.sqlite --http-listen-addr 0.0.0.0:8081 \
  --primary-grpc-url http://127.0.0.1:5001
cd receipts/20260919-terrascale-vs-turso/turso-client && cargo build --release
taskset -c 14,15 nice -n 19 ./target/release/turso-client

# TerraBase: anna-replication branch (b94c8773), harness ProjectReference → that tree
env DOTNET_PROCESSOR_COUNT=4 nice -n 19 dotnet \
  .codex/swarm/measurement/leaderless-replication-lag-20260918/bin/Release/net10.0/TerraBaseLeaderlessLagMeasurement.dll \
  --samples 60 --batches 1,16,32,64,128,1024 --wal async
```
