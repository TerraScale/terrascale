# TerraBase free reads (RT2 / RT2c lane R) — 2026-10-23

Goal: N readers of one shape cost about one storage read per change per cache location, for T0 (origin flight
collapse, closed-chunk cache and ETag), T1 (bucket objects) and T2 (Cloudflare shape edge). Readers N = 1, 10, 100,
1000. Object store: proven on Tigris, then switched to Cloudflare R2 with configuration only.

Sections 1–4b are RT2c's runs. Sections 5–8 are RT2's measured tables, kept as measured, each labelled with its binary.

## 1. Summary

- **T0 at the head (RT2c, dev host, 3 repeats): payload reads follow sealed chunks, not readers, for N = 1, 10 and 100.**
  `shape_body` per sealed chunk is 1.0 (N = 1) and about 1.2 (N = 10 and 100, after one bootstrap page per reader);
  document `point` reads stay at 3.9–4.1k per step from N = 1 to N = 1000. Every write reached every reader for
  N ≤ 100 (600, 6,000 and 60,000 deliveries), p50 0.8–1.4 s.
- **N = 1000 at the head delivered 0 writes in all 3 repeats** (all 600 writes done, all 1000 readers admitted): no
  chunk sealed during the step, and 315–363k `429 shape_busy` per step. Run alone (section 3), N = 1000 delivers all
  600,000 writes with and without the frontier seal whenever the writer keeps up: the 0 comes with the earlier steps.
- **R2: the object-store proof passes 5 of 5 on Tigris and on R2 with only the env file changed**, and the Slow T1/T2
  test (`S3ShapeMirrorTests`: mirror upload, manifest, anonymous fetch through the shape edge until a cache HIT, TBS1
  open) passes on both.
- **T1 and T2 (catch-up, 3 repeats): no origin payload read at any N on R2 (section 4) or Tigris (4b).** T2 cuts
  bucket GETs at N = 1000 from 11,000 to 2,049 on R2 (hit ratio 0.814) and to 802 on Tigris (0.927): fewer than one
  per reader, but not yet about one per object per location.

## 2. Binaries and hosts (RT2c)

| run | binary | host |
| --- | --- | --- |
| T0 live head | `ff4773804` = rt2c/reads-bench: rt2c/reads-1..7 (on main `d9d804812`) + lane P's project-token cache `772775f57` (bench only) | mariogk-dev (46 CPUs, shared): 1-min load 61.0 (r1, 10:20Z), 36.3 (r2, 10:29Z), 37.5 (r3, 10:37Z) at start |
| T0/T1/T2 catch-up on R2 | `ff4773804`, bucket `ts-rt2c-r2-bench-test` (R2, location hint `enam`), edge `shapes.terrascale.tech` (Worker version `98291f92`) | mariogk-dev, see section 4 |
| object-store proof | rt2c/reads-2 tree on main `d9d804812` | delta (16 CPUs), Tigris `ts-rt2c-tigris-test`, R2 `ts-rt2c-r2-test` |
| T0/T1/T2 catch-up on Tigris | `ff4773804`, bucket `ts-rt2c-tigris-bench-test` | mariogk-dev, see section 4b |

The R2 runs used S3 keys derived from an account API token with R2 access, not a bucket-scoped R2 token (none could be
minted with the available tokens); the bucket-scoped keys change only the env file.

`shape-bench load -steps 1,10,100,1000`: readers join over 60 s, then a writer sends 20 documents/s for 30 s, 5 s
grace, 6 s settle; local gru + sjc in one process; enterprise plan. Storage reads are `terrabase.storage.reads{path}`
deltas over the whole step on the serving node.

## 3. T0: live readers at the head (RT2c)

Median of 3 repeats (range). `per sealed` = (`shape_body` − one bootstrap page per reader) / sealed.

| N | delivered / expected | p50 / p99 ms | sealed | shape_body | per sealed | point | shape (metadata) | 429 shape_busy |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 600 / 600 (all 3) | 924 (819–1053) / 2136 (1843–5755) | 178 (167–224) | 178 (167–224) | 1.0 | 3886 (3863–3892) | 90,224 (86,796–133,128) | 0 |
| 10 | 6,000 / 6,000 (all 3) | 860 (758–897) / 1927 (1458–2206) | 205 (173–223) | 253 (209–272) | 1.2 | 4006 (3984–4012) | 134,838 (112,817–159,311) | 0 |
| 100 | 60,000 / 60,000 (all 3) | 867 (817–1400) / 2136 (1491–7782) | 185 (166–199) | 326 (304–343) | 1.2 | 4018 (4006–4033) | 497,995 (454,670–533,305) | 0 (0–18) |
| 1000 | **0 / 600,000 (all 3)** | – | **0** | 687 (682–798) | – | 4085 (4081–4091) | 629,507 (503,734–980,748) | 330,660 (315,463–363,461) |

- `point` reads no longer grow with N (RT2's "before": 445k at N = 1000): the project-token cache (lane P) is in this
  binary.
- `shape` metadata reads grow with N: frontier, head and retention reads per response (RT2 report, section 3). The
  tool's `OVER BOUND` verdict adds them to the payload reads; it is the wrong gate, as RT2 found.
- N = 1000 isolated: `-steps 1000` alone (a fresh host process with no earlier steps), 2 repeats per binary, same host.
  Alone, every binary delivers all writes when its writer keeps up, with or without the frontier seal (rt2c/reads-4); the
  0 of the full run comes with running N = 1000 after the N = 1, 10 and 100 steps in the same host process:

| binary | repeat, 1-min load at start | writes done | delivered / 600,000 | p50 / p99 ms | sealed | shape_body | 429 / 503 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `ff4773804` (bench, with reads-4) | r1, 56.9 | 600 | 600,000 | 3545 / 9904 | 72 | 904 | 3 / 0 |
| `ff4773804` | r2, 57.6 | 211 (writer timed out after 17 s) | 165,000 | 28869 / 31998 | 20 | 726 | 4 / 0 |
| `99da1621a` (bench minus reads-4) | r1, 41.0 | 600 | 600,000 | 4096 / 12272 | 17 | 889 | 5 / 0 |
| `99da1621a` | r2, 24.2 | 600 | 600,000 | 12829 / 20726 | 5 | 468 | 11 / 0 |
| `68cd06b9c` (RT2's rt2/shape-qos) | r1, 54.5 | 600 | 600,000 | 7229 / 13574 | 10 | 761 | 3 / 0 |
| `68cd06b9c` | r2, 52.4 (97.4 at end) | 295 (writer timed out) | 0 | – | 4 | 377 | 19 / 1000 |

  Every run whose writer kept up delivered all 600,000 writes; the failed repeats are the ones where the overloaded host
  timed the writer out. What the earlier steps leave behind that starves N = 1000 (330k `429 shape_busy` against 3–19
  here) is not isolated; candidates are state the earlier steps leave in the process (feed and chunk growth, lingering
  polls) meeting the per-organization flight cap (8).

## 4. T0, T1 and T2 catch-up on Cloudflare R2 (RT2c)

`shape-bench catchup -chunks 10 -repeats 3`: readers join behind the head over 10 s and read a snapshot page plus 10
sealed chunks (11 objects), checking size and sha256 against the manifest. Local gru + sjc mirror to the R2 bucket
`ts-rt2c-r2-bench-test`; T2 objects go through `shapes.terrascale.tech` from Cloudflare's Curitiba location (`CWB`).
Binary `ff4773804`, mariogk-dev, 11:09–11:21Z, 1-min load 29.9 at start and 33.4 at end, 0 failures. Median (range).

| tier | N | origin requests | origin shape_body | object GETs | bucket GETs | edge hit ratio | origin shape (metadata) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| t0 | 1 | 11 | 2 (1–2) | 0 | 0 | – | 134 (128–158) |
| t0 | 10 | 110 | 10 (10–12) | 0 | 0 | – | 1,134 (1,131–1,144) |
| t0 | 100 | 1,100 | 99 (97–102) | 0 | 0 | – | 10,901 (10,802–11,117) |
| t0 | 1000 | 11,000 | 756 (755–909) | 0 | 0 | – | 80,004 (78,849–96,835) |
| t1 | 1 | 0 (2 manifest) | 0 | 11 | 11 | – | 16 (15–21) |
| t1 | 10 | 0 (20) | 0 | 110 | 110 | – | 86 (86–93) |
| t1 | 100 | 0 (200) | 0 | 1,100 | 1,100 | – | 807 (806–812) |
| t1 | 1000 | 0 (2,000) | 0 | 11,000 | 11,000 | – | 8,006 (8,006–8,013) |
| t2 | 1 | 0 (2) | 0 | 11 | 11 | 0 | 14 (14–15) |
| t2 | 10 | 0 (20) | 0 | 110 | 33 (33–34) | 0.700 (0.691–0.700) | 86 (85–91) |
| t2 | 100 | 0 (200) | 0 | 1,100 | 260 (255–282) | 0.764 (0.744–0.768) | 811 (805–811) |
| t2 | 1000 | 0 (2,000) | 0 | 11,000 | 2,049 (1,463–2,193) | 0.814 (0.801–0.867) | 8,005 (8,004–8,005) |

- **T1 and T2 on R2: the origin reads no payload at any N**; only manifest calls reach it (2 per reader, about 4
  metadata reads each).
- **T1:** the bucket serves every object once per reader (N × 11 GETs), as on Tigris.
- **T2 on R2 costs about 1 bucket GET per 5 objects at N = 1000 (2,049 for 11,000), not about 1 per object per
  location.** RT2 measured 126 (hit ratio 0.989) on Tigris with the same Worker (section 7, other time and host load).
  The Worker's Cache API has no request collapsing: every concurrent first miss at one location goes to the bucket
  and only a completed `cache.put` turns later requests into hits, so a slower bucket answer widens the miss window.
  Not isolated further; a Worker with an R2 binding, or a collapsing cache in front, is the next candidate.
- **T0 catch-up:** payload reads grow with N here (756 at N = 1000 against 123 on RT2's run): readers that join over
  10 s mostly miss the running flight of each snapshot page, and pages are not in the closed-chunk cache.

## 4b. The same catch-up on Tigris (RT2c)

Same binary `ff4773804` and tool, bucket `ts-rt2c-tigris-bench-test` (Tigris, `t3.storage.dev`), mariogk-dev 12:01–12:17Z,
**1-min load 95.1 at start and 58.1 at end** (other lanes' builds), 0 failures. Median (range).

| tier | N | origin requests | origin shape_body | object GETs | bucket GETs | edge hit ratio | origin shape (metadata) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| t0 | 1 | 11 | 3 (3–4) | 0 | 0 | – | 128 (124–130) |
| t0 | 10 | 110 | 13 (11–13) | 0 | 0 | – | 1,128 (1,121–1,129) |
| t0 | 100 | 1,100 | 96 (86–102) | 0 | 0 | – | 10,581 (8,348–11,121) |
| t0 | 1000 | 11,000 | 466 (177–532) | 0 | 0 | – | 48,985 (17,478–53,408) |
| t1 | 1 | 0 (2 manifest) | 0 | 11 | 11 | – | 14 (14–16) |
| t1 | 10 | 0 (20) | 0 | 110 | 110 | – | 85 |
| t1 | 100 | 0 (200) | 0 | 1,100 | 1,100 | – | 811 (809–812) |
| t1 | 1000 | 0 (0–67) (2,000) | 0 (0–2) | 11,000 (10,933–11,000) | 11,000 (10,933–11,000) | – | 8,013 (8,006–8,593) |
| t2 | 1 | 0 (2) | 0 | 11 | 11 | 0 | 15 (13–22) |
| t2 | 10 | 0 (20) | 0 | 110 | 17 (17–19) | 0.845 (0.827–0.845) | 85 |
| t2 | 100 | 0 (200) | 0 | 1,100 | 114 (95–133) | 0.896 (0.879–0.914) | 806 (805–808) |
| t2 | 1000 | 0 (2,000) | 0 | 11,000 | 802 (572–824) | 0.927 (0.925–0.948) | 8,011 (8,009–8,013) |

- Tigris and R2 behave the same for T1 (one bucket GET per object per reader, no origin payload read).
- T2: Tigris needs 17 / 114 / 802 bucket GETs at N = 10 / 100 / 1000 against R2's 33 / 260 / 2,049, although this run had
  three times the host load. One Tigris repeat at N = 1000 fell back to the origin for 67 requests (t1, `0–67`).

## 5. RT2: T0 live readers, dev host (binaries `56a1b6c4c` / `0d09176f7`)

Payload reads are `path="shape_body"` on the serving node. "Per sealed" subtracts the N bootstrap page reads.

| N | before cache: shape_body / sealed | per sealed | with cache r1: shape_body / sealed | per sealed | with cache r2: shape_body / sealed | per sealed |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 5 / 5 | 0.8 | 14 / 14 | 0.9 | 86 / 86 | 1.0 |
| 10 | 56 / 6 | 7.7 | 65 / 54 | 1.0 | 67 / 56 | 1.0 |
| 100 | 1113 / 13 | 77.9 | 117 / 25 | 0.7 | 155 / 54 | 1.0 |
| 1000 | 3271 / 3 | stopped | 231 / 0 | stopped | 294 / 0 | stopped |

Before the cache: `56a1b6c4c` (server `1f5cab388`); with the cache: `0d09176f7`, host load 43 (r1) and 17.6 (r2).
N = 1000 stopped on `503 storage_busy` in every repeat (project-token lookups, fixed by lane P's cache).

## 6. RT2: T0 live readers on the OCI cells (binary `f06647f22`, enterprise plan, 3 repeats)

Readers on ts-sjc-1 against the sjc cell (replicas gru and fra). Median (range).

| N | admitted | delivered / expected | p50 / p99 ms | sealed | shape_body | point | shape |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 600 / 600 | 17711 / 32031 | 3 | 4 | 10276 (10009–10278) | 13754 (13599–13786) |
| 10 | 10 | 6,000 / 6,000 | 17973 / 32031 | 2 | 12 | 10527 (10435–10789) | 14331 (14233–14408) |
| 100 | 100 | 60,000 / 60,000 | 18039 / 32244 | 2 | 102 | 10394 (10366–10447) | 21600 (21369–21674) |
| 1000 | 1000 | 84,180 (82,940–158,469) / 600,000 | 26231 / 34210 | 2 | 618 (498–620) | 12233 (12041–12558) | 68961 (59269–69620) |

Delivery waited for the remote-durable frontier (p50 about 18 s): sealing required F = head under a steady writer.
rt2c/reads-4 (seal through F) removes that wait; not re-measured on the cells (lane V owns them).

## 7. RT2: catch-up on Tigris (binary `0d09176f7`, dev host, 3 repeats; cells `f06647f22`, 1 repeat)

Readers read a snapshot page plus 10 sealed chunks (11 objects). Dev host load 23.6 at start, 11.9 at end. Median (range).

| tier | N | origin requests | origin shape_body | object GETs | bucket GETs | edge hit ratio | origin shape (metadata) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| t0 | 1 | 11 | 3 (2–6) | 0 | 0 | – | 158 |
| t0 | 10 | 110 | 15 (14–15) | 0 | 0 | – | 1148 |
| t0 | 100 | 1,100 | 104 (103–105) | 0 | 0 | – | 11124 |
| t0 | 1000 | 11,000 | 123 (104–129) | 0 | 0 | – | 8369 |
| t1 | 1 | 0 (2 manifest) | 0 | 11 | 11 | – | 14 |
| t1 | 10 | 0 (20) | 0 | 110 | 110 | – | 86 |
| t1 | 100 | 0 (200) | 0 | 1,100 | 1,100 | – | 809 |
| t1 | 1000 | 0 (2,000) | 0 | 11,000 | 11,000 | – | 8006 |
| t2 | 1 | 0 (2) | 0 | 11 | 11 | 0 | 14 |
| t2 | 10 | 0 (20) | 0 | 110 | 16 (14–16) | 0.855 | 86 |
| t2 | 100 | 0 (200) | 0 | 1,100 | 110 (83–111) | 0.900 | 807 |
| t2 | 1000 | 0 (2,000) | 0 | 11,000 | 126 (36–709) | 0.989 | 8006 |

Live cells (`f06647f22`, sjc cell, its Tigris bucket, 6 objects per reader, 1 repeat, no spread):

| tier | N | origin requests | origin shape_body | object GETs | bucket GETs | edge hit ratio | origin shape (metadata) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| t0 | 100 | 600 | 99 | 0 | 0 | – | 5965 |
| t0 | 1000 | 6,000 | 239 | 0 | 0 | – | 11486 |
| t2 | 100 | 0 (100 manifest) | 0 | 600 | 20 | 0.967 | 447 |
| t2 | 1000 | 0 (1,000 manifest) | 0 | 6,000 | 61 | 0.990 | 4024 |

## 8. RT2: QoS and N = 1000 (before `2558412fb`, after `77ad109f8` = rt2/shape-qos, dev host, 3 repeats)

Before: rt2/free-reads-5. After: lane P's project-token cache plus the QoS change (rt2c/reads-5), without the
frontier seal (rt2c/reads-4). Median (range).

| N | run | writes done / 600 | delivered | p50 / p99 ms | 503 storage_busy | 429 shape_busy | point reads |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | before | 600 | 600 | 1998 / 4563 | 0 | 0 | 9,760 (9,721–9,784) |
| 1 | after | 600 | 600 | 696 / 1,251 | 0 | 0 | 3,853 (3,840–3,873) |
| 100 | before | 600 | 60,000 | 2,224 / 6,169 | 0 | 0 | 78,584 (69,832–95,287) |
| 100 | after | 600 | 60,000 | 723 / 1,249 | 0 | 0 | 4,000 (3,975–4,009) |
| 1000 | before | 332 (220–380), stopped | 760 (673–3,838) | 24,838 / 28,099 | 54 (24–55) | 26,870 (25,390–27,937) | 444,959 (412,618–455,705) |
| 1000 | after | 600 (all 3) | 9,249 (7,442–26,229) | 1,479 / 29,524 | 0 | 378,047 (367,763–380,491) | 4,050 (4,029–4,055) |

Hobby plan on the cells (`f06647f22`, 1 repeat): 1000 readers of one shape spent the tenant's request budget and
stalled its writer 10–32 s (429 `tenant_busy` 91,445 at N = 1000). rt2c/reads-5 (refund shared answers, separate read
lane) is proven by Fast tests, not re-measured on the cells.

## 9. Commands (RT2c)

```sh
# T0 live head, 3 repeats (rt2c/reads-bench)
TS_HEAVY_HOST=mariogk-dev ts-heavy-any bash -c 'make terrabase-build CONFIGURATION=Release && dotnet build tests/TerraBase.E2E -c Release && cd benchmark && for r in 1 2 3; do go run ./cmd/shape-bench load -steps 1,10,100,1000 -out <dir>/r$r; done' --repo terrascale --branch rt2c/reads-bench
# Catch-up on R2: the env file (mode 0600, outside the repository) holds AWS_ENDPOINT_URL_S3, AWS_REGION, BUCKET_NAME,
# AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and TERRABASE_MASTER_KEY
... && (set -a; . <r2 env>; set +a; go run ./cmd/shape-bench catchup -chunks 10 -repeats 3 -out <dir>/catchup)
# Object-store proof, then the Slow T1/T2 test, per provider
scripts/terrabase/object-store-proof.sh <tigris or r2 env>
(set -a; . <env>; set +a; dotnet run --project tests/TerraBase.Slow --no-build -- --treenode-filter '/*/*/S3ShapeMirrorTests/*')
```
