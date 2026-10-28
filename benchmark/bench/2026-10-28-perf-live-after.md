# Live after-numbers in gru, sjc and fra, and the dashui:20 cold-open 502, 2026-10-28

## What was live

| Region | TerraBase image | Built from | TerraBase cgroup `cpu.max` | Host vCPUs | `/readyz` |
| --- | --- | --- | --- | ---: | --- |
| gru | `25cc3dd45d8a` | main `d0f3672fc` (#782) | 300000 100000 (3 CPUs) | 4 | ready |
| sjc | `25cc3dd45d8a` | main `d0f3672fc` (#782) | 150000 100000 (1.5 CPUs) | 2 | ready |
| fra | `25cc3dd45d8a` | main `d0f3672fc` (#782) | 150000 100000 (1.5 CPUs) | 2 | ready |

- **Image:** the digest is the one #782's main CI run published (job `main (terrabase)`, run 40765139577). It contains
  #781 (SQL key index), #845 (intent journal) and #852 (SQL read cache).
- **CPU limits:** read from each running container's cgroup after ts-orchestrator #220 merged at 20:35Z.

## Method

- **Probe:** `benchmark/scripts/perf-live-writes.sh` (a link to `perf-live.sh`, running `perf-live-writes.py`) runs on
  each serving host in turn: gru, then fra, then sjc.
- **Tenant:** a synthetic organization, project, database and documents key, created and deleted through the local
  region's internal listener. The database is placed in its region and a pair (gru+fra, fra+gru, sjc+gru), because a
  database needs two regions.
- **Cells:** 16 documents per size (256 B and 4 KiB). Then, per repeat and size:
  - 60 c=1 point reads per surface: document GET, HTTP SQL and Hrana, using P1's live statement
    `SELECT value FROM <table> WHERE id = '<id>'`;
  - 60 c=1 `local_durable` writes over HTTP PUT and Wire.
  Operations are paced at most 10/s, with three repeats.
- **Clients:** persistent, on the host-local client listener (127.0.0.1:8080) and the local Wire TLS edge (7464).
- **Comparison:** P1's live baseline of 2026-10-27, taken on images before #781
  (`perf-baseline-20261027/live-summary.md`), with the same statements, sizes, pacing and c=1.

## Results

### gru (2026-10-28T20:57:18Z, database in gru+fra, host load 2.1/2.7/2.7)

| Kind | Size | Surface | p50 ms per repeat | p99 ms per repeat | Median p50 ms | P1 live p50 ms (2026-10-27) |
| --- | --- | --- | --- | --- | ---: | ---: |
| read | small | document | 1.1, 1.0, 1.1 | 6, 8, 20 | 1.13 | 1.69 |
| read | small | HTTP SQL | 3.2, 2.8, 4.4 | 13, 13, 96 | 3.21 | 7.47 |
| read | small | Hrana | 3.0, 2.9, 3.9 | 15, 19, 30 | 3.03 | 8.51 |
| read | medium | document | 1.1, 1.1, 1.1 | 9, 14, 10 | 1.10 | 1.75 |
| read | medium | HTTP SQL | 2.9, 2.9, 3.2 | 13, 17, 16 | 2.93 | 7.95 |
| read | medium | Hrana | 3.2, 2.9, 3.2 | 19, 8, 13 | 3.23 | 6.51 |
| write | small | document PUT | 10.4, 11.3, 11.3 | 34, 28, 34 | 11.27 |  |
| write | small | Wire | 9.5, 8.8, 9.0 | 26, 31, 32 | 8.96 |  |
| write | medium | document PUT | 10.5, 10.7, 9.8 | 24, 27, 25 | 10.51 |  |
| write | medium | Wire | 9.2, 9.4, 9.3 | 86, 24, 36 | 9.29 |  |

Teardown: revoke key 204, delete database 204, delete project 204, delete organization 204, organization gone fra 404, organization gone gru 404, organization gone sjc 404

### fra (2026-10-28T21:00:42Z, database in fra+gru, host load 3.6/3.0/2.9)

| Kind | Size | Surface | p50 ms per repeat | p99 ms per repeat | Median p50 ms | P1 live p50 ms (2026-10-27) |
| --- | --- | --- | --- | --- | ---: | ---: |
| read | small | document | 1.2, 1.0, 1.0 | 15, 24, 22 | 1.02 | 1.33 |
| read | small | HTTP SQL | 5.6, 3.0, 3.3 | 34, 22, 20 | 3.27 | 6.27 |
| read | small | Hrana | 3.9, 3.3, 3.8 | 19, 21, 20 | 3.80 | 5.66 |
| read | medium | document | 1.1, 1.1, 1.1 | 12, 18, 29 | 1.12 | 1.23 |
| read | medium | HTTP SQL | 3.1, 7.7, 7.0 | 33, 906, 248 | 7.02 | 7.81 |
| read | medium | Hrana | 4.3, 9.3, 3.6 | 891, 124, 54 | 4.28 | 6.39 |
| write | small | document PUT | 13.2, 13.8, 12.2 | 52, 73, 36 | 13.16 |  |
| write | small | Wire | 12.3, 14.2, 12.2 | 57, 81, 34 | 12.34 |  |
| write | medium | document PUT | 24.3, 12.3, 11.6 | 433, 38, 47 | 12.25 |  |
| write | medium | Wire | 15.1, 10.1, 11.4 | 42, 41, 57 | 11.41 |  |

Teardown: revoke key 204, delete database 204, delete project 204, delete organization 204, organization gone fra 404, organization gone gru 404, organization gone sjc 404

### sjc (2026-10-28T21:04:18Z, database in sjc+gru, host load 2.7/2.5/2.8)

| Kind | Size | Surface | p50 ms per repeat | p99 ms per repeat | Median p50 ms | P1 live p50 ms (2026-10-27) |
| --- | --- | --- | --- | --- | ---: | ---: |
| read | small | document | 1.2, 1.1, 1.4 | 15, 18, 21 | 1.18 | 1.55 |
| read | small | HTTP SQL | 6.0, 4.9, 5.1 | 33, 81, 38 | 5.11 | 8.09 |
| read | small | Hrana | 5.7, 5.5, 4.3 | 70, 34, 1130 | 5.46 | 8.29 |
| read | medium | document | 1.2, 1.2, 1.3 | 16, 26, 69 | 1.25 | 1.39 |
| read | medium | HTTP SQL | 4.4, 4.5, 4.2 | 36, 31, 28 | 4.41 | 7.25 |
| read | medium | Hrana | 3.5, 4.2, 5.0 | 15, 25, 36 | 4.23 | 6.63 |
| write | small | document PUT | 17.1, 17.6, 13.4 | 74, 1072, 111 | 17.09 |  |
| write | small | Wire | 17.5, 13.9, 13.8 | 101, 82, 59 | 13.88 |  |
| write | medium | document PUT | 17.3, 14.9, 15.9 | 81, 31, 64 | 15.93 |  |
| write | medium | Wire | 13.9, 12.9, 16.7 | 41, 101, 44 | 13.85 |  |

Teardown: revoke key 204, delete database 204, delete project 204, delete organization 204, organization gone fra 404, organization gone gru 404, organization gone sjc 404

## Summary

- **SQL point reads (median p50), against P1's live baseline:**
  - gru: HTTP SQL 7.47 → 3.21 ms (small) and 7.95 → 2.93 ms (medium); Hrana 8.51 → 3.03 ms and 6.51 → 3.23 ms.
  - fra: HTTP SQL 6.27 → 3.27 ms (small) and 7.81 → 7.02 ms (medium, noisy: repeats 3.1, 7.7, 7.0); Hrana 5.66 → 3.80 ms
    and 6.39 → 4.28 ms.
  - sjc: HTTP SQL 8.09 → 5.11 ms and 7.25 → 4.41 ms; Hrana 8.29 → 5.46 ms and 6.63 → 4.23 ms.
- **Document reads:** stay at about 1.0–1.3 ms p50 everywhere. P1 had 1.2–1.7 ms.
- **What the numbers cover:** the SQL gains are #781 and #852 as measured locally, plus whatever else changed between
  the two days' images. A live A/B of one commit was not possible, so the per-change speedups remain the local
  measurements in `2026-10-28-perf-sql-read-cache.md` and `2026-10-28-perf-engine-intent-capture.md`.
- **Writes (no P1 live baseline):** c=1 `local_durable` writes take 9–11 ms p50 in gru, 11–13 ms in fra and 14–17 ms in sjc,
  each into a two-region database. This afternoon's gru runs on #845's image measured 90–97 ms
  (`2026-10-28-perf-engine-live-writes.md`), during sjc's instability and on uncapped hosts. The gap between local writes
  and live writes is still real: 1.5 ms single node, 20–34 ms in the local three-region cell.
- **Not reached:** Q-PARITY for SQL. The live c=1 SQL p50 is still about 3× a document read.

## dashui:20: the 30.5 s 502 on a cold database overview (18:31Z)

- **No ts-auth in gru:** at 18:31Z gru ran no ts-auth process at all. Its container logged "Application is shutting
  down" at 12:02:39Z, and the next one started at 18:43:59Z (ts-orch runtime logs on ts-gru-1). This is inside the
  declared gru sign-in P0 (about 17:43–19:16Z).
- **Timeouts:** the dashboard's `platform` (ts-auth) and TerraBase HTTP clients both time out at 30 s
  (`ts-dashboard` `DashboardApplication.cs`). Its database pages build their account context through ts-auth and read
  TerraBase with a project token exchanged at ts-auth.
- **TerraBase:** at 18:31Z gru's TerraBase had been up since 18:14:57Z on #852's image. The request's own log lines
  could not be recovered: the dashboard and TerraBase logs on gru are bounded rings and no longer reached 18:31Z when
  read. Exactly which upstream call timed out is therefore not proven.
- **TerraBase's cold open is not the cost.** `benchmark/dotnet/VsTurso/ColdOpenBenchmark.cs` runs on a local gru/sjc/fra
  cluster on delta through `ts-heavy-any`. It creates three gru+fra databases, then times the overview's TerraBase reads:
  the database record, collections and the backup list. It repeats all of this with sjc stopped.

| Phase | Create ms | First record / collections / backups ms | Second record / collections / backups ms |
| --- | --- | --- | --- |
| all regions up | 110, 96, 111 | 2.7 / 17.2 / 25.7; 3.4 / 2.1 / 1.6; 10.3 / 4.0 / 4.1 | 1.2 / 1.0 / 1.4; 0.8 / 0.6 / 1.1; 2.1 / 1.2 / 5.9 |
| sjc stopped | 33, 24, 27 | 2.6 / 1.4 / 1.5; 2.2 / 2.0 / 3.0; 3.0 / 2.0 / 2.5 | 1.1 / 1.0 / 1.5; 1.6 / 1.8 / 1.4; 0.9 / 1.2 / 1.6 |

  Every response was 200 (201 for creates). The slowest first read was 25.7 ms, three orders of magnitude below 30 s.

- **Conclusion:** dashui:20 belongs to the gru outage window, not to a cold-open cost to cut.
- **Live side effects of that window:**
  - an organization create through fra's internal listener hung 30 s and created nothing (20:02Z);
  - creates of gru+fra databases in gru were refused with 409 `database_limit`, because part of the allowance was held
    in unreachable sjc;
  - a single-region database is refused outright (400 `placement_needs_two_regions`).
  These are recorded here for P3 and dashui:19 and not changed.

## Runs not used

- **20:05Z fra:** the run failed when fra's internal listener refused, during a restart. Its tenant was revoked and deleted
  through fra once fra was ready (organization 404 in every region).
- **19:53Z fra and 19:54Z gru** (images of #855 and #854, hosts uncapped):
  - fra was at load 4.6 on 2 vCPUs and gru at load 6.0 on 4 vCPUs;
  - gru SQL small was 72–98 ms and writes about 100 ms; fra SQL small 14.5–282 ms.
  They show a saturated cluster, not the code. Both tenants were deleted and confirmed 404 in all regions.
- **Every other synthetic tenant of the day** was removed and checked absent in gru, sjc and fra. This includes those
  stranded when sjc or fra refused a teardown; they were reaped through the local region later.
