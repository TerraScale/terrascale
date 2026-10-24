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

## Receiver coalescing (feat/rt2c-receiver-coalesce): queued chained batches of a database in one transaction

The batch whose turn comes applies the batches admitted behind it in its chain in one durable transaction (up to one
batch's groups and bytes and one transaction's deltas), then acks each in order. Host beta (12 cores), same cell and
scripts. main = `1a3a7a72e` (#505), pipe = `30398963b` (window 8), coal = `d07b3dd06` (pipe + coalescing; the same
receiver code as the branch's rebase onto the restacked pipelining branches). Order: idle main, pipe, coal; load coal,
pipe, main; load main, pipe, coal; idle coal, pipe, main. The first idle coal cell overlapped another job's .NET build
on the host (every storage step's p99 rose 5-30x on all nodes, including steps coalescing never touches, and no batch
was coalesced: transactions = batches); the idle table uses the second idle pass, run with the host quiet.

### Idle (second pass): write at A visible at B (ms; p50 / p95 / p99), remote ack delay (p50 / p99)

| Direction | main visible | pipe visible | coal visible | main ack | pipe ack | coal ack |
| --- | --- | --- | --- | --- | --- | --- |
| fra → gru | 132.2 / 254.4 / 270.5 | 131.0 / 193.8 / 216.6 | 129.4 / 135.3 / 141.2 | 237 / 484 | 228 / 298 | 224 / 280 |
| fra → sjc | 106.7 / 169.1 / 201.4 | 103.7 / 135.2 / 167.0 | 103.6 / 112.6 / 123.9 | 187 / 470 | 178 / 258 | 174 / 235 |
| gru → fra | 130.2 / 193.6 / 213.5 | 132.2 / 199.5 / 208.8 | 129.9 / 137.3 / 154.0 | 234 / 479 | 231 / 299 | 228 / 299 |
| gru → sjc | 122.6 / 187.8 / 207.3 | 120.3 / 177.5 / 202.2 | 122.3 / 190.4 / 205.9 | 225 / 477 | 220 / 299 | 219 / 295 |
| sjc → fra | 105.6 / 164.8 / 187.9 | 105.1 / 174.0 / 184.9 | 102.4 / 113.4 / 120.2 | 181 / 460 | 184 / (6560) | 174 / 237 |
| sjc → gru | 119.5 / 170.7 / 190.4 | 121.8 / 192.3 / 205.5 | 119.6 / 128.9 / 147.4 | 214 / 476 | 212 / 299 | 202 / 249 |

Idle has one write per batch, so nothing coalesces (transactions = batches on every node); the idle difference is
within this host's run-to-run spread (the first pass had p99 152-274 ms for main and 182-235 ms for pipe). (6560): one
outlier group in the bucket-interpolated histogram.

### 8 writers on the origin: visible p50 and p99, remote ack p50 (ms, run 1 / run 2)

| Direction | main p50 | pipe p50 | coal p50 | main p99 | pipe p99 | coal p99 | main ack p50 | pipe ack p50 | coal ack p50 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| fra → gru | 208 / 222 | 190 / 192 | 195 / 187 | 402 / 1991 | 324 / 388 | 435 / 479 | 365 / 361 | 350 / 342 | 260 / 267 |
| fra → sjc | 228 / 206 | 218 / 188 | 140 / 160 | 351 / 283 | 550 / 355 | 318 / 321 | 337 / 321 | 339 / 305 | 212 / 217 |
| gru → fra | 189 / 207 | 198 / 185 | 146 / 189 | 471 / 391 | 614 / 345 | 307 / 385 | 374 / 366 | 263 / 279 | 235 / 265 |
| gru → sjc | 238 / 279 | 124 / 174 | 122 / 178 | 403 / 322 | 282 / 292 | 210 / 318 | 378 / 364 | 273 / 277 | 204 / 241 |
| sjc → fra | 213 / 214 | 179 / 184 | 168 / 158 | 323 / 2524 | 384 / 313 | 1589 / 233 | 323 / 321 | 289 / 308 | 218 / 218 |
| sjc → gru | 214 / 190 | 182 / 211 | 174 / 181 | 328 / 326 | 330 / 604 | 252 / 342 | 345 / 350 | 336 / 333 | 240 / 241 |

The second main load run ended with host load 10.6 (another job); its 1991 and 2524 ms p99 are disturbed.

### Step split under load (p50 per node, ms; fra / gru / sjc; run 1, run 2)

| Step | main | pipe | coal |
| --- | --- | --- | --- |
| receive.wait (behind the database's previous batch) | 0.25 / 0.25 / 0.25 | 48 / 78 / 87; 52 / 77 / 73 | 0.5 / 5.7 / 0.4; 7.5 / 7.9 / 7.2 |
| receive.total | 18 / 17 / 24; 15 / 15 / 15 | 90 / 123 / 130; 96 / 122 / 117 | 38 / 53 / 32; 57 / 58 / 56 |
| receive.begin + commit + stable | 13 / 12 / 17; 10 / 10 / 10 | 26 / 31 / 28; 27 / 30 / 28 | 22 / 29 / 19; 31 / 31 / 30 |
| send.read / send.ack | 3.5 / 8.0 (fra) | 0.7 / 1.5 | 0.7 / 1.7 |
| batches received / transactions (per node) | ~1300 / ~1300 | ~5250 / ~5250 | ~4900 / ~4400 (9-12 % fewer) |

### Reading

- Under load the receiver's wait behind the database's previous batch falls from 48-87 ms p50 to 0.4-8 ms, and its
  total per batch from 90-130 ms to 32-58 ms. Remote ack p50 falls 15-35 % against pipe in 11 of 12 direction-runs
  (204-267 ms against 263-350), and against main by 25-45 %.
- Visible p50 falls against pipe in 8 of 12 direction-runs (by up to 78 ms on fra → sjc) and is within 10 ms in the
  rest. Visible p99 under load is 210-479 ms for coal, with one 1589 ms tail (sjc → fra, run 1); pipe 282-614 ms. The
  200 ms p99 target is not met under 8 writers on this cell; idle p99 is 120-206 ms.
- Only 9-12 % of batches joined another's transaction, yet the wait collapsed: a batch that queues behind a slow
  apply is now applied with it instead of starting its own begin/commit/stable cycle afterwards, which removes the
  queue's growth. The per-transaction cost (begin + FULL commit + stable read, 19-31 ms p50 under load, of which the
  stable read is 9-15 ms) is now the largest receiver contributor; next on the path are the sender's ship pass and the
  remote ack delay above the visible time (the client wait ends at the remote durable ack).
