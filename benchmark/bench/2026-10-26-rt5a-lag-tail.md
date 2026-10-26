# RT5a: where the cross-region lag tail comes from, and fewer storage transactions per replicated batch

Production gru / sjc / fra on batch 9 (`d6905e2dc`, live since 2026-10-26 00:07Z), a dedicated Hobby test tenant (database
home sjc, replicas gru and fra), probes on ts-sjc-1 and ts-fra-1 only (none on gru hosts), headers-only packet captures
(snaplen 64: IP, TCP and the HTTP/2 frame header, no document bytes) of the peer mesh on both ends. Method as
`2026-10-23-rt2-live-6dir.md` (3 x 60 probes per direction, idle and `-load 2`), plus the probe change below. The lag
target is p99 < 200 ms per direction, pooled over the three runs.

## 1. The first probe of every run measured a TCP connect, not replication

xregion-probe's first write opened its connection to the origin inside the timed window: one more round trip (137-190 ms).
Every RT2c run shows it (probe 0 overhead 170-450 ms, all others near the median). The pooled p99 is the third largest of
180 probes, so with one cold probe per run it was set by the three cold probes: sjc -> fra idle pooled p99 240.7 ms equals
the third-slowest probe 0; without probe 0 the same data gives 150.2 ms (fra -> sjc 237.2 -> 152.4).

The probe now opens both clients' connections before the first probe (`benchmark/cmd/xregion-probe`). A remaining first-write
cost after idle (origin server time +75-80 ms) is real and stays in the measurement.

## 2. Batch 9, sjc <-> fra and gru-origin, probe warmed (ms)

| Direction | Floor | Idle p50 | Idle p99 (per-run median, min-max) | Load p50 | Load p99 (per-run median, min-max) |
| --- | ---: | ---: | --- | ---: | --- |
| sjc -> fra | 68.6 | 93.3 | 130.4 (113.4, 111.8-124.0) | 92.5 | 131.2 (130.4, 129.3-130.7) |
| fra -> sjc | 68.7 | 92.9 | 115.3 (112.4, 112.1-159.9) | 92.1 | 189.9 (146.8, 113.8-189.9) |
| gru -> sjc | 84.1 | 115.6 | 309.7 (152.1, 117.7-329.6) | 115.5 | 185.6 (172.3, 155.9-180.0) |
| gru -> fra | 90.6 | 127.2 | 168.7 (154.3, 149.2-168.7) | 125.6 | 186.3 (185.2, 146.3-186.3) |

0 probes lost (1,440). gru -> sjc idle run 3 overlapped sjc's batch 10 restart (TerraBase started 02:48:18Z, probes
02:49:32-39Z): sjc applied batches 200-600 ms late for about 5 s while every writer holder slowed together (write
transactions 11.1 s held in that run vs 1.7-2.2 s in the others). `-> gru` needs a client in gru (FR1).

## 3. Where a slow probe spends its time (sjc <-> fra, captures on both ends matched by TCP sequence number)

| Stage | Median | Slow probes (latency > 140 ms) |
| --- | ---: | --- |
| Origin write, request in -> response out | 11-14 | 30-170 |
| Origin commit -> batch leaves (send minus server) | 1-5 | up to 60-90 |
| Wire, A -> B | one-way floor, 0 retransmissions on every peer stream | same |
| Batch in at B -> visible to B's poll | 10-20 | at most 57 |

The tail is on the origin, not the wire or the receiver. Into gru the same holds: gru's own receive steps are p99 ~50 ms
(FR1's 2 h: receive.total p50/p99/p999 16.5/49.4/209 ms), and the origin's batch -> durable ack to gru is RTT + 10-25 ms at
p99 idle (sjc -> gru 183-196 ms, fra -> gru 243-360 ms against 169/181 ms RTT).

### Origin -> gru, split with exact timestamps

sjc -> fra probes (origin sjc, so every probe also replicates to gru), headers-only captures on sjc and on gru at once
(read-only; no client on gru), each batch on sjc's stream to gru matched by TCP sequence number. Clock offset gru - sjc from
the two directions: 0.2 ms. 2026-10-26 ~04:3xZ, sjc on batch 10, gru on batch 9. ms:

| Segment | Idle p50 / p90 / p99 (60 batches) | `-load 2` p50 / p90 / p99 (323 batches) |
| --- | --- | --- |
| sjc: probe write in -> response out (origin commit) | 20.1 / 63.2 / 331.9 | 40.6 / 161.2 / 477.0 |
| sjc: probe write in -> first batch to gru leaves | 22.4 / 159.8 / 358.7 | 23.4 / 198.1 / 1001.4 |
| wire: batch leaves sjc -> TCP receipt at gru | 84.0 / 84.3 / 86.2 | 83.8 / 84.1 / 84.9 |
| gru: TCP receipt -> ack leaves | 13.0 / 72.1 / 173.2 | 8.4 / 64.5 / 174.6 |
| wire: ack back at sjc | 83.9 / 84.5 / 85.6 | 84.0 / 84.4 / 86.2 |

- The wire adds nothing beyond its 84 ms floor. The largest tail is the origin's (commit, then shipping).
- gru's receipt -> ack p99 (~175 ms) is ~125 ms above its own `receive.total` p99 (49 ms): `received` was stamped only
  when the stream's loop took the frame. The receiver now stamps the frame's arrival and records `receive.loop` (arrival
  until the loop takes it), so production shows this gap. `apply_delay` used buckets 0.05 / 0.1 / 0.25 / 0.5 s, so its
  "~250 ms p99" was a bucket edge; it now uses the step buckets.
- sjc also ships about 4 SYSTEM batches per second to gru while idle (129-byte frames, each answered and applied).

## 4. What holds the origin's writer: bookkeeping, about four FULL write transactions per client write

Writer holds on sjc over one `-load 2` sjc -> fra run (all databases): client commits (group commit batches) 354 holds,
2.9 s; other write transactions 1,678 holds, 9.2 s. The node's background rate is 207 in 30 s, so about 3.7 extra per client
batch: each peer link records its peer's ack with a proof of the shape frontier (`ProveAsync`) and the ack (`AckAsync`),
two FULL write transactions per batch per peer. They queue client writes behind the writer and contend for the node's two
I/O threads (production `storage-threads` = CPU count = 2), which also delays the sender's next read.

The intent journal's "rebuild repair" holds (87k-105k in 2 h) are two per write transaction on `_intents.db`'s own
writer, 1-2 ms each: they never hold a tenant database's writer.

## 5. Change and cell proof

- An ack of a database with two or more batches still in flight to that peer waits for theirs, up to 8 acks
  (`ReplicationProtocol.Window`): one proof and one ack write per window instead of two per batch per peer. With one batch
  left, or none, it is recorded at once (as before). No timer: a stalled stream ends at the progress timeout and resends.
- The ack's inline `HeadAsync` read runs only when a `remote_durable` write waits on the database.
- The sender's read returns retention from its own snapshot: one read transaction per batch instead of two. A fault noted
  after the read still refuses the batch (in-memory check before shipping).
- `terrabase.storage.queue_wait{lane}`: time a Turso call waits for an I/O thread.

The first cell run below used an earlier form of this rule (defer while any batch was in flight, up to 250 ms); the
count-bounded form replaced it so tests on frozen clocks keep their ordering, and is measured in the second run.

Local three-region cell (`local-lag.sh`, one-way delays gru-sjc 84.6, gru-fra 94.1, sjc-fra 68.9 ms, 2 storage threads per
node), main `09e3edbf` vs branch `1e1ae8ef`, interleaved on mariogk-dev, 3 runs x 60 probes, 2 load writers at a fixed
20 writes/s each (`-load-every 50ms`; main and branch 37.6 / 37.0 writes/s achieved). Per-run median (min-max), ms:

| Direction | main p50 | main p99 | branch p50 | branch p99 |
| --- | --- | --- | --- | --- |
| sjc -> fra | 98.6 (98.1-106.9) | 144.3 (130.0-187.1) | 88.2 (85.1-147.7) | 199.1 (123.8-304.7) |
| fra -> sjc | 101.2 (100.3-111.0) | 144.4 (131.6-186.9) | 94.8 (84.3-138.1) | 184.3 (119.0-232.2) |
| sjc -> gru | 116.1 (111.9-127.5) | 232.0 (153.0-333.2) | 103.3 (102.6-328.2) | 145.0 (127.7-1755.8) |
| fra -> gru | 126.5 (124.9-134.8) | 158.4 (157.9-165.0) | 120.7 (109.3-120.9) | 254.4 (156.4-315.5) |
| gru -> sjc | 110.4 (103.3-127.1) | 233.3 (230.4-334.1) | 100.7 (100.1-112.4) | 144.6 (129.2-289.4) |
| gru -> fra | 119.8 (118.7-212.6) | 221.7 (205.0-408.1) | 115.0 (110.8-117.2) | 186.7 (137.4-253.7) |

Write transactions other than client commits and applies, summed over the three nodes: main 35,357, branch 6,784 (-81 %).

- p50 is lower on the branch in every direction (4-13 ms).
- p99 does not resolve on this host: both versions had a run whose load writers fell to 12-26 writes/s (host-wide stalls;
  three nodes in one process on a shared 46-core host), and per-run p99 moves 100+ ms between runs of the same version.
  Branch better in 3 directions, worse in 3. Production decides p99.
- Back to back load (no rate cap) is not a fair comparison: the branch completed 2.6x the writes (137.6 vs 52.8 writes/s),
  because client writes no longer wait behind bookkeeping.

Second run, the final code (count-bounded deferral, receiver arrival stamp): main `1d823d85` vs branch `fcf41a08`, same
method. The host was busier: some runs of both versions sustained only 7-20 of the 40 writes/s. Per-run median (min-max), ms:

| Direction | main p50 | main p99 | branch p50 | branch p99 |
| --- | --- | --- | --- | --- |
| sjc -> fra | 118.0 (114.2-192.0) | 407.3 (243.1-996.4) | 91.6 (88.8-91.9) | 437.9 (170.1-641.9) |
| fra -> sjc | 129.4 (112.6-137.1) | 728.4 (298.6-834.6) | 92.0 (90.2-94.5) | 307.6 (140.6-362.0) |
| sjc -> gru | 135.1 (124.9-180.4) | 479.4 (181.1-1095.1) | 106.4 (102.3-632.1) | 319.6 (240.2-4230.8) |
| fra -> gru | 149.5 (136.0-155.5) | 297.2 (202.9-848.3) | 119.1 (118.8-119.8) | 486.8 (316.7-757.5) |
| gru -> sjc | 124.2 (109.8-740.9) | 356.6 (249.7-2598.4) | 105.9 (105.3-146.3) | 502.6 (236.8-1038.8) |
| gru -> fra | 140.8 (124.1-235.1) | 491.3 (261.3-1005.4) | 113.4 (112.4-159.4) | 174.4 (169.0-1000.5) |

Write transactions other than client commits and applies: main 29,701, branch 6,847 (-77 %). p50 lower on the branch in all
six directions (6-37 ms); p99 again unresolved by this host.

## Still open

- `-> gru` on the new build: FR1's container probe.
- Disk-level stalls: the hosts serve from a 48 GB boot volume; a continuous-backup snapshot copies a database row by row
  into a new local file on the shared I/O pool (`TursoCopy.Write`), and a restart's catch-up both saturate it for seconds.
