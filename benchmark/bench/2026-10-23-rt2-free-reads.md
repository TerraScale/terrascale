# TerraBase free reads (RT2 lane C) — 2026-10-23

Goal: N readers of one shape cost about one storage read per change per cache location, for T0 (origin flight
collapse and ETag), T1 (bucket objects) and T2 (Cloudflare shape edge). Readers N = 1, 10, 100, 1000.

## 1. Status

- **T0 at the head, live OCI cells (`f06647f22`, enterprise plan, 3 repeats): payload reads do not grow with readers.**
  Every step seals 2–3 chunks; payload reads are one per sealed chunk plus about one snapshot-page read per joining
  reader (100 readers: 102; 1000 readers: 498–620). Document (`point`) reads stay at 10–12.6k per step for N = 1 to 1000.
  All 1000 readers were admitted and parked in every repeat. N = 1, 10, 100 delivered every write.
- **N = 1000 on the cells delivers only 14–26 % of writes inside the step** (82,940 / 84,180 / 158,469 of 600,000):
  delivery waits for the remote-durable frontier, p50 17.5–18.3 s at every N, and the step's 5 s grace ends first. That
  lag is replication's (lane I), not the read path's.
- **T1 and T2 (dev host, 3 repeats, 11 objects per reader): no origin payload read at any N.** T1 costs N × 11 bucket
  GETs; T2 costs about 11 bucket GETs per Cloudflare location (126 at N = 1000, edge hit ratio 0.989). On the live
  cells (1 repeat), T2 with 1000 readers × 6 objects: 0 origin payload reads, 61 bucket GETs, hit ratio 0.990.
- **Dev host N = 1000 live load still stops on `503 storage_busy`** in all three repeats (section 5).
- **Hobby plan (finding, section 6):** 1000 readers of one shape spend the tenant's whole request budget and stall its
  writer 10–32 s.
- Not run: R2 (no R2 token: parent NEED).

## 2. Binaries and hosts

| run | binary | host | host load during the run |
| --- | --- | --- | --- |
| dev, before the chunk cache | `56a1b6c4c` (rt2/free-reads-4 before its harness commits; server = 1f5cab388) | mariogk-dev (46 CPUs), local gru + sjc in one process | shared with other lanes; not sampled |
| dev, with the chunk cache | `0d09176f7` (rt2/free-reads-5) | mariogk-dev | load 43 (r1), 17.6 (r2) at step start |
| cells, main baseline | `888ea413` (image eb0e8e62) | ts-sjc-1 (2 CPUs, arm64) → sjc cell :9465 | **contaminated**: lane B's convergence load ran 03:00–03:31Z, this run 03:13–03:22Z |
| cells, after, hobby plan | `f06647f22` = rt2/cells-1 = 0d09176f7 + a16947f52 (billing fix, no read-path change) | ts-sjc-1 → sjc cell | load 3.4 on 2 CPUs before the run |
| cells, after, enterprise plan | `f06647f22` | ts-sjc-1 → sjc cell, load 04:14–04:39Z and catch-up 04:39–04:41Z; org on enterprise 04:12:02–04:41:27Z (lane B), no other lane on the cells | load 3.8–4.2 on 2 CPUs (ts-sjc-1 also runs prod) |

Tool: `benchmark/cmd/shape-bench load` (README "Live load"): readers join over 60 s, then a writer sends 20 documents/s;
30 s window, 5 s grace, 6 s settle. Storage reads are `terrabase.storage.reads{path}` deltas over the whole step.

## 3. Live readers at the head (T0)

Payload reads are `path="shape_body"` on the serving node. "Per sealed" subtracts the N bootstrap page reads, one per
reader (each reader joins at a different second, so page flights rarely overlap).

| N | before cache: shape_body / sealed | per sealed | with cache r1: shape_body / sealed | per sealed | with cache r2: shape_body / sealed | per sealed |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 5 / 5 | 0.8 | 14 / 14 | 0.9 | 86 / 86 | 1.0 |
| 10 | 56 / 6 | 7.7 | 65 / 54 | 1.0 | 67 / 56 | 1.0 |
| 100 | 1113 / 13 | 77.9 | 117 / 25 | 0.7 | 155 / 54 | 1.0 |
| 1000 | 3271 / 3 | stopped | 231 / 0 | stopped | 294 / 0 | stopped |

Deliveries (every admitted reader receives every in-window write): N = 1, 10 and 100 delivered all of them in every
dev run (600, 6,000 and 60,000). Latency p50 / p99 (ms), write sent → reader has it:

| N | before cache | with cache r1 | with cache r2 |
| --- | --- | --- | --- |
| 1 | 11149 / 25854 | 4633 / 9142 | 897 / 1861 |
| 10 | 8069 / 20234 | 2912 / 10158 | 2204 / 10625 |
| 100 | 5349 / 15622 | 4493 / 15647 | 1155 / 6648 |

Latency follows host load (r1 at load 43, r2 at 17.6), and every chunk waits for the remote-durable frontier (sjc's
ack) before it is served. That lag is replication's (lane I), not the read path's.

**The tool's "bound" column says OVER BOUND on every row; it is the wrong gate.** It adds `path="shape"` to the
payload reads. `shape` also counts the replication tailer's frontier reads per change group
(`TursoChangeGroupSource.cs:170`, `:374`, `:391`, `:485`), about 13–74k per step even at N = 1. Measured in a Fast probe
(not committed): 50 parked live readers cost 0 `point` and 1 `shape` read to park; one wake costs 9 `point` (the write),
41 `shape` and 1 `shape_body`, the same as for one reader.

## 3a. Live readers on the OCI cells (T0, enterprise plan)

`f06647f22`, readers on ts-sjc-1 against the sjc cell (home region of database `rt2`; replicas gru and fra). Median
over 3 repeats, range in brackets when they differ. Storage reads are the sjc node's.

| N | admitted | delivered / expected | p50 / p99 ms | sealed | shape_body | point | shape |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 600 / 600 | 17711 / 32031 | 3 | 4 | 10276 (10009–10278) | 13754 (13599–13786) |
| 10 | 10 | 6,000 / 6,000 | 17973 / 32031 | 2 | 12 | 10527 (10435–10789) | 14331 (14233–14408) |
| 100 | 100 | 60,000 / 60,000 | 18039 / 32244 | 2 | 102 | 10394 (10366–10447) | 21600 (21369–21674) |
| 1000 | 1000 | 84,180 (82,940–158,469) / 600,000 | 26231 / 34210 | 2 | 618 (498–620) | 12233 (12041–12558) | 68961 (59269–69620) |

- Every reader's first live poll answers `503 unavailable: the shape head is not remote durable yet` once (1, 10, 100,
  1000 per step), then parks: the head waits for gru or fra to hold the write.
- 429 `shape_busy` at N = 1000: 16, 30 and 89 per step, retried.
- `shape` (metadata) still grows with N, about 5 per response at N = 1000: per-flight frontier, head and retention reads
  for readers that are not yet on the same offset.

## 4. Catch-up readers (T0, T1, T2)

`shape-bench catchup`: readers join behind the head over 10 s and read a snapshot page plus 10 sealed chunks (11
objects). Dev host, `0d09176f7`, real Tigris test bucket and the live edge, 3 repeats, 0 failures, mariogk-dev load
23.6 at start, 11.9 at end. Median (range).

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

- t0: 10× more readers, 10× more origin requests, about the same payload reads (104 → 123): flights plus the
  closed-chunk cache; most remaining reads are snapshot pages, which the cache does not hold.
- t1: the origin reads no payload; the bucket serves every object once per reader.
- t2: about one bucket GET per object per Cloudflare location; concurrent first misses at one location each go to the
  bucket (Cache API has no request collapsing), hence 110 rather than 11 at N = 100.
- Manifest calls cost about 4 metadata reads each (retention floor and lineage), so t1/t2 metadata still grows with N.

Live OCI cells, `f06647f22`, enterprise plan, readers on ts-sjc-1 against the sjc cell, which mirrors to its own Tigris
bucket (`terrascale-terrabase-rt2-test`); edge objects through Cloudflare's location nearest San Jose. 5 chunks (6 objects
per reader), **1 repeat** (the plan window allowed one), so no spread:

| tier | N | origin requests | origin shape_body | object GETs | bucket GETs | edge hit ratio | origin shape (metadata) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| t0 | 100 | 600 | 99 | 0 | 0 | – | 5965 |
| t0 | 1000 | 6,000 | 239 | 0 | 0 | – | 11486 |
| t2 | 100 | 0 (100 manifest) | 0 | 600 | 20 | 0.967 | 447 |
| t2 | 1000 | 0 (1,000 manifest) | 0 | 6,000 | 61 | 0.990 | 4024 |

**T2 on the live cells: 1000 readers × 6 objects cost the origin no payload read and the bucket 61 GETs.**
The catch-up ended at about 04:41Z, at the end of the enterprise window (04:41:27Z). Its client repeats a 429 without
counting it, so a late plan refusal would show as time, not as a read: storage and bucket counts are unaffected.

## 5. N = 1000 on the dev host: what stops it

- All 1000 readers are admitted (before: 64 per organization, `ShapeFlights.cs`).
- Each step stopped on `503 storage_busy` mid-window (r1 at offset 787, r2 at 1479). Readers that fall behind read
  closed chunks at different offsets; those reads hit the 8-flights-per-organization cap (`shape_busy` retries: 8,620,
  23,581, 29,035 per step) and storage admission.
- `point` reads grow with N at N = 1000 (431,643 and 490,032 per step against about 10k at N = 1). `point` is
  node-wide; at the head it does not grow with readers (probe in section 3), so this is the behind-the-head path.

## 6. Live cells: the tenant plan is the limit (finding)

Cell org `01M5K94R0MBAC0ESM76VB7JGSG` on its QoS plan, `f06647f22`, repeat 1:

| N | admitted | writes done | delivered | 429 tenant_busy | writer waits |
| --- | --- | --- | --- | --- | --- |
| 1 | 1 | 193 of 600 | 0 | 10,868 (incl. writer retries) | 19.7 s |
| 10 | 10 | 183 | 174 | 10,797 | 11.6 s |
| 100 | 100 | 86 | 118 | 17,096 | 26.1 s |
| 1000 | 733 | 106 | 7 | 91,445 (78,840 reader 429s of ~83k responses) | 31.9 s |

The main 888ea413 baseline looks the same (429 tenant_busy 77,546 at N = 1000), but lane B's own load overlapped it.

**Are collapsed or cached shape reads charged like origin reads? Yes.**

- Admission takes one request token per HTTP request before routing: `QosExtensions.cs:83` →
  `QosAdmission.TryEnter`, `QosAdmission.cs:75` (`lane.Tokens--`). Hobby: 50 req/s, burst 100, 4 concurrent
  (`QosPolicy.cs:21`); Pro: 500 req/s.
- A live poll gives back only its concurrency slot (`GetShapeEndpoint.cs:144`), never its token. A poll that joins
  an existing flight, or is answered from the closed-chunk cache, costs no storage but still spends a token.
- Reads and writes share one bucket, so 1,000 readers of one shape on Hobby spend the tenant's whole budget and the
  writer's PUTs get 429 with Retry-After: the 10–30 s writer stalls above.

**Suggestion (not implemented; QoS is a product decision):**

1. Refund the token when the answer was shared: the flight joiner (`ShapeFlights.cs:184`, a `ReadAsync` call that did not start the flight) and a closed-chunk cache hit (`ShapeService.cs`, `ClosedAsync`) cost no storage read.
2. Or give writes their own bucket, so reads can never starve the writer.

Either keeps the per-request cap for the origin work that is real.

**Does T2 make it moot for real clients? Only for catch-up.** Behind the head, a T2 client calls the origin once per 8
objects (the manifest) and fetches the rest from Cloudflare, so its token use falls about 8×. At the head, each live
poll still goes to the origin once per reader per change: shape responses are `private, no-store`
(`OriginNoStore`), so the edge does not collapse live polls. Electric gets that from cacheable, cursor-keyed live
responses at the CDN; adding it would need live responses that are safe to share (encrypted or signed per epoch).

## 7. What changed (branches)

| branch | SHA | change |
| --- | --- | --- |
| rt2/free-reads-1 | f125d33 | one flight per (handle, offset, live), not per cursor; 4096 / 1024 joined requesters; collection lookup from memory (a 1024-poll burst answered 503 before) |
| rt2/free-reads-2 | 95bc86c | T1 Slow test on a real bucket; manifest answers from mirror metadata without a payload read |
| rt2/free-reads-3 | 2899869 | T2 shape edge (Worker + edge links); 4 mirror upload workers |
| rt2/free-reads-4 | f51ed6c | `shape-bench catchup`; local host on a real bucket; `-origin` for running cells; bench host env rename (`TERRABASE_BENCH_CLUSTER_FILE` was read as a secret file) |
| rt2/free-reads-5 | 0d09176 | closed-chunk memory cache (512 chunks, 16 MiB) |

## 8. Commands

Dev host (catch-up, all tiers; the bucket env file holds `BUCKET_NAME`, `AWS_*`, `TERRABASE_MASTER_KEY`):

```sh
~/.local/bin/ts-heavy-any bash -c 'make terrabase-build CONFIGURATION=Release && dotnet build tests/TerraBase.E2E -c Release && cd benchmark && go run ./cmd/shape-bench load -steps 1,10,100,1000 -out <base> && set -a && . <bucket env> && set +a && go run ./cmd/shape-bench catchup -chunks 10 -repeats 3 -out <base>' --repo terrascale --branch <branch>
```

Live cells, from a NetBird host with the cell's metrics on loopback (ts-sjc-1), key in `TS_BENCH_API_KEY`:

```sh
shape-bench load -origin http://10.200.109.193:9465 -metrics http://127.0.0.1:19091/metrics -project 01M5K94R1P7TBK9K69CXRB2F3A -database rt2 -collection <new> -start <unix+15> -steps 1,10,100,1000 -out load.json && shape-bench merge -out load load.json
shape-bench catchup -origin http://10.200.109.193:9465 -metrics http://127.0.0.1:19091/metrics -project 01M5K94R1P7TBK9K69CXRB2F3A -database rt2 -tiers t0,t1,t2 -chunks 10 -repeats 3 -out catchup
```

R2: the same catch-up with an env file naming the R2 endpoint, bucket and keys; no code change. Not run: the Cloudflare
token has no R2 permission (parent NEED).
