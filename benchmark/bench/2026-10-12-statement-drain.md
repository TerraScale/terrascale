# Cost of releasing abandoned Turso statements (#115), before and after (2026-10-12)

#115 makes the thread that owns a connection finalize the native statement Turso.Data.Sqlite 0.8.2 abandons when a
statement without result columns fails (`SqliteSession.ReleaseAbandonedStatements`: a blocking `GC.Collect()` and
`GC.WaitForPendingFinalizers()`), so the garbage collector's finalizer thread no longer uses the connection at the same
time ("concurrent use forbidden"). This measures what that costs.

## Builds

- before: `c4b15068` (#109's head, the base of #115). after: `16610428` (#115's head). Both `dotnet build -c Release` of
  `src/TerraBase`, same SDK, same ts-dotnet (`bcd20603`).

## Measured configuration (the server process itself)

Read from the TerraBase server process (not a wrapper or the load generator) at the start of every run, identical in all six:

| Item | Value |
| --- | --- |
| Host | beta: AMD Ryzen 5 PRO 6650H, 12 logical CPUs (6 cores, SMT on: CPUs 2n and 2n+1 share core n), 14 GB RAM, kernel 7.2.9, .NET 11.0.0-rc.1.26425.128; host load average 4.6 to 10.4 during the runs (other users' processes) |
| Launch | `systemd-run --user --scope -p CPUQuota=200% -p MemoryMax=512M taskset -c 2,4 dotnet terrabase.dll --storage-threads=2` (Development environment, internal listener) |
| `taskset -cp <pid>` / `Cpus_allowed_list` | `2,4` (two logical CPUs on different cores; their SMT siblings 3 and 5 idle) |
| cgroup | `/user.slice/user-1000.slice/user@1000.service/app.slice/run-p<pid>-….scope` |
| `cpu.max` | `200000 100000` (2 CPUs) |
| `cpuset.cpus.effective` | absent: the user manager does not delegate the cpuset controller, so `AllowedCPUs=` would be ignored; `taskset` pins |
| `memory.max` / `memory.swap.max` | `536870912` (512 MiB) / `max` |
| Runtime CPU count (`dotnet_process_cpu_count`) | 2 |
| GC | workstation, concurrent (the project's settings); no `DOTNET_GCHeapHardLimit`: the runtime takes its limit from the cgroup's 512 MiB |
| Load generator | `benchmark/dotnet/DrainBench` in its own process, `taskset -c 6-9 nice -n 19` (the server inherits nice 19), HTTP/2 to the internal listener |

So this is a real 2-CPU (affinity + quota), 512 MiB limit, not a Fly machine: Fly's shared-cpu vCPUs, disk and network differ.

## Method

`benchmark/dotnet/DrainBench`: one fresh database per run; 16 writers each PUT new documents (about 10 bytes of JSON) as
fast as they get answers, 4 readers GET one document; per phase 3 s warm-up then 20 s measured. A failing write is a
document the server's own INSERT rejects: a UNIQUE expression index (`bench_fail`, added offline before the server starts)
on a field only those documents carry, so their statement fails the way a stopped read or an aborted transaction fails one.
A conflict write is `If-None-Match: *` on an existing document: TerraBase rejects it itself (412, like a 409 unique
violation), no statement fails. Writers pick failing writes with `Random(writer)`, the same sequence for both builds.
Latency: per request at the client. CPU: the server's user + system time from `/proc/<pid>/stat`, as a share of 2 CPUs.
Peak RSS: `VmHWM`, reset per phase with `clear_refs`. GC: the server's own metrics (collections, gen2 collections, pause
time, heap and committed size after the last collection, sampled every 250 ms). Order: before, after, three times.

## Results (medians of 3 runs)

| failing writes | kind | variant | valid writes/s | write p50 / p95 / p99 ms | read p50 / p95 / p99 ms | reads/s | CPU % of allowed | peak RSS MB | max GC heap / committed MB | GC/s (gen2/s) | GC pause ms/s | unexpected (3 runs) |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 % | none | before | 327 | 44.8 / 81.0 / 103.0 | 3.1 / 8.6 / 12.3 | 1066 | 92 % | 186 | 13 / 28 | 23.2 (0.05) | 33.4 | 0 |
| 0 % | none | after | 324 | 44.4 / 82.8 / 97.5 | 3.1 / 8.7 / 12.6 | 1067 | 92 % | 189 | 13 / 28 | 23.1 (0.05) | 34.1 | 0 |
| 1 % | statement | before | 447 | 32.6 / 58.6 / 65.5 | 2.4 / 5.7 / 7.5 | 1460 | 89 % | 198 | 14 / 29 | 32.0 (0.00) | 44.4 | 16 |
| 1 % | statement | after | 434 | 32.8 / 60.0 / 71.2 | 2.4 / 6.0 / 11.2 | 1411 | 88 % | 203 | 16 / 31 | 36.9 (7.95) | 76.2 | 0 |
| 10 % | statement | before | 387 | 35.3 / 65.6 / 73.0 | 2.4 / 5.8 / 7.6 | 1440 | 88 % | 215 | 16 / 30 | 34.1 (0.00) | 47.2 | 151 |
| 10 % | statement | after | 282 | 48.5 / 80.6 / 97.1 | 2.7 / 12.9 / 16.3 | 992 | 79 % | 215 | 15 / 30 | 74.2 (63.97) | 271.1 | 0 |
| 50 % | statement | before | 199 | 45.1 / 70.0 / 86.1 | 2.9 / 6.4 / 8.2 | 1255 | 88 % | 213 | 20 / 34 | 42.7 (0.10) | 57.3 | 253 |
| 50 % | statement | after | 78 | 114.1 / 163.8 / 182.8 | 8.9 / 20.8 / 28.1 | 468 | 69 % | 206 | 15 / 22 | 151.8 (151.60) | 537.1 | 0 |
| 10 % | conflict 412 | before | 428 | 31.1 / 61.8 / 68.1 | 2.4 / 5.5 / 7.1 | 1489 | 89 % | 215 | 20 / 35 | 31.8 (0.00) | 44.5 | 0 |
| 10 % | conflict 412 | after | 436 | 30.8 / 58.5 / 64.5 | 2.3 / 5.5 / 7.0 | 1520 | 89 % | 206 | 14 / 27 | 32.3 (0.00) | 45.9 | 0 |
| 50 % | conflict 412 | before | 330 | 23.4 / 49.4 / 60.5 | 2.2 / 5.4 / 7.1 | 1565 | 89 % | 215 | 19 / 34 | 32.5 (0.00) | 45.9 | 0 |
| 50 % | conflict 412 | after | 333 | 23.4 / 47.0 / 58.7 | 2.2 / 5.3 / 7.0 | 1592 | 89 % | 207 | 14 / 27 | 33.1 (0.00) | 46.9 | 0 |

"unexpected" counts answers other than the expected one (201 for a valid write, 500 for a failing one, 412 for a
conflict, 200 for a read), summed over the 3 runs. Before #115 they are innocent writes failing with
`concurrent use forbidden` (and `no such savepoint: batched`): the bug #115 fixes.

## What it shows

- Normal path (0 % failing) and C#-detected conflicts (412 at 10 % and 50 %): no difference beyond noise in throughput,
  latency, CPU, memory or GC. Nothing is released: no statement fails (gen2 collections 0.05/s both).
- Failing statements: about 2 forced gen2 collections per failing write (one after the write, one after the abort probe's
  own failing `BEGIN`), 7 to 9 ms of pause each at this heap size (14 to 20 MB). With 1 % failing writes (4.4/s) the cost is
  within noise for throughput (434 vs 447 writes/s) and p99 (71 vs 66 ms); GC pause rises from 44 to 76 ms/s. With 10 %
  (31/s) throughput drops 27 % and pauses take 27 % of the time; with 50 % (78/s) throughput drops 61 %.
- Other connections: the collection is process-wide, so readers of the same process wait too: read p95 5.8 → 12.9 ms at
  10 %, 6.4 → 20.8 ms at 50 %; reads/s drop with it. CPU use falls (88 % → 69 % of allowed at 50 %): threads wait for the
  collection rather than work.
- Memory: unchanged (peak RSS 186 to 215 MB, GC heap 13 to 20 MB in all phases).
- Before #115, failing statements cost nothing measurable but break unrelated writes: 16, 151 and 253 wrong answers.

## Raw rows

Columns as in `DrainBench`: variant, failing writes, kind, valid writes/s, write p50/p95/p99 ms, failing p50/p99 ms, read p50/p95/p99 ms, reads/s, CPU s, % of allowed CPU, peak RSS MB, max GC heap/committed MB, GC/s (gen2/s), GC pause ms/s, unexpected.

```text
## run 1 before (host load 6.50 4.00 3.11)
# before server pid 367038: pid 367038's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p367038-i323449.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 111 | 142.0 / 216.1 / 264.1 | - | 8.9 / 22.0 / 31.5 | 387 | 30.2 | 75 % | 184 | 12 / 28 | 8.1 (0.05) | 20.7 | 0 |
| before | 1 % | statement | 247 | 58.3 / 113.2 / 142.9 | 51.8 / 112.7 | 3.9 / 11.2 / 15.9 | 829 | 31.5 | 78 % | 198 | 14 / 29 | 18.0 (0.00) | 35.9 | 1 |
| before | 10 % | statement | 197 | 73.6 / 130.2 / 166.9 | 55.1 / 129.7 | 4.6 / 13.4 / 20.0 | 704 | 29.5 | 73 % | 215 | 14 / 29 | 17.4 (0.00) | 39.6 | 15 |
| before | 50 % | statement | 169 | 53.2 / 74.4 / 90.6 | 40.5 / 66.8 | 3.4 / 7.7 / 9.7 | 1057 | 35.7 | 89 % | 213 | 20 / 34 | 36.7 (0.25) | 58.4 | 75 |
| before | 10 % | conflict 412 | 400 | 34.0 / 57.8 / 67.5 | 32.6 / 63.8 | 2.6 / 6.0 / 7.8 | 1368 | 35.9 | 89 % | 215 | 20 / 35 | 29.7 (0.00) | 44.5 | 0 |
| before | 50 % | conflict 412 | 301 | 25.9 / 38.5 / 56.9 | 24.7 / 55.8 | 2.4 / 5.8 / 7.5 | 1436 | 36.0 | 90 % | 215 | 20 / 35 | 30.0 (0.00) | 45.9 | 0 |
## run 1 after (host load 10.40 7.42 4.59)
# after server pid 368841: pid 368841's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p368841-i330243.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| after | 0 % | none | 290 | 53.3 / 94.4 / 113.8 | - | 3.5 / 9.9 / 14.5 | 941 | 37.4 | 93 % | 189 | 13 / 28 | 20.6 (0.05) | 34.1 | 0 |
| after | 1 % | statement | 428 | 33.1 / 60.7 / 71.9 | 37.3 / 61.2 | 2.4 / 6.0 / 11.6 | 1402 | 35.2 | 88 % | 199 | 16 / 30 | 36.3 (7.76) | 78.7 | 0 |
| after | 10 % | statement | 280 | 49.2 / 82.0 / 96.6 | 46.8 / 93.0 | 2.7 / 13.1 / 16.3 | 986 | 31.8 | 79 % | 217 | 17 / 30 | 73.3 (63.17) | 274.1 | 0 |
| after | 50 % | statement | 78 | 115.6 / 163.8 / 182.6 | 91.7 / 134.8 | 8.5 / 21.0 / 28.1 | 470 | 27.6 | 69 % | 206 | 15 / 22 | 150.7 (150.34) | 538.0 | 0 |
| after | 10 % | conflict 412 | 435 | 30.8 / 60.5 / 67.0 | 29.8 / 67.1 | 2.3 / 5.5 / 7.0 | 1533 | 35.7 | 89 % | 206 | 14 / 27 | 32.5 (0.00) | 44.6 | 0 |
| after | 50 % | conflict 412 | 333 | 23.4 / 47.1 / 60.2 | 21.9 / 57.4 | 2.2 / 5.4 / 7.0 | 1587 | 36.0 | 89 % | 207 | 14 / 27 | 33.1 (0.00) | 47.0 | 0 |
## run 2 before (host load 5.51 6.44 4.62)
# before server pid 369993: pid 369993's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p369993-i385109.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 327 | 44.8 / 81.0 / 103.0 | - | 3.1 / 8.6 / 12.3 | 1066 | 37.1 | 92 % | 186 | 13 / 28 | 23.2 (0.05) | 33.4 | 0 |
| before | 1 % | statement | 447 | 32.6 / 58.6 / 65.2 | 29.3 / 51.8 | 2.4 / 5.7 / 7.5 | 1460 | 35.7 | 89 % | 196 | 15 / 29 | 32.0 (0.00) | 46.9 | 5 |
| before | 10 % | statement | 387 | 35.3 / 65.6 / 73.0 | 29.0 / 64.5 | 2.4 / 5.8 / 7.6 | 1440 | 35.7 | 89 % | 212 | 17 / 30 | 34.1 (0.00) | 49.0 | 48 |
| before | 50 % | statement | 199 | 45.1 / 70.0 / 84.4 | 34.0 / 66.2 | 2.9 / 6.4 / 8.2 | 1255 | 35.4 | 88 % | 213 | 22 / 35 | 42.7 (0.10) | 57.3 | 91 |
| before | 10 % | conflict 412 | 428 | 31.1 / 61.8 / 68.1 | 29.9 / 67.4 | 2.4 / 5.5 / 7.1 | 1489 | 35.6 | 89 % | 213 | 20 / 35 | 31.8 (0.05) | 45.1 | 0 |
| before | 50 % | conflict 412 | 330 | 23.4 / 51.2 / 63.2 | 22.0 / 60.1 | 2.2 / 5.4 / 7.0 | 1565 | 35.8 | 89 % | 213 | 19 / 34 | 32.5 (0.00) | 46.2 | 0 |
## run 2 after (host load 4.65 5.67 4.58)
# after server pid 370853: pid 370853's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p370853-i393232.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| after | 0 % | none | 339 | 38.2 / 79.7 / 97.5 | - | 3.0 / 8.3 / 12.4 | 1108 | 37.0 | 92 % | 189 | 13 / 28 | 24.0 (0.05) | 34.1 | 0 |
| after | 1 % | statement | 435 | 32.6 / 60.0 / 69.6 | 35.5 / 68.9 | 2.4 / 6.0 / 11.2 | 1411 | 35.3 | 88 % | 203 | 16 / 34 | 36.9 (7.95) | 75.5 | 0 |
| after | 10 % | statement | 283 | 48.1 / 80.6 / 97.3 | 45.9 / 92.2 | 2.7 / 12.8 / 16.3 | 992 | 32.0 | 79 % | 212 | 15 / 30 | 74.7 (64.08) | 268.6 | 0 |
| after | 50 % | statement | 79 | 112.5 / 164.4 / 182.8 | 91.0 / 138.3 | 9.3 / 20.8 / 27.6 | 461 | 27.7 | 69 % | 203 | 15 / 23 | 156.0 (155.71) | 537.1 | 0 |
| after | 10 % | conflict 412 | 436 | 30.8 / 58.5 / 64.5 | 29.6 / 63.4 | 2.3 / 5.4 / 7.0 | 1520 | 35.7 | 89 % | 204 | 14 / 27 | 32.3 (0.00) | 45.9 | 0 |
| after | 50 % | conflict 412 | 334 | 23.3 / 46.3 / 58.5 | 22.0 / 56.5 | 2.2 / 5.3 / 7.0 | 1592 | 35.9 | 89 % | 204 | 14 / 27 | 33.0 (0.00) | 46.4 | 0 |
## run 3 before (host load 4.60 5.17 4.55)
# before server pid 371753: pid 371753's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p371753-i385253.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 340 | 39.3 / 80.0 / 95.6 | - | 2.9 / 8.1 / 12.3 | 1128 | 37.1 | 92 % | 186 | 13 / 28 | 24.2 (0.05) | 34.8 | 0 |
| before | 1 % | statement | 449 | 32.6 / 58.6 / 65.5 | 28.8 / 68.6 | 2.4 / 5.6 / 7.3 | 1490 | 35.7 | 89 % | 200 | 14 / 29 | 32.3 (0.00) | 44.4 | 10 |
| before | 10 % | statement | 390 | 35.3 / 62.9 / 70.7 | 29.4 / 63.0 | 2.4 / 5.7 / 7.6 | 1448 | 35.5 | 88 % | 217 | 16 / 30 | 34.6 (0.00) | 47.2 | 88 |
| before | 50 % | statement | 200 | 44.9 / 68.9 / 86.1 | 33.7 / 66.6 | 2.9 / 6.3 / 7.9 | 1270 | 35.4 | 88 % | 214 | 17 / 32 | 43.0 (0.00) | 55.9 | 87 |
| before | 10 % | conflict 412 | 432 | 30.9 / 62.1 / 69.5 | 29.7 / 67.2 | 2.3 / 5.4 / 6.9 | 1531 | 35.6 | 89 % | 215 | 17 / 32 | 32.2 (0.00) | 40.6 | 0 |
| before | 50 % | conflict 412 | 334 | 23.2 / 49.4 / 60.5 | 21.8 / 59.6 | 2.2 / 5.4 / 7.1 | 1566 | 35.8 | 89 % | 215 | 17 / 32 | 32.8 (0.00) | 42.4 | 0 |
## run 3 after (host load 4.61 5.03 4.59)
# after server pid 372989: pid 372989's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p372989-i356092.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| after | 0 % | none | 324 | 44.4 / 82.8 / 96.3 | - | 3.1 / 8.7 / 12.6 | 1067 | 37.2 | 92 % | 193 | 16 / 28 | 23.1 (0.05) | 33.6 | 0 |
| after | 1 % | statement | 434 | 32.8 / 59.4 / 71.2 | 38.1 / 65.8 | 2.4 / 5.9 / 10.6 | 1425 | 35.4 | 88 % | 205 | 18 / 31 | 37.3 (7.96) | 76.2 | 0 |
| after | 10 % | statement | 282 | 48.5 / 77.1 / 97.1 | 46.3 / 93.8 | 2.7 / 12.9 / 16.3 | 998 | 32.0 | 79 % | 215 | 15 / 30 | 74.2 (63.97) | 271.1 | 0 |
| after | 50 % | statement | 78 | 114.1 / 162.9 / 199.0 | 89.3 / 142.0 | 8.9 / 20.8 / 28.4 | 468 | 27.7 | 69 % | 208 | 15 / 21 | 151.8 (151.60) | 530.5 | 0 |
| after | 10 % | conflict 412 | 440 | 30.8 / 56.3 / 63.6 | 29.4 / 61.0 | 2.4 / 5.6 / 7.1 | 1477 | 35.6 | 89 % | 211 | 15 / 27 | 32.3 (0.00) | 46.2 | 0 |
| after | 50 % | conflict 412 | 333 | 23.4 / 47.0 / 58.7 | 22.0 / 55.8 | 2.2 / 5.3 / 6.8 | 1598 | 36.0 | 90 % | 211 | 15 / 27 | 33.2 (0.00) | 46.9 | 0 |
```
