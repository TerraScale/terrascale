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

## 4. What holds the origin's writer: bookkeeping, about four FULL write transactions per client write

Writer holds on sjc over one `-load 2` sjc -> fra run (all databases): client commits (group commit batches) 354 holds,
2.9 s; other write transactions 1,678 holds, 9.2 s. The node's background rate is 207 in 30 s, so about 3.7 extra per client
batch: each peer link records its peer's ack with a proof of the shape frontier (`ProveAsync`) and the ack (`AckAsync`),
two FULL write transactions per batch per peer. They queue client writes behind the writer and contend for the node's two
I/O threads (production `storage-threads` = CPU count = 2), which also delays the sender's next read.

The intent journal's "rebuild repair" holds (87k-105k in 2 h) are two per write transaction on `_intents.db`'s own
writer, 1-2 ms each: they never hold a tenant database's writer.

## 5. Change and cell proof

- An ack of a database with batches still in flight to that peer waits for theirs, up to 250 ms: one proof and one ack write
  per interval instead of two per batch per peer. A quiet database's last ack is recorded at once.
- The ack's inline `HeadAsync` read runs only when a `remote_durable` write waits on the database.
- The sender's read returns retention from its own snapshot: one read transaction per batch instead of two. A fault noted
  after the read still refuses the batch (in-memory check before shipping).
- `terrabase.storage.queue_wait{lane}`: time a Turso call waits for an I/O thread.

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

## Still open

- `-> gru` on the new build: FR1's container probe.
- Disk-level stalls: the hosts serve from a 48 GB boot volume; a continuous-backup snapshot copies a database row by row
  into a new local file on the shared I/O pool (`TursoCopy.Write`), and a restart's catch-up both saturate it for seconds.
