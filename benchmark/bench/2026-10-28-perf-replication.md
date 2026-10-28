# Replication speed: idle background work, writer stalls and the catalog gate (2026-10-27/28)

Production gru / sjc / fra. **Before** is revision `9731313c0` (2026-10-27) for lag and idle rates, and image `509dc509`
(#855, 1 CPU, 2026-10-28 19:35-19:40Z, CoC blips worker) for the idle counters. **After** is `d0f3672f` (#849, #855,
#782), live 20:50-20:53Z on 2026-10-28, with the ts-orch #220 limits in place: gru `cpu.max` 300000/100000 (3 CPU), sjc
and fra 150000/100000 (1.5 CPU). The before and after CPU limits differ, so the CPU rows below are not same-limit
comparisons.

Method: `scripts/terrabase/xregion-lag.sh` (xregion-probe on every destination host, operator credential on the internal
listeners, a synthetic all-regions tenant deleted afterwards), 3 runs x 60 probes per direction, idle and with 2 load
writers on the origin. Pooled p99 is the 99th-percentile nearest rank of the cell's probes (the third largest of 180, the second largest of 120). Writer holds and counters come from each node's
`/metrics` (admin listener, read-only); restart-to-serving comes from TerraBase stdout ("Now listening" to "The catalog
is verified").

## Six-direction lag (ms)

| Direction | Mode | p50 before | p50 after | pooled p99 before | pooled p99 after | max after |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| gru -> sjc | idle | 211.7 | 124.3 | 658.4 | 456.0 | 45550.4 |
| gru -> fra | idle | 207.0 | 128.7 | 736.8 | 366.6 | 531.4 |
| sjc -> gru | idle | 200.3 | 119.2 | 494.1 | 276.8 | 1540.6 |
| fra -> gru | idle | 203.9 | 128.4 | 580.2 | 669.7 | 786.6 |
| sjc -> fra | idle | 144.5 | 108.6 | 1511.6 | 309.6 | 1071.9 |
| fra -> sjc | idle | 132.7 | 105.7 | 1814.9 | 215.1 | 248.0 |
| gru -> sjc | load | 239.1 | 117.4 | 934.5 | 187.2 | 215.3 |
| gru -> fra | load | 209.8 | 127.4 | 628.8 | 384.2 | 581.0 |
| sjc -> gru | load | 213.3 | 130.1 | 621.9 | 213.9 | 1452.6 |
| fra -> gru | load | 206.6 | 128.2 | 698.8 | 588.3 | 910.6 |
| sjc -> fra | load | 149.1 | 112.6 | 2012.2 | 443.4 | 605.9 |
| fra -> sjc | load | 145.3 | 111.0 | 636.7 | 745.8 | 782.5 |

Before: 3 runs x 60 probes in every cell, 2,160 probes, 0 lost. After: the run was cut short when its thread was archived;
runs 1-2 are complete in every cell and run 3 only for idle (fra -> sjc idle and every load cell have 120 probes, the
rest 180), 0 lost. One gru -> sjc idle probe became visible only after 45.5 s (the 45,550 ms max); it is kept, not
rerun. Origin server time p50 (write in -> response out) went from 75-105 ms to 12-14 ms on gru and stayed 16-23 ms on
sjc and fra. p99 < 200 ms is met only in gru -> sjc under load (187.2 ms); the remaining tail is mostly origin-bound
under load and receiver-side for gru -> fra.

## gru's writer (SYSTEM saturation, the 2026-10-28 14:55Z outage)

| gru | before (`1035dd682`, 1 CPU) | after #849 (`8187c354`, 1 CPU) | after #782 (`d0f3672f`, 3 CPU) |
| --- | ---: | ---: | ---: |
| TerraBase CPU | 100 % of 1 core, PSI 74 % | 99 % of 1 core, PSI 70 % | 1.15 cores of 3 |
| writer holds >= 1 s per minute | 10-17 (incident) | 17.4 | **0.0** |
| repair (`StorageChangeGroupSink.cs:124`) held | 0.44 s/s | gone | gone |
| frontier records (`StorageChangeGroupSink.cs:95`) | 9.3/s, 1.67 s/s held | 616/min, 9.4 holds >= 1 s/min | 89.5/min, 7 ms/s held, 0 >= 1 s |
| usage flush (`UsageMeter.cs`) | 0.25/s, 0.14 s/s held | 21/min, 5.4 holds >= 1 s/min | 21/min, 27 ms/s held, 0 >= 1 s |

Since the #782 restarts (15-17 min, including startup and this run's probe load) no node held any writer 5 s or more.

## Idle counters (sjc / fra; gru after)

| Per node | before (`509dc509`, 1 CPU) | after (`d0f3672f`): sjc / fra / gru |
| --- | --- | --- |
| CPU used | 1.00 core, throttled 94 % / 90 % | 0.93 / 0.89 / 1.14 cores, throttled 12 % / 19 % / 0 % |
| `send.pass` per s | 905 / 1,278 | 42 / 51 / 154 |
| storage point reads per s | ~9,300 | 3,669 / 4,489 / 3,664 |
| I/O pool waits, lane `other`, per s | 870-1,330 | 3,604 / 3,275 / 3,840 |
| writer holds "an engine caller" | 40-43/s at 5-7 ms | 22 / 34 / 46 per s at 1.7-2.6 ms |
| `StorageChangeGroupSink.cs:95` | 11/s at 15 ms | 1.6 / 1.7 / 1.4 per s at 5-8 ms |
| allocations | 85-88 MB/s | 44 / 47 / 44 MB/s |

The after window began about 5 minutes after the restarts, while catch-up still ran. The `other` lane rose: 84 % of all
storage transactions on sjc (558 of 667 a second) come from one line, the management service's shared catalog read
helper; this PR names their callers.

## Restart to serving (catalog gate)

| Region | before | after (`d0f3672f` rollout) | pulls: ms, states repaired |
| --- | --- | ---: | --- |
| sjc | 46-70 s (16:15-16:48Z) | 14.9 s | fra 9,806 (0), gru 13,712 (0) |
| fra | 59 s (17:07Z) | 20.7 s | sjc 18,246 (49), gru 19,487 (7) |
| gru | 56 s (17:09Z) | 15.7 s | sjc 11,810 (2), fra 14,146 (39) |

Not yet under the 10 s target. Each pull still takes 10-19 s; this PR logs each pull's local digest, peer digest and
bucket reads and joins apart, so the next rollout shows which one to cut.

## Local three-region cell (idle, same host, 2 interleaved runs, cell database + 3 all-region + 3 two-region databases)

| Per node per s (gru / sjc / fra) | main | #782 |
| --- | --- | --- |
| writer holds | 16.7-17.0 / 17.7-18.1 / 11.8-12.3 | 2.5-2.7 / 2.7-2.8 / 2.1-2.2 |
| sender passes | 406-579 / 469-548 / 389-491 | 4.4 / 4.3-4.4 / 3.3 |
| host process CPU, three nodes | 16.9-18.1 % of a core | 8.4-8.6 % of a core |
