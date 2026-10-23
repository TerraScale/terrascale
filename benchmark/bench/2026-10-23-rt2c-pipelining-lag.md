# 2026-10-23 RT2c: pipelined replication batches (protocol 2, window 8) on the local lag cell

`benchmark/scripts/xregion/local-lag.sh <out> 3 60` (idle) and `<out> 3 40 8` (8 writers on the origin), then
`local-lag-table.py`. Three nodes on one host, peer links with the real one-way delays (gru-sjc 84.6, gru-fra 94.1,
sjc-fra 68.9 ms). Host beta (12 cores), order main idle, branch idle, branch load, main load; 180 probes per direction
idle, 120 under load. First pass: main = `cd2de5108` (#512), branch = `e6e615d4` (this change on that base).
Visible = write at A readable at B, including the injected one-way delay. Remote ack delay = origin stamp to the peer
region's durable ack (`terrabase_replication_remote_ack_delay_seconds`, bucket-interpolated).

## Idle: write at A visible at B (ms; p50 / p95 / p99), remote ack delay (p50 / p99)

| Direction | main visible | branch visible | main remote ack | branch remote ack |
| --- | --- | --- | --- | --- |
| fra → gru | 128.9 / 140.0 / 148.5 | 128.5 / 136.0 / 145.9 | 225.7 / 472.0 | 224.3 / 291.6 |
| fra → sjc | 103.9 / 113.4 / 120.8 | 102.4 / 152.9 / 175.0 | 175.2 / 454.3 | 174.4 / 239.6 |
| gru → fra | 129.5 / 138.8 / 157.1 | 132.3 / 201.9 / 219.2 | 226.4 / 475.7 | 234.3 / 402.2 |
| gru → sjc | 120.1 / 148.7 / 182.5 | 122.7 / 191.4 / 205.2 | 220.6 / 475.7 | 224.6 / 298.7 |
| sjc → fra | 103.5 / 111.9 / 117.7 | 103.1 / 142.6 / 165.5 | 175.0 / 412.8 | 177.4 / 278.1 |
| sjc → gru | 119.9 / 126.9 / 130.5 | 121.0 / 190.6 / 197.6 | 206.8 / 473.6 | 209.4 / 293.6 |

## 8 writers (on #512): write at A visible at B (ms; p50 / p95 / p99), remote ack delay (p50 / p99)

| Direction | main visible | branch visible | main remote ack | branch remote ack |
| --- | --- | --- | --- | --- |
| fra → gru | 243.0 / 629.3 / 957.1 | 214.6 / 391.7 / 750.6 | 400.7 / 2322.8 | 363.9 / 962.4 |
| fra → sjc | 255.0 / 1335.6 / 3387.7 | 240.9 / 523.6 / 719.3 | 372.8 / 2258.6 | 355.1 / 990.2 |
| gru → fra | 203.7 / 492.4 / 694.0 | 231.0 / 415.2 / 595.9 | 405.3 / 2453.0 | 368.4 / 950.0 |
| gru → sjc | 261.0 / 1495.0 / 3719.6 | 235.5 / 396.7 / 441.0 | 401.5 / 2479.8 | 358.8 / 969.7 |
| sjc → fra | 310.3 / 688.8 / 885.7 | 229.3 / 412.5 / 594.6 | 394.6 / 2165.7 | 307.6 / 982.8 |
| sjc → gru | 282.1 / 830.5 / 1256.1 | 207.4 / 461.3 / 587.0 | 399.3 / 2248.2 | 322.1 / 932.8 |

## Step split under load (p50 per node, ms; fra / gru / sjc)

| Step | main | branch |
| --- | --- | --- |
| send.read | 4.7 / 4.7 / 5.0 | 0.7 / 0.7 / 0.9 |
| send.ack | 10.3 / 9.7 / 10.1 | 1.6 / 1.4 / 1.6 |
| receive.wait (behind the database's previous batch) | 0.3 / 0.3 / 0.3 | 103 / 84 / 120 |
| receive.total | 38 / 37 / 36 | 152 / 131 / 165 |
| batches received | 1303 / 1212 / 1329 | 5292 / 5266 / 5251 |

## 8 writers again after rebasing onto #505 (two runs each, order main, branch, branch, main; ms, run 1 / run 2)

main = `1a3a7a72e` (#505), branch = `b14e1660b`. Receiver `receive.wait` p50: main 0.3 ms, branch 78-124 ms; batches
received per node: main ~1250, branch ~5200.

| Direction | main visible p50 | branch visible p50 | main visible p99 | branch visible p99 | main remote ack p50 | branch remote ack p50 |
| --- | --- | --- | --- | --- | --- | --- |
| fra → gru | 202 / 254 | 204 / 224 | 500 / 684 | 351 / 595 | 376 / 407 | 359 / 377 |
| fra → sjc | 238 / 262 | 253 / 233 | 374 / 726 | 1091 / 412 | 351 / 382 | 369 / 366 |
| gru → fra | 193 / 209 | 218 / 189 | 450 / 938 | 1134 / 427 | 393 / 413 | 361 / 328 |
| gru → sjc | 241 / 251 | 214 / 209 | 593 / 730 | 865 / 1093 | 389 / 409 | 356 / 300 |
| sjc → fra | 289 / 294 | 231 / 203 | 566 / 882 | 419 / 483 | 376 / 393 | 362 / 314 |
| sjc → gru | 246 / 330 | 220 / 187 | 427 / 618 | 922 / 573 | 381 / 396 | 368 / 309 |

## Reading

- Under load the window removes the sender's wait for the previous ack: send.read and send.ack fall about 6x, remote
  ack p50 falls 3-27 % in 11 of 12 direction-runs, visible p50 falls in 9 of 12. p99 is mixed: the first pass
  (on #512) cut p99 in every direction (to 441-751 ms from 694-3720 ms), the second (on #505, where main's own tails
  were lower) improved p99 in 7 of 12 direction-runs and worsened it in 5.
- The wait moved to the receiver: four times as many, smaller batches, each its own apply transaction, applied one
  after another per database (`receive.wait` p50 78-124 ms). That is now the top contributor; the next step is applying
  a database's queued chained batches in one transaction on the receiver (one ack per batch still, cumulative).
- Idle: p50 unchanged (one write, one batch). p95/p99 within this host's run-to-run noise (one of the three branch runs
  had a slow p50 in the gru directions); a repeat in reverse order was disturbed by another job on the host (load 87)
  and is not reported. A first pass on mariogk-dev (load 40-58 on 46 cores) showed the same load-case shape.
