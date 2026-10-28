# SQL point reads: key index (#781) and per-database read cache, 2026-10-28

## Changes measured

1. **#781 (merged): point reads and writes by a single TEXT primary key use the document id index.** Before it, a
   relational read wrapped every table in a CTE that Turso does not flatten, so `SELECT … WHERE k = ?` scanned and
   decoded the whole table.
2. **This change: a per-database cache of parsed reads and compiled templates** (docs/terrabase/sql/read-cache.md).
   - A `perf` profile of the SQL point read after #781 put about half of the SQL slice's samples in SqlParserCS's
     visitor, which finds each node's children by reflection on every walk: validation, guard and coercion. Tokenize
     and parse took about 10% more.
   - SQL coercion (#793) now records its parameter copies in the template, so the compiled text does not depend on the
     arguments.

Both use the P1 SQL lanes in `benchmark/dotnet/VsTurso/SurfaceLanes.cs`:
- a TEXT primary key table with a JSON body column and 20,000 preloaded rows;
- one statement per request;
- HTTP `…/sql/execute`, Hrana `…/libsql/v2/pipeline`, and pooled unprepared Npgsql.

## 1. Key index, beta, sequential arms, 3 repeats each

Host beta, 12 vCPU, quiet (load about 1–4 of 12), one `ts-heavy-any` job per arm on the same host. Before is P1's
harness at `606445d75`; after is the same plus #781. Medians, min–max, 0 errors.

| Lane | Size | c | Ops/s before | Ops/s after | p50 µs before → after | p99 µs after | Alloc B/op before → after |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| sql | small | 1 | 5 (3–5) | 956 (954–957) | 211,193 → 930 | 2,225 | 9,256,467 → 275,304 |
| hrana | small | 1 | 5 (4–5) | 975 (959–979) | 210,384 → 916 | 2,045 | 9,247,242 → 267,963 |
| pg | small | 1 | 5 (4–5) | 748 (740–754) | 209,203 → 1,222 | 2,521 | 9,192,075 → 305,935 |
| sql | small | 16 | 9 (7–9) | 4,148 (4,144–4,153) | 1,640,514 → 4,252 | 6,876 | 9,972,296 → 276,435 |
| sql | medium | 1 | 5 (4–5) | 899 (897–914) | 209,814 → 976 | 2,343 | 9,284,485 → 315,393 |

The read cache, sequential on beta (before = #781, after = #781 + the cache before its rebase onto #793), gave:

| Cell | p50 µs | ops/s | CPU µs/op | Alloc B/op |
| --- | ---: | ---: | ---: | ---: |
| sql small c=1 | 930 → 573 | 956 → 1,625 | 1,207 → 765 | 275,304 → 118,712 |
| sql small c=16 | 4,252 → 1,794 | 4,148 → 6,832 | 1,497 → 785 | 276,435 → 119,238 |

## 2. Read cache on current main, mariogk-dev, interleaved, 3 repeats

- **Arms:** one `ts-heavy-any` job built `origin/main` `30022135f` (with #781 and #793) in a side worktree and this branch.
  It alternated them per repeat.
- **Host:** mariogk-dev, 46 vCPU, IO pressure near 0.
- **CPU contention:** the host was busy with other jobs, so even raw Turso moved by up to about 25% between arms.
  Allocation per read is the steadiest column. Latency and throughput are directional, not a calibrated host estimate.

| Size | c | Lane | Ops/s before | Ops/s after | p50 µs before → after | p99 µs before → after | CPU µs/op before → after | Alloc B/op before → after | Errors |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| small | 1 | turso | 9,463 (9,139–12,947) | 12,112 (10,636–14,326) | 102 → 77 | 153 → 133 | 102 → 82 | 2,176 → 2,172 | 0 |
| small | 1 | sql | 301 (206–384) | 556 (441–586) | 2,838 → 1,676 | 9,082 → 4,257 | 3,422 → 2,003 | 260,805 → 121,718 | 0 |
| small | 1 | hrana | 357 (239–388) | 560 (392–602) | 2,471 → 1,677 | 6,731 → 3,992 | 3,015 → 2,028 | 253,381 → 114,904 | 0 |
| small | 1 | pg | 295 (250–302) | 452 (407–524) | 3,195 → 2,084 | 6,862 → 4,973 | 3,763 → 2,540 | 273,521 → 97,913 | 0 |
| small | 1 | tcp-echo | 12,759 (4,953–13,634) | 14,989 (13,159–15,031) | 69 → 60 | 195 → 131 | 87 → 80 | 927 → 923 | 0 |
| small | 16 | turso | 113,128 (59,115–139,628) | 137,357 (125,197–147,041) | 103 → 102 | 564 → 150 | 107 → 103 | 2,174 → 2,169 | 0 |
| small | 16 | sql | 1,869 (741–2,211) | 2,872 (2,477–2,946) | 8,681 → 4,388 | 23,818 → 13,254 | 3,302 → 2,014 | 258,987 → 121,207 | 0 |
| small | 16 | hrana | 1,590 (709–2,145) | 2,930 (2,883–2,977) | 9,403 → 4,321 | 31,275 → 13,380 | 3,404 → 1,935 | 252,044 → 113,999 | 0 |
| small | 16 | pg | 1,162 (1,081–1,610) | 1,641 (1,624–1,697) | 13,253 → 8,936 | 35,576 → 18,398 | 4,048 → 2,153 | 271,807 → 96,987 | 0 |
| small | 16 | tcp-echo | 85,094 (72,475–137,618) | 75,364 (21,521–89,753) | 116 → 137 | 1,086 → 1,433 | 68 → 68 | 901 → 896 | 0 |
| medium | 1 | turso | 8,364 (8,012–10,496) | 7,771 (7,728–9,941) | 104 → 114 | 200 → 199 | 117 → 127 | 13,699 → 13,703 | 0 |
| medium | 1 | sql | 200 (122–367) | 335 (206–436) | 4,288 → 2,610 | 14,204 → 9,096 | 4,768 → 3,103 | 303,009 → 162,785 | 0 |
| medium | 1 | hrana | 284 (122–343) | 432 (153–458) | 3,033 → 2,064 | 10,054 → 5,910 | 3,563 → 2,484 | 273,396 → 134,708 | 0 |
| medium | 1 | pg | 221 (139–299) | 355 (312–377) | 3,906 → 2,464 | 12,543 → 9,923 | 4,527 → 3,019 | 297,335 → 121,471 | 0 |
| medium | 1 | tcp-echo | 12,106 (8,291–12,767) | 10,097 (8,333–12,454) | 67 → 74 | 210 → 468 | 90 → 100 | 913 → 920 | 0 |
| medium | 16 | turso | 80,402 (43,243–92,785) | 73,513 (28,778–83,261) | 141 → 148 | 1,853 → 2,229 | 144 → 151 | 13,687 → 13,687 | 0 |
| medium | 16 | sql | 1,540 (1,058–1,924) | 2,152 (1,077–2,232) | 10,568 → 5,741 | 25,762 → 17,312 | 3,605 → 2,327 | 298,813 → 160,913 | 0 |
| medium | 16 | hrana | 1,414 (1,409–2,024) | 2,411 (1,840–2,464) | 10,821 → 5,311 | 31,181 → 15,244 | 3,572 → 2,111 | 271,232 → 133,386 | 0 |
| medium | 16 | pg | 1,340 (1,035–1,422) | 1,484 (1,078–1,564) | 12,101 → 9,845 | 21,664 → 19,219 | 3,787 → 2,443 | 294,912 → 120,082 | 0 |
| medium | 16 | tcp-echo | 108,762 (62,677–155,680) | 92,058 (36,890–100,970) | 99 → 110 | 1,143 → 1,221 | 64 → 64 | 905 → 896 | 0 |

- **Allocation per SQL read:** about −53% for HTTP SQL and Hrana and −64% for PostgreSQL. This is deterministic per
  request, so host load does not move it.
- **CPU per read:** −34% to −47%, against −20% to +9% drift on the raw controls.
- **Latency:** c=1 p50 −32% to −41% and c=16 throughput +11% to +84%, against up to ±25% on the controls.
- **Q-PARITY:** SQL point reads are still far above the 1.1 × (raw Turso + TCP echo) read target. On beta, c=1 small is
  573 µs against about 50 µs. What remains:
  - the HTTP/JSON request path (or Hrana, or the PG protocol);
  - the session and storage transaction per request;
  - a fresh Turso prepare of each relational program;
  - the CTE and guard functions per row.

