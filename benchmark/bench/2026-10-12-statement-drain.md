# Cost of releasing abandoned Turso statements (#115), of not abandoning them, and of the fixed driver (2026-10-12)

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

## Step 1 and step 2 (COS decision Q-DRAIN-MITIGATION)

Same host, launch, limits (read from the server process in every run, identical to the table above) and method.

- trim `9da71771`: no collection after a stopped read (its reader disposes it), and the abort probe's `BEGIN` on the native
  handle, disposed when it fails: one forced collection per failing write instead of two.
- native `3a988d91`: `TursoStatements.Run` runs every statement without result columns on the native handle and disposes
  it in a `finally`, `TursoTransaction` replaces the provider's transaction: nothing is abandoned, no collection is forced.

Session 2 (before, v1 = `16610428`, trim; 3 interleaved runs, host load 4 to 6), medians:

| failing writes | kind | variant | valid writes/s | write p50 / p95 / p99 ms | read p50 / p95 / p99 ms | reads/s | CPU % of allowed | peak RSS MB | max GC heap / committed MB | GC/s (gen2/s) | GC pause ms/s | unexpected (3 runs) |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 % | none | before | 332 | 40.7 / 83.7 / 99.1 | 3.0 / 8.4 / 12.3 | 1092 | 92 % | 189 | 13 / 28 | 23.6 (0.05) | 34.1 | 0 |
| 0 % | none | v1 | 335 | 37.3 / 83.8 / 99.2 | 3.0 / 8.4 / 12.3 | 1091 | 92 % | 187 | 13 / 28 | 23.7 (0.05) | 34.1 | 0 |
| 0 % | none | trim | 339 | 38.6 / 81.2 / 98.1 | 3.0 / 8.3 / 12.5 | 1103 | 92 % | 187 | 13 / 28 | 24.0 (0.05) | 35.0 | 0 |
| 1 % | statement | before | 448 | 32.7 / 59.1 / 66.6 | 2.4 / 5.6 / 7.3 | 1472 | 89 % | 199 | 15 / 29 | 32.2 (0.00) | 45.0 | 23 |
| 1 % | statement | v1 | 435 | 32.6 / 58.8 / 70.5 | 2.4 / 5.9 / 11.1 | 1418 | 88 % | 200 | 16 / 31 | 37.0 (7.96) | 77.3 | 0 |
| 1 % | statement | trim | 442 | 32.6 / 59.5 / 68.5 | 2.4 / 5.8 / 9.0 | 1446 | 88 % | 198 | 15 / 30 | 33.8 (4.03) | 62.5 | 0 |
| 10 % | statement | before | 387 | 35.4 / 65.8 / 73.8 | 2.4 / 5.8 / 7.6 | 1446 | 89 % | 215 | 16 / 30 | 34.4 (0.00) | 47.7 | 172 |
| 10 % | statement | v1 | 283 | 48.4 / 80.8 / 97.0 | 2.7 / 12.8 / 16.5 | 992 | 79 % | 212 | 15 / 30 | 74.3 (64.01) | 271.4 | 0 |
| 10 % | statement | trim | 320 | 42.7 / 71.8 / 85.4 | 2.6 / 10.2 / 13.0 | 1131 | 82 % | 208 | 15 / 29 | 47.5 (35.45) | 204.6 | 0 |
| 50 % | statement | before | 199 | 45.3 / 68.1 / 85.2 | 2.9 / 6.3 / 8.2 | 1256 | 88 % | 215 | 21 / 34 | 42.6 (0.25) | 57.2 | 246 |
| 50 % | statement | v1 | 79 | 113.7 / 161.6 / 186.2 | 9.0 / 20.7 / 27.4 | 467 | 69 % | 203 | 15 / 24 | 153.7 (153.34) | 538.3 | 0 |
| 50 % | statement | trim | 103 | 86.6 / 122.6 / 139.2 | 6.2 / 14.9 / 18.9 | 612 | 71 % | 205 | 15 / 26 | 102.0 (101.83) | 439.6 | 0 |
| 10 % | conflict 412 | before | 432 | 30.7 / 62.4 / 68.5 | 2.3 / 5.5 / 7.2 | 1520 | 89 % | 215 | 20 / 34 | 32.2 (0.00) | 44.4 | 0 |
| 10 % | conflict 412 | v1 | 443 | 30.4 / 59.1 / 67.0 | 2.3 / 5.5 / 7.1 | 1509 | 88 % | 205 | 14 / 27 | 32.6 (0.00) | 46.7 | 0 |
| 10 % | conflict 412 | trim | 434 | 30.6 / 60.7 / 69.0 | 2.3 / 5.4 / 7.0 | 1533 | 89 % | 211 | 14 / 30 | 32.4 (0.00) | 45.4 | 0 |
| 50 % | conflict 412 | before | 330 | 23.4 / 50.2 / 61.3 | 2.2 / 5.4 / 6.9 | 1574 | 89 % | 215 | 19 / 34 | 32.8 (0.00) | 45.5 | 0 |
| 50 % | conflict 412 | v1 | 334 | 23.3 / 47.5 / 58.7 | 2.2 / 5.3 / 6.9 | 1593 | 89 % | 206 | 14 / 27 | 33.1 (0.00) | 48.5 | 0 |
| 50 % | conflict 412 | trim | 337 | 23.1 / 46.4 / 57.6 | 2.2 / 5.4 / 6.9 | 1586 | 89 % | 211 | 14 / 30 | 33.1 (0.00) | 47.1 | 0 |

Session 3 (before, native; 3 interleaved runs, host load 4 to 5), medians:

| failing writes | kind | variant | valid writes/s | write p50 / p95 / p99 ms | read p50 / p95 / p99 ms | reads/s | CPU % of allowed | peak RSS MB | max GC heap / committed MB | GC/s (gen2/s) | GC pause ms/s | unexpected (3 runs) |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 % | none | before | 313 | 50.6 / 84.4 / 97.9 | 3.1 / 8.6 / 12.7 | 1063 | 92 % | 187 | 13 / 28 | 22.7 (0.05) | 32.3 | 0 |
| 0 % | none | native | 319 | 46.5 / 82.2 / 96.2 | 3.2 / 8.7 / 12.6 | 1049 | 92 % | 186 | 13 / 28 | 21.7 (0.05) | 32.1 | 0 |
| 1 % | statement | before | 448 | 32.5 / 59.3 / 68.1 | 2.4 / 5.6 / 7.5 | 1476 | 89 % | 198 | 16 / 29 | 32.3 (0.00) | 45.5 | 32 |
| 1 % | statement | native | 447 | 32.6 / 59.6 / 66.7 | 2.3 / 5.5 / 7.3 | 1493 | 89 % | 197 | 15 / 29 | 31.1 (0.00) | 44.1 | 0 |
| 10 % | statement | before | 387 | 35.5 / 63.6 / 73.6 | 2.4 / 5.7 / 7.6 | 1452 | 89 % | 214 | 16 / 31 | 34.3 (0.00) | 46.7 | 202 |
| 10 % | statement | native | 392 | 35.1 / 64.2 / 72.9 | 2.4 / 5.7 / 7.5 | 1456 | 89 % | 214 | 15 / 29 | 33.2 (0.00) | 44.8 | 0 |
| 50 % | statement | before | 199 | 45.2 / 69.2 / 85.3 | 2.9 / 6.4 / 8.3 | 1265 | 88 % | 212 | 21 / 35 | 42.8 (0.35) | 58.3 | 324 |
| 50 % | statement | native | 201 | 44.8 / 66.2 / 82.4 | 2.9 / 6.4 / 8.2 | 1249 | 88 % | 214 | 20 / 34 | 41.7 (0.25) | 57.2 | 0 |
| 10 % | conflict 412 | before | 434 | 30.6 / 61.3 / 68.6 | 2.3 / 5.5 / 7.0 | 1519 | 89 % | 213 | 20 / 35 | 32.1 (0.00) | 44.9 | 0 |
| 10 % | conflict 412 | native | 435 | 30.7 / 59.6 / 67.9 | 2.3 / 5.5 / 7.1 | 1514 | 89 % | 214 | 19 / 34 | 30.9 (0.00) | 45.1 | 0 |
| 50 % | conflict 412 | before | 331 | 23.5 / 46.5 / 61.8 | 2.2 / 5.4 / 6.9 | 1583 | 89 % | 213 | 20 / 35 | 32.8 (0.00) | 45.5 | 0 |
| 50 % | conflict 412 | native | 333 | 23.2 / 47.4 / 59.2 | 2.2 / 5.4 / 7.0 | 1583 | 89 % | 214 | 18 / 34 | 31.4 (0.00) | 46.4 | 0 |

- v1 reproduces session 1 within 1 % (10 %: 283 vs 282 writes/s; 50 %: 79 vs 78).
- trim: forced gen2 collections 7.96 → 4.03/s (1 %), 64 → 35/s (10 %), 153 → 102/s (50 %); throughput 283 → 320 (10 %),
  79 → 103 (50 %); still below the broken before.
- native: the same as before in every mix (throughput, p50/p95/p99, reads, CPU, peak RSS, GC heap, GC count and pause),
  no forced collections (gen2 0.00/s at 1 % and 10 %), and no wrong answers where before gave 32, 202 and 324.

### Raw rows, sessions 2 and 3

```text
## run 1 before (host load 4.26 2.79 3.14)
# before server pid 379473: pid 379473's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p379473-i394890.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 306 | 44.2 / 88.0 / 100.3 | - | 3.4 / 9.4 / 13.6 | 973 | 37.3 | 93 % | 189 | 13 / 28 | 21.5 (0.05) | 33.4 | 0 |
| before | 1 % | statement | 432 | 34.1 / 58.6 / 66.1 | 30.7 / 62.2 | 2.5 / 5.9 / 7.8 | 1394 | 35.8 | 89 % | 200 | 15 / 29 | 30.9 (0.00) | 46.9 | 14 |
| before | 10 % | statement | 366 | 37.6 / 65.8 / 73.8 | 31.2 / 61.8 | 2.6 / 6.1 / 8.1 | 1352 | 35.6 | 89 % | 219 | 16 / 30 | 32.4 (0.00) | 47.7 | 65 |
| before | 50 % | statement | 184 | 49.2 / 68.1 / 85.3 | 37.0 / 65.5 | 3.1 / 6.9 / 8.8 | 1167 | 35.5 | 88 % | 217 | 22 / 35 | 39.8 (0.25) | 58.2 | 88 |
| before | 10 % | conflict 412 | 412 | 32.6 / 58.5 / 67.0 | 31.3 / 66.3 | 2.5 / 5.8 / 7.5 | 1416 | 35.8 | 89 % | 217 | 20 / 35 | 30.4 (0.00) | 47.1 | 0 |
| before | 50 % | conflict 412 | 317 | 24.6 / 46.1 / 59.6 | 23.4 / 58.3 | 2.4 / 5.7 / 7.3 | 1478 | 36.0 | 89 % | 217 | 20 / 35 | 31.2 (0.00) | 46.8 | 0 |
## run 1 v1 (host load 6.26 4.21 3.63)
# v1 server pid 380723: pid 380723's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p380723-i330736.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| v1 | 0 % | none | 331 | 37.3 / 83.8 / 99.2 | - | 3.0 / 8.4 / 12.3 | 1091 | 37.1 | 92 % | 187 | 12 / 28 | 23.6 (0.05) | 34.0 | 0 |
| v1 | 1 % | statement | 435 | 32.6 / 58.8 / 70.5 | 37.3 / 72.2 | 2.4 / 5.9 / 11.1 | 1418 | 35.2 | 88 % | 200 | 16 / 34 | 37.0 (7.96) | 76.1 | 0 |
| v1 | 10 % | statement | 281 | 48.6 / 80.8 / 97.0 | 46.5 / 95.3 | 2.7 / 13.2 / 16.5 | 992 | 31.9 | 79 % | 209 | 15 / 30 | 73.9 (63.71) | 277.2 | 0 |
| v1 | 50 % | statement | 78 | 114.0 / 161.6 / 188.6 | 91.9 / 147.0 | 9.0 / 21.4 / 29.0 | 460 | 27.5 | 68 % | 202 | 15 / 27 | 150.6 (150.24) | 540.0 | 0 |
| v1 | 10 % | conflict 412 | 438 | 30.5 / 60.5 / 67.8 | 29.1 / 66.4 | 2.3 / 5.5 / 7.1 | 1509 | 35.6 | 88 % | 202 | 14 / 27 | 32.4 (0.00) | 46.7 | 0 |
| v1 | 50 % | conflict 412 | 332 | 23.5 / 47.5 / 58.6 | 22.0 / 57.7 | 2.2 / 5.3 / 6.9 | 1601 | 35.9 | 89 % | 203 | 14 / 27 | 33.1 (0.00) | 49.2 | 0 |
## run 1 trim (host load 4.08 4.17 3.71)
# trim server pid 381937: pid 381937's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p381937-i395222.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| trim | 0 % | none | 339 | 38.3 / 80.5 / 92.1 | - | 3.0 / 8.3 / 12.5 | 1103 | 37.0 | 92 % | 186 | 13 / 28 | 24.0 (0.05) | 34.9 | 0 |
| trim | 1 % | statement | 445 | 32.3 / 59.5 / 69.6 | 34.0 / 74.4 | 2.4 / 5.8 / 9.1 | 1436 | 35.4 | 88 % | 196 | 14 / 30 | 33.8 (4.08) | 62.5 | 0 |
| trim | 10 % | statement | 320 | 43.3 / 71.5 / 84.0 | 39.4 / 80.2 | 2.6 / 10.2 / 13.0 | 1140 | 32.9 | 82 % | 208 | 15 / 29 | 47.2 (35.42) | 206.5 | 0 |
| trim | 50 % | statement | 103 | 86.6 / 122.6 / 142.3 | 68.3 / 105.5 | 6.5 / 14.9 / 18.9 | 604 | 28.9 | 71 % | 205 | 15 / 28 | 102.0 (101.83) | 447.8 | 0 |
| trim | 10 % | conflict 412 | 438 | 30.6 / 58.4 / 65.2 | 29.5 / 64.0 | 2.3 / 5.4 / 7.0 | 1533 | 35.8 | 89 % | 211 | 15 / 34 | 32.5 (0.00) | 45.4 | 0 |
| trim | 50 % | conflict 412 | 337 | 23.1 / 44.7 / 57.0 | 21.8 / 55.3 | 2.2 / 5.4 / 6.9 | 1586 | 35.8 | 89 % | 211 | 15 / 34 | 33.1 (0.00) | 47.8 | 0 |
## run 2 before (host load 5.09 4.41 3.85)
# before server pid 382656: pid 382656's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p382656-i330808.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 333 | 40.5 / 82.4 / 94.5 | - | 3.0 / 8.2 / 12.1 | 1105 | 37.0 | 92 % | 189 | 13 / 28 | 23.8 (0.05) | 34.1 | 0 |
| before | 1 % | statement | 448 | 32.7 / 59.1 / 69.1 | 27.2 / 81.4 | 2.4 / 5.6 / 7.3 | 1489 | 35.8 | 89 % | 199 | 15 / 29 | 32.3 (0.00) | 45.0 | 6 |
| before | 10 % | statement | 387 | 35.4 / 65.9 / 75.7 | 28.9 / 62.7 | 2.4 / 5.7 / 7.5 | 1471 | 35.6 | 89 % | 213 | 16 / 30 | 34.4 (0.00) | 48.5 | 62 |
| before | 50 % | statement | 200 | 44.8 / 67.4 / 83.4 | 33.9 / 65.4 | 2.9 / 6.3 / 8.2 | 1258 | 35.4 | 88 % | 215 | 20 / 34 | 43.1 (0.30) | 57.2 | 75 |
| before | 10 % | conflict 412 | 434 | 30.6 / 62.4 / 68.5 | 29.4 / 68.1 | 2.3 / 5.5 / 7.2 | 1520 | 35.7 | 89 % | 215 | 20 / 34 | 32.2 (0.00) | 44.4 | 0 |
| before | 50 % | conflict 412 | 334 | 23.2 / 50.2 / 62.7 | 21.8 / 59.0 | 2.2 / 5.4 / 6.9 | 1574 | 35.9 | 89 % | 215 | 19 / 34 | 32.8 (0.00) | 45.5 | 0 |
## run 2 v1 (host load 4.73 4.47 3.95)
# v1 server pid 383370: pid 383370's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p383370-i402236.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| v1 | 0 % | none | 335 | 36.3 / 83.8 / 96.7 | - | 3.0 / 8.4 / 12.4 | 1088 | 37.1 | 92 % | 186 | 13 / 29 | 23.7 (0.05) | 35.1 | 0 |
| v1 | 1 % | statement | 433 | 32.8 / 59.3 / 69.1 | 38.4 / 72.2 | 2.4 / 5.9 / 11.5 | 1411 | 35.1 | 87 % | 201 | 14 / 30 | 36.8 (7.95) | 79.0 | 0 |
| v1 | 10 % | statement | 283 | 48.4 / 79.0 / 96.8 | 46.0 / 99.4 | 2.7 / 12.8 / 16.5 | 991 | 32.0 | 79 % | 212 | 16 / 30 | 74.3 (64.01) | 270.8 | 0 |
| v1 | 50 % | statement | 79 | 110.5 / 161.8 / 184.9 | 90.5 / 134.3 | 9.0 / 20.7 / 26.9 | 467 | 27.7 | 69 % | 203 | 15 / 24 | 156.6 (156.30) | 538.3 | 0 |
| v1 | 10 % | conflict 412 | 443 | 30.4 / 56.9 / 62.8 | 29.4 / 61.9 | 2.3 / 5.5 / 7.2 | 1508 | 35.7 | 89 % | 205 | 15 / 28 | 32.6 (0.00) | 46.4 | 0 |
| v1 | 50 % | conflict 412 | 334 | 23.3 / 45.0 / 58.7 | 22.0 / 57.9 | 2.2 / 5.4 / 7.1 | 1584 | 35.8 | 89 % | 206 | 15 / 28 | 33.0 (0.00) | 48.5 | 0 |
## run 2 trim (host load 3.82 4.21 3.94)
# trim server pid 384130: pid 384130's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p384130-i386016.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| trim | 0 % | none | 326 | 42.9 / 81.3 / 100.1 | - | 3.1 / 8.6 / 12.5 | 1076 | 37.1 | 92 % | 189 | 13 / 28 | 23.2 (0.05) | 35.0 | 0 |
| trim | 1 % | statement | 442 | 32.6 / 58.4 / 67.0 | 34.7 / 63.2 | 2.3 / 5.7 / 8.9 | 1469 | 35.4 | 88 % | 203 | 15 / 34 | 33.9 (4.03) | 63.5 | 0 |
| trim | 10 % | statement | 322 | 42.3 / 71.8 / 86.0 | 38.8 / 77.8 | 2.6 / 10.2 / 13.0 | 1131 | 32.9 | 82 % | 212 | 15 / 30 | 47.5 (35.56) | 204.6 | 0 |
| trim | 50 % | statement | 104 | 85.0 / 121.3 / 139.2 | 67.2 / 108.8 | 6.2 / 14.9 / 18.8 | 612 | 28.9 | 71 % | 210 | 15 / 25 | 102.4 (102.15) | 439.6 | 0 |
| trim | 10 % | conflict 412 | 434 | 30.6 / 61.7 / 69.4 | 29.5 / 67.8 | 2.3 / 5.4 / 7.0 | 1542 | 35.7 | 89 % | 212 | 14 / 30 | 32.4 (0.00) | 45.4 | 0 |
| trim | 50 % | conflict 412 | 338 | 23.1 / 46.4 / 57.6 | 21.7 / 56.8 | 2.2 / 5.3 / 6.9 | 1580 | 35.9 | 89 % | 212 | 14 / 30 | 33.1 (0.00) | 47.0 | 0 |
## run 3 before (host load 3.73 3.98 3.89)
# before server pid 384963: pid 384963's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p384963-i402435.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 332 | 40.7 / 83.7 / 99.1 | - | 3.0 / 8.4 / 12.3 | 1092 | 37.1 | 92 % | 185 | 13 / 28 | 23.6 (0.05) | 34.3 | 0 |
| before | 1 % | statement | 452 | 32.3 / 59.9 / 66.6 | 29.5 / 60.2 | 2.4 / 5.6 / 7.3 | 1472 | 35.7 | 89 % | 198 | 15 / 29 | 32.2 (0.00) | 44.8 | 3 |
| before | 10 % | statement | 391 | 35.0 / 65.8 / 73.5 | 28.9 / 61.8 | 2.4 / 5.8 / 7.6 | 1446 | 35.5 | 88 % | 215 | 16 / 30 | 34.4 (0.00) | 47.6 | 45 |
| before | 50 % | statement | 199 | 45.3 / 68.3 / 85.2 | 34.0 / 66.3 | 2.9 / 6.3 / 8.2 | 1256 | 35.3 | 88 % | 215 | 21 / 34 | 42.6 (0.10) | 57.1 | 83 |
| before | 10 % | conflict 412 | 432 | 30.7 / 63.2 / 68.9 | 29.5 / 68.9 | 2.3 / 5.4 / 7.1 | 1533 | 35.7 | 89 % | 214 | 18 / 33 | 32.2 (0.00) | 43.5 | 0 |
| before | 50 % | conflict 412 | 330 | 23.4 / 50.3 / 61.3 | 22.1 / 59.3 | 2.2 / 5.3 / 6.9 | 1597 | 35.9 | 89 % | 214 | 18 / 33 | 32.9 (0.00) | 44.6 | 0 |
## run 3 v1 (host load 4.87 4.32 4.03)
# v1 server pid 385702: pid 385702's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p385702-i386088.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| v1 | 0 % | none | 341 | 40.2 / 79.0 / 99.8 | - | 3.0 / 8.5 / 12.3 | 1096 | 37.1 | 92 % | 188 | 13 / 28 | 24.0 (0.05) | 34.1 | 0 |
| v1 | 1 % | statement | 439 | 32.2 / 58.5 / 70.9 | 37.0 / 64.1 | 2.4 / 5.9 / 10.9 | 1441 | 35.2 | 88 % | 199 | 17 / 31 | 37.2 (8.06) | 77.3 | 0 |
| v1 | 10 % | statement | 285 | 47.4 / 81.7 / 97.9 | 45.4 / 93.1 | 2.7 / 12.8 / 15.7 | 1001 | 32.0 | 79 % | 212 | 15 / 30 | 75.5 (64.19) | 271.4 | 0 |
| v1 | 50 % | statement | 79 | 113.7 / 161.2 / 186.2 | 90.1 / 136.7 | 8.5 / 20.6 / 27.4 | 477 | 27.6 | 69 % | 204 | 15 / 24 | 153.7 (153.34) | 530.3 | 0 |
| v1 | 10 % | conflict 412 | 443 | 30.1 / 59.1 / 67.0 | 28.9 / 64.0 | 2.3 / 5.4 / 7.0 | 1522 | 35.6 | 88 % | 205 | 14 / 27 | 32.8 (0.00) | 47.0 | 0 |
| v1 | 50 % | conflict 412 | 338 | 22.9 / 49.6 / 60.8 | 21.6 / 58.5 | 2.2 / 5.3 / 6.9 | 1593 | 35.7 | 89 % | 206 | 14 / 27 | 33.3 (0.00) | 48.2 | 0 |
## run 3 trim (host load 4.00 4.16 4.02)
# trim server pid 386568: pid 386568's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p386568-i386117.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| trim | 0 % | none | 339 | 38.6 / 81.2 / 98.1 | - | 3.0 / 8.2 / 11.9 | 1115 | 37.1 | 92 % | 187 | 15 / 28 | 24.1 (0.05) | 35.4 | 0 |
| trim | 1 % | statement | 439 | 32.7 / 59.8 / 68.5 | 33.9 / 70.2 | 2.4 / 5.8 / 9.0 | 1446 | 35.4 | 88 % | 198 | 15 / 30 | 33.4 (4.03) | 62.4 | 0 |
| trim | 10 % | statement | 320 | 42.7 / 72.0 / 85.4 | 40.0 / 82.2 | 2.6 / 10.0 / 12.9 | 1128 | 33.0 | 82 % | 208 | 15 / 27 | 48.3 (35.45) | 203.0 | 0 |
| trim | 50 % | statement | 103 | 86.8 / 122.9 / 137.7 | 68.7 / 101.4 | 6.2 / 14.9 / 19.3 | 617 | 29.0 | 72 % | 201 | 15 / 26 | 101.8 (101.53) | 439.4 | 0 |
| trim | 10 % | conflict 412 | 429 | 31.0 / 60.7 / 69.0 | 29.8 / 67.0 | 2.3 / 5.5 / 7.2 | 1522 | 35.7 | 89 % | 206 | 14 / 30 | 32.0 (0.00) | 45.3 | 0 |
| trim | 50 % | conflict 412 | 328 | 23.6 / 49.6 / 61.1 | 22.3 / 59.7 | 2.2 / 5.4 / 6.9 | 1595 | 35.9 | 89 % | 206 | 14 / 30 | 32.7 (0.00) | 47.1 | 0 |
## run 1 before (host load 3.15 3.68 3.88)
# before server pid 388511: pid 388511's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p388511-i386201.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 291 | 50.9 / 93.4 / 105.8 | - | 3.5 / 9.9 / 14.5 | 929 | 37.4 | 93 % | 183 | 13 / 28 | 20.5 (0.05) | 32.3 | 0 |
| before | 1 % | statement | 423 | 34.7 / 59.3 / 68.2 | 29.0 / 98.8 | 2.6 / 6.1 / 8.0 | 1345 | 35.8 | 89 % | 194 | 16 / 29 | 30.0 (0.00) | 46.3 | 7 |
| before | 10 % | statement | 367 | 37.6 / 63.6 / 75.2 | 31.1 / 63.0 | 2.7 / 6.2 / 8.3 | 1324 | 35.7 | 89 % | 215 | 17 / 35 | 32.2 (0.00) | 50.1 | 80 |
| before | 50 % | statement | 197 | 45.3 / 69.6 / 85.7 | 34.5 / 66.9 | 2.9 / 6.4 / 8.3 | 1265 | 35.3 | 88 % | 211 | 24 / 36 | 42.8 (0.35) | 58.8 | 107 |
| before | 10 % | conflict 412 | 434 | 30.6 / 61.3 / 69.2 | 29.4 / 65.9 | 2.3 / 5.5 / 7.0 | 1530 | 35.7 | 89 % | 212 | 20 / 35 | 32.3 (0.00) | 44.9 | 0 |
| before | 50 % | conflict 412 | 336 | 23.2 / 46.5 / 57.4 | 21.8 / 56.5 | 2.2 / 5.4 / 6.9 | 1575 | 35.9 | 89 % | 212 | 20 / 35 | 32.9 (0.00) | 46.5 | 0 |
## run 1 native (host load 4.58 4.16 4.04)
# native server pid 389356: pid 389356's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p389356-i402973.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| native | 0 % | none | 317 | 48.7 / 82.2 / 98.9 | - | 3.2 / 8.7 / 12.8 | 1049 | 37.2 | 92 % | 186 | 13 / 28 | 21.7 (0.05) | 31.5 | 0 |
| native | 1 % | statement | 447 | 32.8 / 60.0 / 66.7 | 29.5 / 61.7 | 2.3 / 5.5 / 7.3 | 1504 | 35.8 | 89 % | 197 | 15 / 29 | 31.1 (0.00) | 45.6 | 0 |
| native | 10 % | statement | 393 | 35.1 / 63.0 / 70.5 | 29.3 / 61.3 | 2.4 / 5.7 / 7.5 | 1456 | 35.7 | 89 % | 216 | 15 / 30 | 33.2 (0.00) | 47.1 | 0 |
| native | 50 % | statement | 201 | 44.5 / 65.6 / 82.4 | 33.7 / 63.5 | 2.9 / 6.4 / 8.2 | 1242 | 35.3 | 88 % | 214 | 22 / 34 | 41.7 (0.25) | 57.2 | 0 |
| native | 10 % | conflict 412 | 435 | 30.7 / 59.6 / 67.9 | 29.6 / 65.7 | 2.3 / 5.5 / 7.1 | 1514 | 35.7 | 89 % | 214 | 19 / 34 | 30.9 (0.00) | 45.1 | 0 |
| native | 50 % | conflict 412 | 333 | 23.2 / 44.9 / 57.2 | 22.1 / 54.4 | 2.2 / 5.4 / 7.0 | 1583 | 35.9 | 89 % | 214 | 19 / 34 | 31.4 (0.00) | 46.4 | 0 |
## run 2 before (host load 5.27 4.55 4.20)
# before server pid 390221: pid 390221's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p390221-i396280.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 313 | 50.6 / 84.4 / 97.9 | - | 3.1 / 8.5 / 12.3 | 1063 | 37.3 | 92 % | 187 | 13 / 28 | 22.7 (0.05) | 32.0 | 0 |
| before | 1 % | statement | 448 | 32.5 / 59.2 / 68.1 | 27.3 / 61.4 | 2.3 / 5.6 / 7.3 | 1492 | 35.9 | 89 % | 198 | 16 / 29 | 32.4 (0.00) | 43.8 | 5 |
| before | 10 % | statement | 387 | 35.5 / 62.5 / 71.2 | 29.4 / 63.1 | 2.4 / 5.7 / 7.6 | 1455 | 35.6 | 89 % | 214 | 16 / 30 | 34.3 (0.00) | 46.7 | 62 |
| before | 50 % | statement | 199 | 45.2 / 69.2 / 85.3 | 34.0 / 67.9 | 2.9 / 6.4 / 8.3 | 1251 | 35.4 | 88 % | 213 | 21 / 35 | 43.1 (0.55) | 58.3 | 90 |
| before | 10 % | conflict 412 | 430 | 31.0 / 62.0 / 68.6 | 29.7 / 67.1 | 2.3 / 5.4 / 6.9 | 1519 | 35.7 | 89 % | 213 | 20 / 35 | 32.0 (0.00) | 45.4 | 0 |
| before | 50 % | conflict 412 | 331 | 23.6 / 37.8 / 62.0 | 22.0 / 60.5 | 2.2 / 5.4 / 7.0 | 1583 | 35.8 | 89 % | 213 | 20 / 35 | 32.8 (0.00) | 45.5 | 0 |
## run 2 native (host load 5.07 4.64 4.27)
# native server pid 390836: pid 390836's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p390836-i386300.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| native | 0 % | none | 329 | 46.5 / 80.7 / 94.7 | - | 3.1 / 8.5 / 12.0 | 1077 | 37.1 | 92 % | 186 | 16 / 28 | 22.4 (0.05) | 32.1 | 0 |
| native | 1 % | statement | 457 | 31.9 / 57.8 / 65.0 | 28.6 / 53.8 | 2.4 / 5.5 / 7.1 | 1491 | 35.8 | 89 % | 197 | 15 / 29 | 31.2 (0.00) | 42.8 | 0 |
| native | 10 % | statement | 392 | 34.9 / 64.4 / 74.5 | 28.4 / 62.5 | 2.4 / 5.7 / 7.4 | 1465 | 35.7 | 89 % | 213 | 15 / 29 | 33.2 (0.00) | 44.0 | 0 |
| native | 50 % | statement | 201 | 44.8 / 66.2 / 81.6 | 33.8 / 63.8 | 2.9 / 6.2 / 7.9 | 1272 | 35.4 | 88 % | 211 | 20 / 32 | 42.0 (0.05) | 54.8 | 0 |
| native | 10 % | conflict 412 | 437 | 30.6 / 59.5 / 66.3 | 29.4 / 64.2 | 2.3 / 5.5 / 7.0 | 1510 | 35.7 | 89 % | 212 | 17 / 32 | 30.9 (0.00) | 44.0 | 0 |
| native | 50 % | conflict 412 | 336 | 23.2 / 47.4 / 59.2 | 21.7 / 57.3 | 2.2 / 5.3 / 6.9 | 1591 | 36.0 | 89 % | 210 | 17 / 32 | 31.6 (0.00) | 44.9 | 0 |
## run 3 before (host load 4.63 4.60 4.31)
# before server pid 391564: pid 391564's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p391564-i331175.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 330 | 43.2 / 78.6 / 93.3 | - | 3.1 / 8.6 / 12.7 | 1077 | 37.1 | 92 % | 190 | 13 / 28 | 23.4 (0.05) | 32.8 | 0 |
| before | 1 % | statement | 449 | 32.5 / 59.8 / 66.3 | 28.4 / 60.7 | 2.4 / 5.6 / 7.5 | 1476 | 35.6 | 89 % | 199 | 15 / 29 | 32.3 (0.00) | 45.5 | 20 |
| before | 10 % | statement | 392 | 35.1 / 64.4 / 73.6 | 29.1 / 60.9 | 2.4 / 5.7 / 7.6 | 1452 | 35.6 | 89 % | 214 | 16 / 31 | 34.5 (0.00) | 46.2 | 60 |
| before | 50 % | statement | 199 | 44.9 / 67.8 / 82.8 | 34.3 / 64.9 | 2.9 / 6.3 / 8.1 | 1272 | 35.3 | 88 % | 212 | 21 / 33 | 42.8 (0.05) | 56.9 | 127 |
| before | 10 % | conflict 412 | 434 | 30.6 / 60.5 / 67.3 | 29.3 / 65.6 | 2.4 / 5.5 / 7.1 | 1501 | 35.7 | 89 % | 213 | 18 / 33 | 32.1 (0.00) | 44.7 | 0 |
| before | 50 % | conflict 412 | 331 | 23.5 / 48.9 / 61.8 | 22.1 / 59.7 | 2.2 / 5.4 / 6.9 | 1596 | 35.9 | 89 % | 213 | 19 / 33 | 32.8 (0.00) | 45.4 | 0 |
## run 3 native (host load 4.21 4.50 4.32)
# native server pid 392705: pid 392705's current affinity list: 2,4; Cpus_allowed_list 2,4; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p392705-i331210.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| native | 0 % | none | 319 | 45.2 / 84.5 / 96.2 | - | 3.2 / 8.9 / 12.6 | 1035 | 37.2 | 92 % | 187 | 13 / 28 | 21.6 (0.05) | 33.0 | 0 |
| native | 1 % | statement | 446 | 32.6 / 59.6 / 67.9 | 26.2 / 67.1 | 2.3 / 5.5 / 7.3 | 1493 | 35.8 | 89 % | 197 | 15 / 29 | 30.8 (0.00) | 44.1 | 0 |
| native | 10 % | statement | 390 | 35.2 / 64.2 / 72.9 | 28.9 / 63.4 | 2.4 / 5.7 / 7.6 | 1456 | 35.6 | 89 % | 214 | 16 / 29 | 33.1 (0.00) | 44.8 | 0 |
| native | 50 % | statement | 197 | 45.2 / 69.4 / 86.5 | 34.3 / 66.3 | 2.9 / 6.4 / 8.2 | 1249 | 35.3 | 88 % | 215 | 19 / 34 | 41.3 (0.30) | 58.5 | 0 |
| native | 10 % | conflict 412 | 427 | 31.3 / 62.8 / 70.1 | 30.0 / 68.4 | 2.3 / 5.5 / 7.2 | 1519 | 35.7 | 89 % | 216 | 19 / 34 | 30.5 (0.00) | 46.3 | 0 |
| native | 50 % | conflict 412 | 332 | 23.4 / 48.4 / 59.8 | 22.1 / 58.4 | 2.2 / 5.5 / 7.1 | 1550 | 35.8 | 89 % | 216 | 18 / 34 | 31.1 (0.00) | 47.7 | 0 |
```

## Session 4: the vendored, fixed driver (provider path) against the native runner

After the user's decision to vendor the Turso .NET driver and fix it (`src/vendor/Turso.Data.Sqlite`, patches 0001-0003:
a failed statement is disposed by the call that ran it, and a transaction it ended is marked rolled back), TerraBase can
use the provider's own commands and transactions again. Builds: before `c4b15068` (#109, no fix), native `7d22d0d4`
(the fixed driver, TerraBase still on its own native runner), provider `de7ed025` (the fixed driver, the provider path,
the native runner removed).

Standard configuration (COS): the server on the two SMT siblings of one core. Read from the server process in every run:

```text
# before server pid 450572: pid 450572's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p450572-i458183.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
```

`systemd-run --user --scope -p CPUQuota=200% -p MemoryMax=512M taskset -c 2,3` (CPUs 2 and 3 = core 1); load generator
on CPUs 6-9, nice 19; host load 1 to 5; same method as above; 3 runs per build, medians. Runs 1 to 3 interleaved before,
native, provider; run 3 of native and provider was re-run (same builds, same configuration) after I removed the
benchmark directory under the first attempt of run 3 native.

| failing writes | kind | variant | valid writes/s | write p50 / p95 / p99 ms | read p50 / p95 / p99 ms | reads/s | CPU % of allowed | peak RSS MB | max GC heap / committed MB | GC/s (gen2/s) | GC pause ms/s | unexpected (3 runs) |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 % | none | before | 221 | 73.7 / 118.7 / 137.1 | 4.2 / 11.7 / 16.9 | 785 | 96 % | 190 | 13 / 28 | 16.8 (0.05) | 23.8 | 0 |
| 0 % | none | native | 223 | 72.5 / 116.1 / 135.2 | 4.1 / 11.7 / 16.6 | 800 | 96 % | 187 | 13 / 28 | 16.4 (0.05) | 23.0 | 0 |
| 0 % | none | provider | 216 | 74.4 / 115.9 / 145.3 | 4.3 / 11.8 / 16.9 | 776 | 96 % | 186 | 13 / 28 | 16.4 (0.05) | 25.0 | 0 |
| 1 % | statement | before | 341 | 43.2 / 72.5 / 85.9 | 2.9 / 6.6 / 8.7 | 1215 | 95 % | 201 | 15 / 29 | 26.3 (0.00) | 36.6 | 17 |
| 1 % | statement | native | 345 | 42.8 / 71.1 / 90.0 | 2.9 / 6.5 / 8.7 | 1217 | 95 % | 196 | 15 / 29 | 25.4 (0.00) | 34.2 | 0 |
| 1 % | statement | provider | 340 | 43.0 / 72.7 / 87.3 | 2.9 / 6.7 / 8.9 | 1203 | 95 % | 195 | 15 / 29 | 26.2 (0.00) | 37.0 | 0 |
| 10 % | statement | before | 300 | 46.4 / 76.9 / 87.2 | 3.0 / 6.7 / 8.9 | 1190 | 95 % | 214 | 15 / 30 | 28.1 (0.00) | 37.9 | 97 |
| 10 % | statement | native | 299 | 46.1 / 77.6 / 89.3 | 3.0 / 6.6 / 8.7 | 1200 | 95 % | 214 | 16 / 29 | 27.0 (0.00) | 35.0 | 0 |
| 10 % | statement | provider | 294 | 46.8 / 80.2 / 91.2 | 3.0 / 6.7 / 9.0 | 1184 | 95 % | 212 | 15 / 29 | 27.8 (0.00) | 37.6 | 0 |
| 50 % | statement | before | 150 | 60.5 / 84.6 / 102.9 | 3.5 / 7.3 / 9.3 | 1047 | 95 % | 216 | 22 / 34 | 34.4 (0.35) | 46.0 | 184 |
| 50 % | statement | native | 149 | 60.7 / 85.7 / 105.3 | 3.5 / 7.1 / 9.0 | 1066 | 95 % | 215 | 20 / 35 | 33.2 (0.20) | 43.1 | 0 |
| 50 % | statement | provider | 148 | 61.2 / 85.2 / 105.5 | 3.6 / 7.2 / 9.2 | 1047 | 95 % | 212 | 21 / 34 | 34.1 (0.20) | 45.9 | 0 |
| 10 % | conflict 412 | before | 331 | 40.6 / 76.5 / 83.6 | 2.8 / 6.4 / 8.2 | 1263 | 95 % | 217 | 20 / 35 | 26.3 (0.00) | 36.3 | 0 |
| 10 % | conflict 412 | native | 332 | 40.6 / 75.4 / 84.3 | 2.8 / 6.3 / 8.1 | 1275 | 95 % | 215 | 19 / 34 | 25.4 (0.00) | 34.3 | 0 |
| 10 % | conflict 412 | provider | 332 | 41.0 / 72.5 / 80.6 | 2.8 / 6.5 / 8.4 | 1244 | 95 % | 212 | 19 / 34 | 26.2 (0.00) | 37.2 | 0 |
| 50 % | conflict 412 | before | 253 | 30.5 / 57.9 / 73.2 | 2.7 / 6.4 / 8.1 | 1299 | 95 % | 217 | 20 / 35 | 26.7 (0.00) | 37.4 | 0 |
| 50 % | conflict 412 | native | 256 | 30.4 / 60.8 / 72.4 | 2.7 / 6.3 / 8.1 | 1308 | 95 % | 215 | 19 / 34 | 25.8 (0.00) | 34.8 | 0 |
| 50 % | conflict 412 | provider | 254 | 30.9 / 57.9 / 69.5 | 2.7 / 6.5 / 8.3 | 1279 | 95 % | 212 | 19 / 34 | 26.5 (0.00) | 38.2 | 0 |

- native and provider are the same in every mix (throughput, latency percentiles, reads, CPU, peak RSS, GC heap, GC
  count and pause) and the same as before; neither forces a collection; neither gives a wrong answer, where before gave
  17, 97, 184. TerraBase keeps the provider path: the same result with less code and no reflection into the driver.

### Raw rows, session 4

```text
## run 1 before (host load 1.35 0.29 0.09)
# before server pid 450572: pid 450572's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p450572-i458183.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 221 | 73.7 / 118.7 / 137.1 | - | 4.2 / 11.7 / 16.9 | 785 | 38.8 | 96 % | 190 | 13 / 28 | 16.8 (0.05) | 25.1 | 0 |
| before | 1 % | statement | 345 | 42.7 / 71.8 / 87.9 | 35.4 / 58.3 | 2.9 / 6.6 / 8.8 | 1215 | 38.2 | 95 % | 201 | 15 / 29 | 26.4 (0.00) | 38.6 | 3 |
| before | 10 % | statement | 300 | 46.3 / 76.9 / 87.2 | 38.5 / 77.8 | 2.9 / 6.6 / 8.6 | 1207 | 38.3 | 95 % | 213 | 15 / 30 | 28.3 (0.00) | 37.9 | 29 |
| before | 50 % | statement | 150 | 60.5 / 84.6 / 97.9 | 46.0 / 77.2 | 3.5 / 7.3 / 9.3 | 1048 | 38.0 | 95 % | 216 | 22 / 34 | 34.4 (0.35) | 46.0 | 49 |
| before | 10 % | conflict 412 | 331 | 40.5 / 76.5 / 84.8 | 38.9 / 80.3 | 2.8 / 6.3 / 8.2 | 1278 | 38.2 | 95 % | 217 | 20 / 35 | 26.5 (0.00) | 36.2 | 0 |
| before | 50 % | conflict 412 | 257 | 30.2 / 56.2 / 73.3 | 28.5 / 72.0 | 2.7 / 6.4 / 8.0 | 1306 | 38.3 | 95 % | 217 | 20 / 35 | 26.9 (0.00) | 36.5 | 0 |
## run 1 native (host load 3.79 1.89 0.73)
# native server pid 450870: pid 450870's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p450870-i444468.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| native | 0 % | none | 226 | 72.5 / 121.3 / 139.3 | - | 4.1 / 11.7 / 16.6 | 800 | 38.8 | 96 % | 186 | 12 / 28 | 16.4 (0.05) | 24.3 | 0 |
| native | 1 % | statement | 345 | 42.7 / 70.8 / 80.5 | 39.1 / 107.3 | 2.9 / 6.5 / 8.5 | 1234 | 38.3 | 95 % | 196 | 15 / 29 | 25.5 (0.00) | 34.2 | 0 |
| native | 10 % | statement | 299 | 46.1 / 76.9 / 89.3 | 39.1 / 76.9 | 2.9 / 6.6 / 8.8 | 1200 | 38.3 | 95 % | 215 | 16 / 30 | 27.0 (0.00) | 35.4 | 0 |
| native | 50 % | statement | 149 | 60.5 / 85.7 / 105.3 | 46.1 / 79.5 | 3.5 / 7.1 / 9.0 | 1069 | 38.1 | 95 % | 216 | 22 / 35 | 33.4 (0.30) | 43.9 | 0 |
| native | 10 % | conflict 412 | 335 | 40.6 / 72.7 / 80.5 | 39.0 / 78.1 | 2.8 / 6.3 / 8.0 | 1275 | 38.3 | 95 % | 217 | 19 / 35 | 25.5 (0.00) | 35.0 | 0 |
| native | 50 % | conflict 412 | 257 | 30.4 / 59.5 / 72.4 | 28.5 / 68.7 | 2.7 / 6.3 / 8.1 | 1300 | 38.3 | 95 % | 217 | 19 / 35 | 25.8 (0.00) | 34.8 | 0 |
## run 1 provider (host load 4.50 3.01 1.32)
# provider server pid 451176: pid 451176's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p451176-i444493.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| provider | 0 % | none | 222 | 74.4 / 115.0 / 145.3 | - | 4.1 / 11.4 / 16.2 | 808 | 38.7 | 96 % | 182 | 13 / 28 | 17.1 (0.05) | 25.0 | 0 |
| provider | 1 % | statement | 340 | 42.9 / 74.0 / 95.0 | 37.3 / 96.2 | 2.9 / 6.7 / 8.9 | 1208 | 38.3 | 95 % | 192 | 14 / 29 | 26.2 (0.00) | 37.7 | 0 |
| provider | 10 % | statement | 296 | 46.8 / 77.1 / 91.2 | 39.2 / 79.7 | 3.0 / 6.9 / 9.0 | 1179 | 38.2 | 95 % | 212 | 15 / 29 | 27.8 (0.00) | 39.7 | 0 |
| provider | 50 % | statement | 149 | 61.2 / 85.2 / 105.5 | 46.2 / 78.5 | 3.6 / 7.2 / 9.3 | 1047 | 38.0 | 95 % | 212 | 21 / 34 | 34.1 (0.30) | 46.3 | 0 |
| provider | 10 % | conflict 412 | 332 | 40.9 / 72.5 / 80.6 | 38.9 / 76.1 | 2.9 / 6.5 / 8.4 | 1236 | 38.2 | 95 % | 212 | 19 / 35 | 26.2 (0.00) | 37.2 | 0 |
| provider | 50 % | conflict 412 | 254 | 30.9 / 60.0 / 71.8 | 29.1 / 70.0 | 2.7 / 6.5 / 8.3 | 1279 | 38.3 | 95 % | 212 | 19 / 35 | 26.5 (0.00) | 38.2 | 0 |
## run 2 before (host load 4.48 3.61 1.79)
# before server pid 451470: pid 451470's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p451470-i444528.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 223 | 73.6 / 114.8 / 131.5 | - | 4.2 / 11.4 / 16.7 | 785 | 38.8 | 96 % | 191 | 13 / 28 | 16.9 (0.05) | 23.8 | 0 |
| before | 1 % | statement | 341 | 43.3 / 72.5 / 81.6 | 37.6 / 74.6 | 2.9 / 6.5 / 8.6 | 1221 | 38.3 | 95 % | 201 | 15 / 29 | 26.3 (0.00) | 35.8 | 4 |
| before | 10 % | statement | 301 | 46.4 / 74.2 / 87.2 | 38.3 / 73.2 | 3.0 / 6.7 / 8.9 | 1181 | 38.1 | 95 % | 214 | 15 / 30 | 28.1 (0.00) | 37.2 | 41 |
| before | 50 % | statement | 151 | 60.1 / 83.5 / 102.9 | 45.6 / 79.9 | 3.5 / 7.3 / 9.3 | 1047 | 38.1 | 95 % | 212 | 24 / 36 | 34.5 (0.35) | 46.0 | 72 |
| before | 10 % | conflict 412 | 334 | 40.6 / 73.1 / 79.8 | 38.9 / 78.4 | 2.9 / 6.4 / 8.1 | 1244 | 38.3 | 95 % | 212 | 21 / 36 | 26.3 (0.00) | 36.6 | 0 |
| before | 50 % | conflict 412 | 253 | 30.7 / 57.9 / 70.5 | 29.2 / 68.8 | 2.7 / 6.3 / 8.1 | 1298 | 38.3 | 95 % | 213 | 21 / 36 | 26.7 (0.00) | 37.6 | 0 |
## run 2 native (host load 4.45 3.97 2.19)
# native server pid 451836: pid 451836's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p451836-i471360.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| native | 0 % | none | 211 | 79.5 / 116.1 / 134.5 | - | 4.4 / 11.9 / 16.9 | 754 | 38.8 | 96 % | 187 | 13 / 28 | 15.4 (0.05) | 22.6 | 0 |
| native | 1 % | statement | 341 | 42.9 / 73.0 / 90.0 | 39.5 / 83.8 | 2.9 / 6.6 / 8.7 | 1217 | 38.2 | 95 % | 196 | 15 / 29 | 25.1 (0.00) | 34.4 | 0 |
| native | 10 % | statement | 295 | 46.8 / 80.3 / 92.3 | 39.4 / 75.7 | 3.0 / 6.7 / 8.7 | 1196 | 38.2 | 95 % | 214 | 16 / 29 | 26.8 (0.00) | 35.0 | 0 |
| native | 50 % | statement | 148 | 60.7 / 86.8 / 105.8 | 46.2 / 80.1 | 3.5 / 7.1 / 9.1 | 1063 | 38.1 | 95 % | 215 | 20 / 35 | 33.2 (0.20) | 43.1 | 0 |
| native | 10 % | conflict 412 | 331 | 40.9 / 75.4 / 84.3 | 39.6 / 80.1 | 2.8 / 6.3 / 8.2 | 1262 | 38.3 | 95 % | 215 | 19 / 34 | 25.2 (0.00) | 34.3 | 0 |
| native | 50 % | conflict 412 | 254 | 30.8 / 60.8 / 73.4 | 28.9 / 72.2 | 2.7 / 6.3 / 8.0 | 1308 | 38.3 | 95 % | 215 | 19 / 34 | 25.6 (0.00) | 33.8 | 0 |
## run 2 provider (host load 4.29 4.09 2.49)
# provider server pid 452180: pid 452180's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p452180-i435557.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| provider | 0 % | none | 213 | 74.4 / 122.5 / 145.4 | - | 4.3 / 11.8 / 16.9 | 776 | 38.8 | 96 % | 188 | 12 / 28 | 16.3 (0.05) | 23.4 | 0 |
| provider | 1 % | statement | 343 | 43.0 / 71.8 / 87.3 | 37.2 / 92.9 | 2.9 / 6.7 / 8.9 | 1203 | 38.3 | 95 % | 198 | 15 / 29 | 26.2 (0.00) | 34.4 | 0 |
| provider | 10 % | statement | 294 | 46.8 / 80.2 / 90.2 | 39.7 / 80.2 | 2.9 / 6.6 / 8.8 | 1209 | 38.3 | 95 % | 212 | 15 / 29 | 28.0 (0.00) | 36.5 | 0 |
| provider | 50 % | statement | 148 | 61.2 / 84.5 / 105.5 | 46.4 / 80.2 | 3.5 / 7.2 / 9.0 | 1066 | 38.1 | 95 % | 211 | 20 / 34 | 34.1 (0.20) | 44.4 | 0 |
| provider | 10 % | conflict 412 | 332 | 41.0 / 72.3 / 81.4 | 38.9 / 80.4 | 2.8 / 6.5 / 8.3 | 1246 | 38.3 | 95 % | 211 | 19 / 34 | 26.3 (0.00) | 36.1 | 0 |
| provider | 50 % | conflict 412 | 256 | 30.6 / 57.9 / 69.3 | 28.8 / 67.6 | 2.7 / 6.4 / 8.1 | 1279 | 38.2 | 95 % | 211 | 19 / 34 | 26.7 (0.00) | 36.9 | 0 |
## run 3 before (host load 4.93 4.40 2.84)
# before server pid 452438: pid 452438's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p452438-i444634.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| before | 0 % | none | 212 | 79.0 / 127.5 / 158.9 | - | 4.4 / 12.1 / 17.1 | 760 | 38.8 | 96 % | 187 | 13 / 28 | 16.2 (0.05) | 23.0 | 0 |
| before | 1 % | statement | 341 | 43.2 / 72.8 / 85.9 | 40.6 / 86.7 | 2.9 / 6.6 / 8.7 | 1211 | 38.3 | 95 % | 198 | 15 / 29 | 26.2 (0.00) | 36.6 | 10 |
| before | 10 % | statement | 295 | 47.1 / 79.4 / 86.5 | 38.8 / 74.7 | 3.0 / 6.8 / 9.1 | 1190 | 38.2 | 95 % | 216 | 17 / 30 | 27.9 (0.00) | 39.1 | 27 |
| before | 50 % | statement | 149 | 61.0 / 85.9 / 104.1 | 46.1 / 81.8 | 3.5 / 7.3 / 9.4 | 1047 | 38.1 | 95 % | 217 | 21 / 34 | 34.1 (0.30) | 45.7 | 63 |
| before | 10 % | conflict 412 | 327 | 41.3 / 77.1 / 83.6 | 39.7 / 82.5 | 2.8 / 6.4 / 8.4 | 1263 | 38.3 | 95 % | 218 | 20 / 35 | 26.2 (0.00) | 36.3 | 0 |
| before | 50 % | conflict 412 | 253 | 30.5 / 59.6 / 73.2 | 29.1 / 70.2 | 2.7 / 6.5 / 8.1 | 1299 | 38.3 | 95 % | 218 | 20 / 35 | 26.7 (0.00) | 37.4 | 0 |
## run 3 native, re-run (host load 2.77 4.19 3.25)
# native server pid 454076: pid 454076's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p454076-i458632.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| native | 0 % | none | 223 | 72.5 / 116.0 / 135.2 | - | 4.0 / 11.1 / 16.2 | 812 | 38.7 | 96 % | 187 | 13 / 28 | 16.4 (0.05) | 23.0 | 0 |
| native | 1 % | statement | 345 | 42.8 / 71.1 / 92.2 | 39.1 / 95.2 | 2.9 / 6.5 / 8.7 | 1216 | 38.3 | 95 % | 192 | 15 / 29 | 25.4 (0.00) | 33.5 | 0 |
| native | 10 % | statement | 300 | 46.1 / 77.6 / 88.6 | 38.9 / 74.0 | 3.0 / 6.6 / 8.7 | 1205 | 38.3 | 95 % | 210 | 15 / 29 | 27.1 (0.00) | 34.6 | 0 |
| native | 50 % | statement | 149 | 61.0 / 85.6 / 104.2 | 46.0 / 77.1 | 3.5 / 7.1 / 9.0 | 1066 | 38.1 | 95 % | 210 | 19 / 34 | 33.2 (0.05) | 42.5 | 0 |
| native | 10 % | conflict 412 | 332 | 40.4 / 77.2 / 84.8 | 38.6 / 82.0 | 2.8 / 6.3 / 8.1 | 1278 | 38.3 | 95 % | 210 | 18 / 34 | 25.4 (0.00) | 33.9 | 0 |
| native | 50 % | conflict 412 | 256 | 30.3 / 61.2 / 71.2 | 28.5 / 70.3 | 2.7 / 6.3 / 8.1 | 1308 | 38.3 | 95 % | 210 | 18 / 34 | 25.8 (0.00) | 35.0 | 0 |
## run 3 provider, re-run (host load 4.42 4.36 3.45)
# provider server pid 454416: pid 454416's current affinity list: 2,3; Cpus_allowed_list 2-3; cgroup /user.slice/user-1000.slice/user@1000.service/app.slice/run-p454416-i458678.scope; cpu.max 200000 100000; cpuset.cpus.effective (no such file: controller not enabled here); memory.max 536870912; memory.swap.max max; runtime CPU count 2
| provider | 0 % | none | 216 | 78.4 / 115.9 / 143.9 | - | 4.3 / 12.1 / 17.5 | 765 | 38.8 | 96 % | 186 | 13 / 29 | 16.4 (0.05) | 25.1 | 0 |
| provider | 1 % | statement | 340 | 43.3 / 72.7 / 86.0 | 39.7 / 80.0 | 3.0 / 6.7 / 8.9 | 1198 | 38.3 | 95 % | 195 | 15 / 29 | 26.1 (0.00) | 37.0 | 0 |
| provider | 10 % | statement | 292 | 47.0 / 80.7 / 98.0 | 39.3 / 75.8 | 3.0 / 6.7 / 9.0 | 1184 | 38.2 | 95 % | 214 | 15 / 29 | 27.7 (0.00) | 37.6 | 0 |
| provider | 50 % | statement | 148 | 60.9 / 86.5 / 104.7 | 46.4 / 80.7 | 3.6 / 7.3 / 9.2 | 1039 | 38.0 | 94 % | 212 | 21 / 34 | 33.8 (0.20) | 45.9 | 0 |
| provider | 10 % | conflict 412 | 329 | 41.2 / 74.9 / 80.5 | 39.6 / 80.0 | 2.8 / 6.5 / 8.5 | 1244 | 38.2 | 95 % | 212 | 18 / 33 | 26.2 (0.00) | 38.7 | 0 |
| provider | 50 % | conflict 412 | 253 | 30.9 / 53.1 / 69.5 | 29.2 / 68.4 | 2.8 / 6.5 / 8.3 | 1265 | 38.2 | 95 % | 212 | 18 / 33 | 26.4 (0.00) | 38.8 | 0 |
```
