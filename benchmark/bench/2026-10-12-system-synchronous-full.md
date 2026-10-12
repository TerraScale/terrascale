# System database writes with `synchronous = FULL` (decision 0012, 2026-10-12)

Decision 0012 makes every write transaction of the system database (`_system.db`: catalog, API keys, management commits)
sync its write-ahead log at commit. This measures what that costs a system database write, and checks that tenant writes
(still `synchronous = NORMAL`, decision 0003) are unchanged.

## Builds

- before: `4cfbf85d` (main). after: `fix/system-synchronous-full` (main + #143 + the 0012 change; `src/` as in its PR). Both
  `dotnet build -c Release` of `src/TerraBase`, same SDK, same ts-dotnet.

## Measured configuration (the server process itself)

| Item | Value |
| --- | --- |
| Host | beta: AMD Ryzen 5 PRO 6650H, 12 logical CPUs, 14 GB RAM, Samsung MZVLB512HAJQ NVMe (ext4 data directory under `/tmp` on it), kernel 7.2.9, .NET 11.0.0-rc.1.26425.128; host load average 3.5 to 10.2 during the runs (other users' processes) |
| Launch | `systemd-run --user --scope -p CPUQuota=200% -p MemoryMax=512M taskset -c 2,4 dotnet terrabase.dll --storage-threads=2` (Development environment, internal listener) |
| Read from the server process every run | affinity `2,4`, `cpu.max` `200000 100000` (2 CPUs), `memory.max` `536870912`, runtime CPU count 2 |
| Load generator | `benchmark/dotnet/DrainBench --mode system` in its own process, `taskset -c 6-9 nice -n 19`, HTTP/2 to the internal listener |

## Method

One fresh server per run; per phase 3 s warm-up then 20 s measured; each writer sends its next request when it has the
answer to the previous one. Phases:

- **api key**: `POST /admin/api-keys` (one `_system` write transaction).
- **management commit**: `POST /v1/management/commits` with one `create_organization` (one `_system` write transaction).
- **tenant put**: `PUT` of a new document in a tenant database (the control: unchanged by 0012).

Latency per request at the client; CPU is the server's user + system time as a share of its 2 CPUs. Order: before,
after, three times. Every request of every phase succeeded (0 unexpected statuses).

## Results (medians of 3 runs)

| write | writers | before writes/s | after writes/s | before p50 / p95 / p99 ms | after p50 / p95 / p99 ms | CPU before → after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| api key | 1 | 270 | 130 | 2.9 / 7.8 / 17.2 | 7.1 / 11.3 / 14.2 | 52 % → 32 % |
| api key | 16 | 333 | 141 | 41.1 / 74.3 / 81.2 | 112.5 / 128.4 / 138.3 | 47 % → 26 % |
| management commit | 1 | 107 | 78 | 7.8 / 18.5 / 27.9 | 11.9 / 17.9 / 21.1 | 47 % → 35 % |
| management commit | 16 | 107 | 75 | 144.5 / 228.2 / 306.5 | 195.8 / 274.3 / 288.9 | 46 % → 34 % |
| tenant put (control) | 1 | 343 | 342 | 2.5 / 3.8 / 16.1 | 2.5 / 3.5 / 15.5 | 46 % → 46 % |
| tenant put (control) | 16 | 584 | 607 | 24.8 / 49.5 / 56.7 | 24.4 / 48.0 / 59.2 | 60 % → 59 % |

## Reading

- A system database write now waits for one WAL fsync: about +4 ms at p50 on this NVMe (api key 2.9 → 7.1 ms,
  management commit 7.8 → 11.9 ms). p99 does not grow (17.2 → 14.2, 27.9 → 21.1 ms): the commit's own fsync replaces the
  checkpoint-time syncs that used to land on an unlucky request.
- System writes are serialized on the database's writer, so 16 writers queue behind one fsync each: api keys top out at
  about 140/s, management commits at about 75/s. Catalog writes are low-volume (one per key, per organization, per
  commit); the server is idle meanwhile (CPU 26 % to 35 % of its 2 CPUs).
- Tenant writes are unchanged (within run-to-run noise).
- Run 2's "after" ran while the host load average reached 10.2 (its tenant control dropped to 165/s at 1 writer as
  well); the medians of 3 are robust to that one run.

## Raw runs

Run order and host load at each run's end:

- run 1 before done 2026-10-12T18:46:29Z load: 5.05 6.52 6.04 4/643 641596
- run 1 after done 2026-10-12T18:50:00Z load: 3.47 5.46 5.77 5/656 643357
- run 2 before done 2026-10-12T18:55:26Z load: 4.07 4.63 5.28 6/692 647240
- run 2 after done 2026-10-12T18:58:59Z load: 10.20 7.24 6.16 7/908 651165
- run 3 before done 2026-10-12T19:05:29Z load: 4.84 6.42 6.29 4/915 655892
- run 3 after done 2026-10-12T19:09:04Z load: 4.72 5.98 6.19 4/871 659129

| run | variant | write | writers | writes/s | p50 / p95 / p99 ms | CPU % of allowed | unexpected |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | before | api key | 1 | 235 | 3.3 / 8.8 / 17.7 | 51 % | 0 |
| 1 | before | api key | 16 | 289 | 53.0 / 74.3 / 82.1 | 47 % | 0 |
| 1 | before | management commit | 1 | 93 | 9.3 / 19.6 / 25.1 | 48 % | 0 |
| 1 | before | management commit | 16 | 110 | 140.0 / 228.2 / 306.5 | 48 % | 0 |
| 1 | before | tenant put | 1 | 343 | 2.5 / 3.8 / 17.2 | 46 % | 0 |
| 1 | before | tenant put | 16 | 591 | 24.8 / 49.5 / 56.7 | 60 % | 0 |
| 1 | after | api key | 1 | 132 | 7.0 / 11.2 / 14.2 | 32 % | 0 |
| 1 | after | api key | 16 | 143 | 110.7 / 127.7 / 137.4 | 26 % | 0 |
| 1 | after | management commit | 1 | 83 | 11.3 / 16.8 / 18.8 | 35 % | 0 |
| 1 | after | management commit | 16 | 75 | 192.0 / 270.7 / 287.2 | 33 % | 0 |
| 1 | after | tenant put | 1 | 347 | 2.5 / 3.5 / 15.5 | 46 % | 0 |
| 1 | after | tenant put | 16 | 612 | 23.8 / 48.0 / 59.2 | 59 % | 0 |
| 2 | before | api key | 1 | 282 | 2.6 / 7.8 / 16.9 | 52 % | 0 |
| 2 | before | api key | 16 | 363 | 37.7 / 57.5 / 63.7 | 48 % | 0 |
| 2 | before | management commit | 1 | 113 | 7.7 / 17.5 / 27.9 | 47 % | 0 |
| 2 | before | management commit | 16 | 107 | 147.0 / 192.7 / 281.1 | 46 % | 0 |
| 2 | before | tenant put | 1 | 348 | 2.5 / 3.6 / 15.6 | 46 % | 0 |
| 2 | before | tenant put | 16 | 584 | 24.7 / 50.6 / 64.0 | 59 % | 0 |
| 2 | after | api key | 1 | 123 | 7.4 / 12.1 / 15.5 | 32 % | 0 |
| 2 | after | api key | 16 | 124 | 128.6 / 149.3 / 230.4 | 30 % | 0 |
| 2 | after | management commit | 1 | 58 | 16.3 / 26.4 / 34.2 | 37 % | 0 |
| 2 | after | management commit | 16 | 45 | 345.0 / 485.5 / 533.4 | 35 % | 0 |
| 2 | after | tenant put | 1 | 165 | 5.2 / 13.1 / 17.5 | 40 % | 0 |
| 2 | after | tenant put | 16 | 515 | 28.4 / 51.4 / 60.9 | 58 % | 0 |
| 3 | before | api key | 1 | 270 | 2.9 / 7.7 / 17.2 | 52 % | 0 |
| 3 | before | api key | 16 | 333 | 41.1 / 74.6 / 81.2 | 47 % | 0 |
| 3 | before | management commit | 1 | 107 | 7.8 / 18.5 / 32.9 | 45 % | 0 |
| 3 | before | management commit | 16 | 106 | 144.5 / 240.6 / 307.6 | 46 % | 0 |
| 3 | before | tenant put | 1 | 323 | 2.7 / 3.8 / 16.1 | 45 % | 0 |
| 3 | before | tenant put | 16 | 582 | 25.4 / 48.6 / 54.8 | 60 % | 0 |
| 3 | after | api key | 1 | 130 | 7.1 / 11.3 / 14.1 | 33 % | 0 |
| 3 | after | api key | 16 | 141 | 112.5 / 128.4 / 138.3 | 26 % | 0 |
| 3 | after | management commit | 1 | 78 | 11.9 / 17.9 / 21.1 | 35 % | 0 |
| 3 | after | management commit | 16 | 75 | 195.8 / 274.3 / 288.9 | 34 % | 0 |
| 3 | after | tenant put | 1 | 342 | 2.5 / 3.4 / 14.0 | 46 % | 0 |
| 3 | after | tenant put | 16 | 607 | 24.4 / 47.6 / 52.7 | 59 % | 0 |
