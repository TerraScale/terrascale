# Replication speed on the flat 2-CPU TerraBase limit (2026-10-29)

Follow-up to [2026-10-28-perf-replication.md](2026-10-28-perf-replication.md), whose after numbers ran on the ts-orch
#220 limits (gru 3 CPU, sjc and fra 1.5 CPU). This run is on ts-orchestrator #222's flat limit, `cpu.max` read from each
TerraBase container's cgroup at every measurement below: **200000 100000** (2 CPU) on gru, sjc and fra. Same method and
harness (`scripts/terrabase/xregion-lag.sh`, `/metrics` on the admin listener, TerraBase stdout); the synthetic tenant
was deleted afterwards.

## Six-direction lag, `dc4c67b70` (#875), 2026-10-29 00:27:48-00:34:56Z

One run x 30 probes per cell, so p99 is the cell's largest or second largest probe (a quick sample, not the 3 x 60
method of the 2026-10-28 report). 0 probes lost. ms:

| Direction | Mode | Floor | p50 | p99 | max | load writes/s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| gru -> sjc | idle | 84.3 | 117.3 | 195.8 | 195.8 | |
| gru -> fra | idle | 94.1 | 125.3 | 183.2 | 183.2 | |
| sjc -> gru | idle | 84.3 | 133.5 | 224.6 | 224.6 | |
| fra -> gru | idle | 94.1 | 127.5 | 195.2 | 195.2 | |
| sjc -> fra | idle | 68.9 | 108.1 | 150.6 | 150.6 | |
| fra -> sjc | idle | 69.0 | 119.5 | 251.4 | 251.4 | |
| gru -> sjc | load | 84.3 | 116.2 | 175.5 | 175.5 | 10.7 |
| gru -> fra | load | 94.1 | 125.6 | 244.4 | 244.4 | 9.6 |
| sjc -> gru | load | 84.3 | 132.1 | 218.3 | 218.3 | 10.0 |
| fra -> gru | load | 94.1 | 126.4 | 209.3 | 209.3 | 9.0 |
| sjc -> fra | load | 68.4 | 111.4 | 176.6 | 176.6 | 12.6 |
| fra -> sjc | load | 68.5 | 149.2 | 1,058.6 | 1,058.6 | 10.0 |

p50 108-149 ms in every cell (2026-10-27 before: 133-239 ms). p99 < 200 ms in 6 of 12 cells; the worst is fra -> sjc
under load (1,058.6 ms).

## Writer holds, 120 s windows

| Node | Window (UTC) | Revision, uptime | TerraBase CPU | holds >= 1 s per min | largest holder (held per s) |
| --- | --- | --- | ---: | ---: | --- |
| gru | 00:35:06-00:37:06Z | `dc4c67b70`, 748 s | 1.22 cores | 7.0 | "an engine caller" 349 ms/s, 5.5 of the 7.0 |
| fra | 00:37:29-00:39:29Z | `dc4c67b70`, 952 s | 0.86 cores | 0.0 | `StorageChangeGroupSink.cs:42` 47 ms/s |
| sjc | 00:42:10-00:44:10Z | `304e38368`, 201 s | 0.97 cores | 0.0 | `StorageChangeGroupSink.cs:42` 68 ms/s |

sjc's first window (00:37:29Z) was discarded: its TerraBase restarted during it (the `304e38368` rollout) and its
counters reset. On gru, the long "engine caller" holds since its start split by holder kind: the continuous backup's
cleanup 9, the intent journal's rebuild repair 6, group commit batches 5, the backup's snapshot 1. On the 3-CPU gru of
the 2026-10-28 report the same counter read 0.0 holds >= 1 s per minute; on 2 CPU it does not.

Repair (`StorageChangeGroupSink.cs:124` before #849) does not appear on any node; frontier records
(`StorageChangeGroupSink.cs:95`) hold 10-12 ms/s with no hold of 1 s or more.

## Restart to serving and stop, two rollouts on 2 CPU

"Now listening" to "The catalog is verified" in TerraBase stdout; the pull phases are the log line #875 added (local
digest, the peer's digest, then reading and joining the differing buckets).

| Rollout | Region | Serving after listening | Pulls: ms (states repaired) | Phases per pull: local / peer / buckets read and joined |
| --- | --- | ---: | --- | --- |
| `dc4c67b70`, 00:20-00:23Z | gru | 16.5 s | fra 11,194 (0), sjc 14,962 (18) | fra 8 / 776 / 5,528 ms (165 buckets); sjc 8 / 697 / 10,083 ms (318) |
| | sjc | 16.6 s | gru 15,314 (0), fra 15,367 (9) | fra 8 / 537 / 11,529 ms (338); gru 8 / 683 / 11,694 ms (356) |
| | fra | 16.6 s | gru 14,657 (0), sjc 15,411 (8) | gru 10 / 763 / 10,754 ms (326); sjc 9 / 569 / 11,156 ms (326) |
| `304e38368`, 00:38-00:41Z | gru | 14.6 s | sjc 9,636 (126), fra 13,110 (39) | sjc 8 / 709 / 4,833 ms (161); fra 9 / 1,030 / 7,605 ms (311) |
| | sjc | 17.7 s | gru 16,036 (0), fra 16,328 (8) | fra 9 / 561 / 12,174 ms (345); gru 19 / 685 / 12,337 ms (345) |
| | fra | 18.8 s | gru 14,062 (16), sjc 17,464 (0) | gru 10 / 736 / 10,316 ms (310); sjc 9 / 582 / 10,931 ms (312) |

Serving 14.6-18.8 s after listening (2026-10-28 before: 46-70 s), not yet the 10 s target. Both digests together take
under 1.1 s; reading and joining 161-356 differing buckets takes 4.8-12.3 s of each pull.

Every stop of both rollouts was clean: the stopping process's stdout and stderr hold no "Unhandled exception" and no
"anchor was not written at stop" warning, and end with a completed shutdown step (gru 00:22:37Z and 00:40:52Z, sjc
00:20:31Z and 00:38:43Z, fra 00:21:31Z and 00:39:45Z).
