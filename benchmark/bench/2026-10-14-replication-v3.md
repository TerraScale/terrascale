# TerraBase V3 shape load — 2026-10-14

## 0. Current result: rerun at `7bf8dd3a` (run live6)

- **V3: FAIL. Still BLOCKED on FR-22 (PR #283, open) and FR-24 (not written).** ts-vtest-tb ran `7bf8dd3a`
  (`fix/shape-frontier-stuck-vtest`: PR #307 memory fix + PR #309 frontier fix). The cluster was not redeployed.
- **Readers are now admitted:** 1 of 1, 100 of 100, 875 of 1,000, 1,489 of 10,000.
- **Deliveries:** N = 1 and N = 100 delivered every in-window write (600 and 60,000; 0 missed). At N = 1,000, 875
  readers were admitted but only 11,373 of 379,750 expected deliveries arrived. At N = 10,000 nothing was delivered.
- **Two nodes crashed under the 10,000-reader step.** fra (`48e4967a225338`) exited at 18:58:37.6Z and sjc
  (`6835466b44e258`) at 18:58:47.3Z, both with exit code 134 (SIGABRT, `restarting: true`, `restart_count: 1`), about
  45 s into the N = 10,000 ramp. Fly did not report OOM. The node logs (last 100 lines) do not reach back to the crash,
  so the cause is not captured. gru did not restart. Both nodes were back by 18:58:48Z.
- **Bound broken on every node at every measured step.** Reads per sealed object per node: 171 to 167,813 (bound: 1).
- **Refused requests still cost storage reads.** At `a439e1d1`, every request was refused and the nodes made about
  2.1 `shape` reads per request (N = 1,000: 354,795 reads over 171,191 refusals). At `7bf8dd3a`, N = 1,000 made about
  2.0 `shape` + `shape_body` reads per request (493,293 over 246,345 requests, 189,731 of them 429). Served and refused
  requests share the counter, so this is an upper bound per refusal, not an isolated cost. The PR #307 pre-check did not
  visibly lower reads per request.

### Live run at `7bf8dd3a` (live6), per step

| N | admitted | rejected | writes | delivered | missed | p50 ms | p99 ms | statuses | load CPU % gru/sjc/fra | load RSS MB gru/sjc/fra |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 0 | 600 | 600 | 0 | 15,770 | 30,409 | 200: 8, 307: 1, 503: 1 | 1 / 0 / 0 | 12 / 6 / 6 |
| 100 | 100 | 0 | 600 | 60,000 | 0 | 374 | 29,950 | 200: 11,438, 307: 100, 503: 34 | 1 / 3 / 3 | 15 / 14 / 14 |
| 1,000 | 875 | 125 | 434 | 11,373 | 368,377 | 1,561 | 15,483 | 200: 55,550, 307: 1,000, 429: 189,731, 503: 64 | 17 / 20 / 1 | 33 / 27 / 29 |
| 10,000 | 1,489 | 8,511 | 0 | 0 | n/a | n/a | n/a | 200: 4,032, 307: 3,177, 429: 120,729, 500: 104, 503: 4,134 | 1 / 0 / 0 | 86 / 16 / 17 |

Refusal reasons:
- 503 unavailable, "the shape head is not remote durable yet": 1 at N = 1, 34 at N = 100, 64 at N = 1,000.
- At N = 10,000, 61 × `503 unavailable` carry no message.
- 429 shape_busy, "shape reads are at capacity": 189,731 at N = 1,000 and 120,729 at N = 10,000.
- At N = 10,000: 4,034 × `503 storage_busy`, 104 × `500 internal_error` with no message, and 39 × `503 unavailable`
  ("shape storage changed or is busy").

Other notes:
- At N = 1,000 the writer failed with `503 write_outcome_unknown` ("no other region confirmed the write within 10 s"),
  so only 434 writes landed in the window.
- At N = 10,000 there were no in-window writes, so missed and latency are n/a.
- At N = 1, the p50 of 15.8 s comes from a single reader. That reader got one `503 unavailable` (head not remote
  durable). This run does not show whether that 503 caused the lag.

| N | node | sealed objects | `shape` reads | `shape_body` reads | `point` reads | reads per object per location | parked | node CPU % | node RSS MB |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | gru | 3 | 14,752 | 3 | 5,198 | 4,918 | 1 | 40 | 331 |
| 1 | sjc | 0 | 171 | 0 | 4,647 | 171 | 0 | 16 | 347 |
| 1 | fra | 0 | 173 | 0 | 4,684 | 173 | 0 | 21 | 348 |
| 100 | gru | 2 | 16,805 | 104 | 5,703 | 8,455 | 34 | 39 | 340 |
| 100 | sjc | 179 | 67,992 | 211 | 7,689 | 381 | 33 | 64 | 340 |
| 100 | fra | 152 | 52,135 | 184 | 7,422 | 344 | 33 | 81 | 338 |
| 1,000 | gru | 0 | 21,653 | 608 | 7,824 | 22,261 | 64 | 77 | 343 |
| 1,000 | sjc | 65 | 281,391 | 21,828 | 9,918 | 4,665 | 0 | 122 | 307 |
| 1,000 | fra | 1 | 155,214 | 12,599 | 12,061 | 167,813 | 58 | 19 | 347 |
| 10,000 | gru | 1 | 40,134 | 500 | 33,545 | 40,634 | 0 | 24 | 377 |
| 10,000 | sjc | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a |
| 10,000 | fra | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a |

- At N = 10,000, sjc and fra have no row: they restarted mid-step, so their scrapes failed. The merged JSON shows
  zeros for them, which are not measurements.
- The machines are shared-cpu-2x with 512 MB. The largest window-end RSS was 377 MB, on gru at N = 10,000. Node CPU
  above 100 % (sjc, 122 %) means both vCPUs were in use.
- Load machines `86540d2be19418` (gru), `48e1405b501e98` (sjc) and `080ee129c32558` (fra) ran image
  `registry.fly.io/ts-vtest-load:v3-20261014184724@sha256:08ffe35d6484da843b7b16cf217639b8807bc6b3850778b7fe82abf3a636eb11`,
  with the harness binary from `3f5d1c6a`.
- Timeline: start 18:51:53Z, end 19:01:57Z. The tenant (organization `01M4XVTTT3TRT18YKB6QFTP0R9`) and key
  `01M4XVV4ACCRG6M76T1SGNBJ3N` were deleted and revoked. App `ts-vtest-load` was destroyed at 19:02:31Z.
- Evidence: `2026-10-14-replication-v3/evidence/live6/`. It holds the shard records, load and merge logs, the ledger,
  ts-vtest-tb machine JSON before and after (with the exit events), and the node log tails.

## 1. History: result at `a439e1d1` (runs live4, live5)

- **V3: FAIL. BLOCKED on the shape memory-budget fix (F-V2-1), FR-22 (PR #283, open) and FR-24 (not written).** No PASS
  claim is possible until all three are merged and live.
- **Live (ts-vtest-tb, gru/sjc/fra, run live4): 0 readers admitted at every step (N = 1, 100, 1,000, 10,000).** Every
  shape request answered `429 shape_busy` ("shape reads are at capacity; retry later", reason recorded in run live5).
  At N = 10,000, 6,949 readers also got `503 storage_busy`. The coordinator reports the cause: the node memory budget
  (gru memory_used 358 MB against a 120 MB budget on 512 MB machines) refuses every shape at `a439e1d1`.
- **Bound broken on every node at every step**, live and local. No sealed object was published live, yet each node made
  132 to 144,445 `shape`-path storage reads per step. Locally, where shapes are served, the origin node made 616 to
  2,441 `shape` + `shape_body` reads per sealed object (bound: 1).
- **Tigris (T1) is not a cache location in any run.** `terrabase.shape.mirror.lag` stayed at 0 (FR-22 is not deployed).

## 2. Live run (live4)

Writer: 20 documents/s into gru (600 in the window). Readers use their own region's node over `.internal`. The tenant is
on the enterprise plan (no QoS request budget), so every refusal is a shape cap or storage refusal, not `tenant_busy`.

| N | admitted | rejected | writes | delivered | missed | refused before admission (repeats) | reader failures | statuses | load CPU % gru/sjc/fra | load RSS MB gru/sjc/fra |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 0 | 1 | 600 | 0 | 0 | 308 | 0 | 429: 309 | 1 / 0 / 0 | 12 / 6 / 6 |
| 100 | 0 | 100 | 600 | 0 | 0 | 19,726 | 0 | 429: 19,826 | 1 / 0 / 0 | 14 / 12 / 12 |
| 1,000 | 0 | 1,000 | 600 | 0 | 0 | 170,191 | 0 | 429: 171,191 | 1 / 0 / 0 | 22 / 22 / 26 |
| 10,000 | 0 | 10,000 | 600 | 0 | 0 | 163,473 | 6,949 | 429: 165,837; 503: 6,949 | 1 / 0 / 0 | 71 / 68 / 67 |

Latency, 304 ratio and missed deliveries are not measurable with no admitted reader (missed counts admitted readers
only). Load CPU is sampled in the 30 s window, after every reader had given up at the 70 s admission deadline.

| N | node | sealed objects | `shape` reads | `shape_body` reads | `point` reads (node-wide) | reads per object per location | parked | node CPU % | node RSS MB | verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | gru | 0 | 2,907 | 0 | 4,969 | 2,907 | 0 | 32 | 346 | FAIL |
| 1 | sjc | 0 | 132 | 0 | 3,884 | 132 | 0 | 15 | 333 | FAIL |
| 1 | fra | 0 | 138 | 0 | 4,045 | 138 | 0 | 20 | 337 | FAIL |
| 100 | gru | 0 | 27,385 | 0 | 5,605 | 27,385 | 0 | 31 | 353 | FAIL |
| 100 | sjc | 0 | 32,112 | 0 | 4,460 | 32,112 | 0 | 14 | 337 | FAIL |
| 100 | fra | 0 | 32,660 | 0 | 4,706 | 32,660 | 0 | 20 | 340 | FAIL |
| 1,000 | gru | 0 | 144,445 | 0 | 7,647 | 144,445 | 0 | 31 | 351 | FAIL |
| 1,000 | sjc | 0 | 123,101 | 0 | 6,235 | 123,101 | 0 | 15 | 332 | FAIL |
| 1,000 | fra | 0 | 87,249 | 0 | 11,432 | 87,249 | 0 | 20 | 347 | FAIL |
| 10,000 | gru | 0 | 37,628 | 0 | 44,950 | 37,628 | 0 | 26 | 354 | FAIL |
| 10,000 | sjc | 0 | 23,669 | 0 | 34,673 | 23,669 | 0 | 12 | 360 | FAIL |
| 10,000 | fra | 0 | 13,124 | 0 | 14,743 | 13,124 | 0 | 18 | 366 | FAIL |

- With no sealed object, "reads per object" is the read count over a denominator of 1. The `shape` reads come from
  refused bootstraps and background shape work; this run does not split them.
- At N = 10,000 the `shape` reads fall below N = 1,000 because 6,949 readers stopped on `503 storage_busy`.
- Node CPU is `dotnet.process.cpu.time` over the window; RSS is the working set at window end (machines: shared-cpu-2x,
  512 MB).

## 3. Local validation (harness check, not V3 evidence)

`mariogk-dev`, local two-node host (gru, sjc, loopback), one load process, harness `455e0a40`. All readers on gru.

| N | admitted | delivered | missed | p50 ms | p99 ms | statuses | gru sealed | gru `shape` / `shape_body` | reads per object | sjc `shape` (0 sealed) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 600 | 0 | 748 | 1,449 | 200: 115, 307: 1 | 106 | 65,216 / 106 | 616 | 23 |
| 10 | 10 | 6,000 | 0 | 741 | 1,397 | 200: 1,264, 307: 11, 409: 1 | 100 | 65,504 / 137 | 656 | 23 |
| 100 | 79 | 45,782 | 1,618 | 13,189 | 27,017 | 200: 2,349, 307: 102, 409: 2, 429: 6,178, 503: 63 | 23 | 55,677 / 476 | 2,441 | 23 |

- The `409 must_refetch` answers ("The shape offset is below the retention floor", on a collection created minutes earlier) were
  restarted at `offset=-1` and caught up. They are unexpected with a 7-day retention and are a separate finding.
- The 304 ratio is 0 everywhere: followers send no `If-None-Match`, so T0 revalidation is not exercised.

## 4. Harness

`benchmark/cmd/shape-bench` (`load` and `merge` subcommands, README "Live load (V3)"):

- One load process per Fly machine (`-shard i -shards k`), steps on a shared wall clock, readers on their own region's
  node (`<FLY_REGION>.<app>.internal:8080`); shard 0 writes and scrapes every node found through
  `regions.<app>.internal` (port 9091). The key is the `TS_BENCH_API_KEY` Fly secret.
- Per step: admitted, rejected and unresolved readers, delivered and missed writes, p50/p99, statuses, refusal reasons,
  304 ratio, load CPU/RSS; per node: sealed and mirrored objects, storage reads by path, parked gauge, CPU, RSS.
- `merge` exits non-zero and prints each node over the bound (`shape` + `shape_body` reads > sealed + mirrored objects),
  and each shard error. Tigris counts as a location once mirrored objects appear (FR-22).
- Not measured: reads served by Tigris itself (no mirror deployed), clock skew between load machines (latency crosses
  machines).

## 5. Provenance

| item | value |
| --- | --- |
| Branch | `feat/repl-v3-shape-load` |
| Live run live4 | 2026-10-14T17:38:31Z to 17:48:16Z; harness `c3b7160f`, binary sha256 `1f237132…3256` |
| live4 load image | `registry.fly.io/ts-vtest-load:v3-20261014173412@sha256:7c344c26…db65` |
| live4 load machines | gru `d8946d4b355408`, sjc `8d31e3ce14d2d8`, fra `e784ee304c2d48` (shared-cpu-2x, 2048 MB) |
| Diagnostic run live5 (N = 1, refusal reasons) | 2026-10-14T17:54:12Z; harness `3f5d1c6a`, image `sha256:7482fb4d…45e5`; machines `8ed915ec7d9118`, `185d467f933938`, `4d8911096a6048` |
| Cluster | `ts-vtest-tb` (terrascale-previews-515), revision `a439e1d17ddcc43df9e714eeb3c6afc6dc9187bd`, image `vtest-a439e1d17ddc@sha256:b2267cc0…c804`; gru `891e70eb4d0378`, sjc `6835466b44e258`, fra `48e4967a225338` (shared-cpu-2x, 512 MB) |
| Local run | `2026-10-14-v3-01M4XPS52TA319RC0SH108DVK2`, log sha256 `4707393c…f667` |
| Raw logs | `2026-10-14-replication-v3/evidence/` (shard records, load and merge logs, ledgers); checksums in `evidence/SHA256SUMS` |
| Run script | `2026-10-14-replication-v3/scripts/run.sh <shape-bench binary> <out dir>` (`STEPS` overrides the steps) |

## 6. Resource ledger

All in org terrascale-previews-515. ts-vtest-tb was read and used, never changed.

| resource | created | final state |
| --- | --- | --- |
| App `ts-vtest-load` | 17:21:43Z | destroyed 17:58:08Z (with its images and the staged secret) |
| App `ts-vtest-load` (live6, recreated) | 18:47:21Z | destroyed 19:02:31Z; machines `86540d2be19418`, `48e1405b501e98`, `080ee129c32558` destroyed 19:01:56Z |
| live6 tenant (org `01M4XVTTT3TRT18YKB6QFTP0R9`, database `01M4XVTTTRP6BTKGNKK0CPAW2E`) and key `01M4XVV4ACCRG6M76T1SGNBJ3N` | 18:47:17Z | deleted and revoked 19:01:57Z |
| Load machines: `e82794da120d48` (live1), `287e354c736e98` (probe), live4 ×3, live5 ×3 (ids above) | 17:23 to 17:50Z | all destroyed (ledgers in `evidence/live*`) |
| Tenants on ts-vtest-tb, one per attempt live1 to live5: organization, project, database `v3` (gru + sjc, fra) | 17:21 to 17:49Z | all databases, projects and organizations deleted |
| API keys `01M4XPYAXTBAG3X8W3TQ6HHTJH`, `01M4XQ45XY6JMSWHZZ5DW546BF`, `01M4XQF5YR2SQJVDAA387BV0GF`, `01M4XQMXADF78M833NJ1P0FCXQ`, `01M4XRHHQ84VG4R92VPQ7AXDGF` (documents read/write, one database, 6 h) | per attempt | all revoked. Live2's key was revoked by hand after its secret import failed |

Attempts live1 to live3 measured nothing: live1 failed on a `flyctl machine run --autostop off` parse, live2 and live3 on
the secret import. They are kept in `evidence/` as ledgers only.

## 7. Next

Rerun once the shape memory-budget fix, FR-22 and FR-24 are live on ts-vtest-tb:
`benchmark/bench/2026-10-14-replication-v3/scripts/run.sh <static shape-bench> <out>`.
