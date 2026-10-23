# Cross-region replication, gru / sjc / fra over direct NetBird paths (2026-10-22)

RT1. Write-to-visible latency in all six directions next to the network floor, sustained throughput and lag, four
correctness checks, and where the time above the floor goes. Everything ran on a disposable test cell; no production
database took part. Raw numbers: [2026-10-22-cross-region-replication.json](2026-10-22-cross-region-replication.json).

## Results

| Direction | Floor (TCP RTT/2) | p50 | p95 | p99 | Overhead p50 | p50 per run (min–max) | Probes |
| --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| gru → sjc | 84.6 ms | 115.0 ms | 156.6 ms | 290.2 ms | 30.5 ms | 115.3 (113.3–117.1) | 300 |
| gru → fra | 94.1 ms | 124.4 ms | 162.6 ms | 319.3 ms | 30.4 ms | 124.9 (123.6–126.0) | 300 |
| sjc → gru | 84.4 ms | 119.5 ms | 186.0 ms | 300.1 ms | 35.1 ms | 120.4 (117.0–123.1) | 300 |
| sjc → fra | 68.9 ms | 98.7 ms | 110.4 ms | 239.0 ms | 29.9 ms | 98.8 (97.3–100.7) | 300 |
| fra → gru | 94.1 ms | 130.8 ms | 188.8 ms | 336.7 ms | 36.7 ms | 130.3 (128.2–135.7) | 300 |
| fra → sjc | 68.9 ms | 98.5 ms | 107.8 ms | 245.0 ms | 29.5 ms | 98.6 (97.5–99.1) | 300 |

- Idle: one probe at a time. No probe was lost (0 of 1,800).
- Overhead above the floor is 30 to 37 ms at p50 in every direction. Directions into gru carry 5 to 6 ms more: the gru node
  is a CPU-starved micro VM (see Setup).
- About 1.7 % of sjc ↔ fra probes take one extra round trip (≈ 240 ms). That is the p99. This run does not show which
  part adds it.
- Two regions only (sjc + fra, before gru joined, same method, 3 runs × 100): sjc → fra p50 95.7 ms, fra → sjc p50 95.7 ms,
  overhead 27 ms.

| Origin | Sustained creates (32 PUTs in flight) | Write-to-visible under that load (sjc ↔ fra) | Replication lag under load (`send_lag`, p50 / p95 / max) |
| --- | --- | --- | --- |
| sjc | 44–50 docs/s per second of load, request p50 600–700 ms | p50 210 ms, p95 422 ms, p99 584 ms (overhead p50 141 ms) | → fra 0.05–0.2 / 4–19 / 11–31 s; → gru 0.0 / 0.0 / 0.0 s while gru was down, 309 / 588 / 620 s when gru had to keep up |
| fra | 41–49 docs/s, request p50 600–735 ms | p50 210 ms, p95 384 ms, p99 583 ms (overhead p50 141 ms) | → sjc 0.0–0.4 / 18–31 / 31–44 s; → gru as for sjc |
| gru | 1.5 docs/s (VM at 57 % CPU steal), request p50 16.7 s | – | → sjc / fra 0.0 / 13–21 / 27–31 s |

"Per second of load" is the per-second average while creates ran; repl-bench's `docs/s` column (created ÷ the whole
load including deletes and conflict rounds) is 14–26. Under-load latency comes from xregion-probe running beside the
two-writer load of the stop test (465 and 474 probes).

## Correctness

| Check | Result | Evidence |
| --- | --- | --- |
| Inserts, updates and deletes arrive; deleted documents never come back | **pass** | 3 loads, all three regions exact: 10,861 / 5,943 / 5,925 documents, 0 missing, 0 extra, 0 mismatched, the same digest in every region. 98 + 57 + 75 deletes. Run 1 and 2 verified again after the gru restart and the partition: still exact, 0 extra. |
| Concurrent writes to one document from 2 regions converge in all 3 | **pass** | Stop and partition runs: sjc and fra each patch the same 100 documents 5 times at once; 100 of 100 byte-identical in gru, sjc and fra, every region's own field kept. Run 1 (three writers): 100 of 100 identical. |
| A stopped region catches up | **pass** | gru stopped with SIGTERM (`docker stop -t 120`, exit 0 after 4 s), down 4 min 9 s while sjc + fra wrote 6,000 documents. Ready 9.3 s after start, exact (5,943, same digest) 9 min 5 s to 9 min 29 s after that. |
| 60 s partition heals with full convergence | **pass** | 8 iptables DROP rules on the gru VM's wt0 for sjc and fra, test ports only, 01:43:33.0 to 01:44:33.2 Z, all removed (0 left). gru exact (5,925) 290 to 311 s after the last write. A write on gru during the partition answered `503 write_outcome_unknown` after 5.6 s and is present with the same version in all three regions after the heal. |

Run 1's repl-bench verdict reads FAILED for one reason only: 32 writes with unknown outcome, created when the starved gru
load was stopped with SIGTERM after 10 min. Every region still held the identical expected set; the conflict "gru lost"
counts in runs 2 and 3 mean gru wrote no rounds (it was down or cut off).

## Where the time above the floor goes (p50)

Packet captures on wt0 of both ends (tcpdump timestamps, each end on its own clock, 120 sjc → fra probes):

| Stage | p50 | p10–p90 | Measured as |
| --- | ---: | --- | --- |
| Client PUT on the wire | 0.1 ms | 0.1–0.2 | fra capture, t0 → first PUT byte |
| Network sjc ← fra (request) | ≈ RTT/2 | | floor |
| sjc: request in → replication batch out to fra | 16.8 ms | 14.6–19.6 | sjc capture only, no RTT estimate involved |
| Network sjc → fra (batch) | ≈ RTT/2 | | floor |
| fra: batch in → ack out (decode, apply, fsync, stable read) | 10.2 ms | 8.7–13.0 | fra capture |
| fra: batch in → first GET that sees it | 12.5 ms | 9.9–23.0 | includes the 2 ms poll and a ~1 ms loopback GET |
| Sum of the measured stages | 29.3 ms | | probe overhead in the same run: 28.6 ms |

- fsync: a 4 KiB `O_DSYNC` write takes 2.0 ms on both A1 hosts (`dd … oflag=dsync`, 200 writes). The commit at the origin
  and the apply at the destination each pay at least one.
- The origin stage grows with the number of databases on the node. With 40 extra empty databases (47 in total, all placed
  in sjc and fra), sjc's request-in → batch-out was 57.5 / 58.7 / 58.8 ms p50 in three runs. After deleting them (7
  databases): 17.2 / 16.8 / 16.9 ms. End-to-end overhead moved 79 / 75 / 78 ms → 31 / 30 / 23 ms. That is about **1 ms per
  database per commit**: every commit wakes each link's ship pass, and the pass reads the change log of every placed
  database (`PeerStreams.ShipAsync`: acked cursor, `ReadAsync`, `RetainsAsync`) before it sends the one that changed.
- Under load the overhead rises from 30 ms to 141 ms p50, about one RTT. A link sends a database's next batch only after
  the previous one is acknowledged (`PeerStreams.ShipAsync` skips a cursor with a batch in flight, and
  `PeerStreamReceiver` refuses a second batch of a database in progress), so a write that commits while a batch is in
  flight waits for that ack.

### Proposed changes (not made here; for the owners)

| Change | Files (owner) | Expected gain | Evidence |
| --- | --- | --- | --- |
| Ship pass reads only databases committed since the last pass (or with an ack or frontier due) instead of every placed database | `Features/Replication/PeerStreams.cs` (R2 sender), commit notification with database ids in `Engine/Sqlite/TursoChangeGroupSource.cs` (R1 capture) | about (N − 1) ms per visible write at p50 for N databases on the origin node: ~6 ms here, ~41 ms at 47 databases | A/B above, 3 runs each |
| Allow more than one batch per database in flight per link, turned on by a Welcome capability so an old receiver keeps getting one at a time | `PeerStreams.cs`, `Protos/…/replication.proto` (R2), `PeerStreamReceiver.cs` (R3) | under sustained writes, one RTT less: overhead p50 141 → ~30–40 ms (idle value) | under-load probes above |

No diff is attached: neither change was built or measured on the cell, and both touch files owned by running threads.

## Setup

| | gru | sjc | fra |
| --- | --- | --- | --- |
| Host | temporary OCI VM `rt1-gru-test`, VM.Standard.E2.1.Micro (Always Free: AMD EPYC 7551, 2 vCPU at a 1/8 OCPU baseline, 1 GB RAM + 2 GB swap), sa-saopaulo-1 | ts-sjc-1, OCI A1 (Neoverse-N1, 2 vCPU, 12 GB) | ts-fra-1, same as sjc |
| Runs as | `docker run`, 700 MiB limit, stop = SIGTERM + 120 s | ts-orch service `terrabase-e2e`, 1 CPU, 1 GiB | same as sjc |
| Image | `ghcr.io/terrascale/terrabase@sha256:8218f233…` (linux/amd64) | `…@sha256:a68b0c17…` (linux/arm64) | same as sjc |

- Code: terrascale `eec12aee`; both digests are from the CI image job of that commit.
- Ports: client 28080, internal 28081, metrics 29091. Membership: a static `TS_PEERS_FILE` naming the three nodes,
  because the gru node is not a ts-orch task. sjc and fra ran a ts-orch build of `ccbc27366` with a local patch that lets
  a spec set its own `TS_PEERS_FILE`; that patch is test-only and not proposed.
- Database `rt1x`: home sjc, replicas fra and gru, in a test organization created through the internal operator API;
  API keys from `/admin/api-keys`, `documents:*` scopes, expiring 2026-10-24.
- Every NetBird path between the three nodes was `Connection type: P2P` (`netbird status -d`, both ends). ICMP sjc ↔ fra:
  137.7 ms avg.

### Relayed versus direct (parent thread, before this run)

| Pair | Relayed through gru (host iptables rejected UDP 51820/3478) | Direct after the fix |
| --- | --- | --- |
| sjc ↔ fra | 247 ms avg, 357 ms max (ICMP) | 137.7 ms ICMP, 137.4 ms TCP connect |
| gru ↔ sjc | – | ~169 ms ICMP; 168.6 ms TCP connect to the gru VM |
| gru ↔ fra | – | ~188 ms ICMP; 188.2 ms TCP connect to the gru VM |

## Method

**Latency (a), single clock.** [`benchmark/cmd/xregion-probe`](../cmd/xregion-probe/main.go) runs on the destination
host B. For each probe it starts polling B's node on loopback (GET by key every 2 ms; 5 ms on the gru VM) and at the same
instant t0 sends the PUT to the origin A over NetBird. Polling starts with the write, not after its ack: a write is
acknowledged only after another region's durable ack, so a poll that waits for the ack measures the ack.

- latency = t_visible − t0 − RTT/2, the time from the write reaching A to B serving it; A's commit is inside it.
- floor = RTT/2. RTT is the median of 21 TCP connects from B to A's client port over the same path.
- overhead = latency − floor.
- 60 probes per run, 200 ms apart, 5 runs per direction. Directions are interleaved within each run. Percentiles are over
  all 300 probes.

**Stage breakdown.** `tcpdump -i wt0` on both ends during 120 probes, analysed with
[`benchmark/scripts/xregion/capture-stages.py`](../scripts/xregion/capture-stages.py) (destination clock) and the
origin's own PUT-in → batch-out times. The latency table comes from
[`latency-table.py`](../scripts/xregion/latency-table.py).

**Throughput, lag and correctness (b, c).** [`repl-bench`](../cmd/repl-bench) in its multi-machine mode:

1. `setup` runs on sjc.
2. `load --only <region>` runs on each writing region's own host against its node on loopback, all at the same time.
   Documents are 256 B 70 %, 1 KiB 25 %, 8 KiB 5 %, each its own PUT. 1 % of them are deleted right away. 100 shared
   conflict documents get 5 merge-patch rounds per writing region.
3. `verify` runs on sjc. It lists every region and compares it with exactly what the loads wrote.
4. Each node's `terrabase_replication_send_lag_seconds` and `ack_lag_seconds` were sampled once a second for the whole run.

The three runs:

| Run | Writers | Documents | Fault | Verify |
| --- | --- | ---: | --- | --- |
| 1 | gru, sjc, fra | 15,000 planned; gru stopped at 953 after 10 min | none | exact |
| 2 | sjc, fra | 6,000 | gru stopped before the load, started after it | exact |
| 3 | sjc, fra | 6,000 | gru partitioned 60 s at the start of the load | exact |

repl-bench's own lag probes poll the other regions over the WAN from the load host. Their resolution is about 200 ms, so
the lag numbers above use the gauges and xregion-probe instead.

## Deviations and limits

- **gru is a micro VM, not ts-gru-1.** FR1's production reset was running on ts-gru-1, and a first test node there
  (400 milli-CPU reserved, 23:40–23:51 Z) left too little CPU for ts-auth to be rescheduled during FR1's change. It was
  removed; ts-auth was 1/1 again at 23:52 Z. The test VM is Always Free and CPU-starved (57 % steal under load). gru's
  throughput (1.5 docs/s), its catch-up times (8 to 9.5 min) and its lag as a receiver under three writers (up to 620 s)
  measure that VM, not TerraBase on an A1 host. gru's latency rows are valid at idle.
- The two hosts with ts-orch ran a patched ts-orch build (see Setup); the release at 00:22 Z replaced it once, and it was
  installed again before the three-region runs.
- 2-region numbers (sjc, fra) are from 3 runs, not 5: the release restart at 00:22 Z interrupted the fourth.
- Code under test is `eec12aee`; main has since gained #376 (envelope buffer reuse), which these numbers do not include.

## Appendix: p50 / p99 per run (ms)

| Direction | Run 1 | Run 2 | Run 3 | Run 4 | Run 5 |
| --- | --- | --- | --- | --- | --- |
| gru → sjc | 117.1 / 159.0 | 114.0 / 190.3 | 113.3 / 158.1 | 115.3 / 224.1 | 115.4 / 183.7 |
| gru → fra | 125.1 / 221.4 | 123.6 / 215.2 | 123.8 / 162.9 | 126.0 / 185.5 | 124.9 / 194.4 |
| sjc → gru | 120.4 / 327.2 | 117.0 / 199.4 | 120.4 / 243.6 | 123.1 / 183.3 | 118.8 / 185.7 |
| sjc → fra | 100.7 / 119.2 | 98.8 / 149.1 | 97.5 / 110.5 | 97.3 / 115.8 | 100.2 / 109.5 |
| fra → gru | 130.3 / 274.4 | 129.5 / 193.9 | 130.4 / 305.9 | 135.7 / 204.9 | 128.2 / 271.4 |
| fra → sjc | 99.1 / 123.1 | 98.6 / 108.6 | 98.0 / 110.1 | 98.9 / 108.2 | 97.5 / 115.5 |

Every probe's latency, every run's summary, the origin-stage runs and the three repl-bench results (writes, lag,
convergence, conflicts) are in the JSON file.
