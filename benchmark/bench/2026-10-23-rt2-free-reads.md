# TerraBase free reads (RT2 lane C) — 2026-10-23

Goal: N readers of one shape cost about one storage read per change per cache location, for T0 (origin flight
collapse and ETag), T1 (bucket objects) and T2 (Cloudflare shape edge). Readers N = 1, 10, 100, 1000.

## 1. Status

- **Payload reads per sealed chunk are flat up to N = 100 with the fixes** (dev host, 2 repeats): about 1 per sealed
  chunk, plus one snapshot-page read per joining reader. Before the closed-chunk cache they grew with N (77.9 per chunk at
  N = 100).
- **N = 1000 is not measured cleanly anywhere yet.** Dev host: every repeat stopped on a `503 storage_busy` mid-step
  (section 5). Live cells: the tenant's request plan refused 95 % of requests (section 6).
- **T2 works end to end** (section 4): one bucket GET per object per Cloudflare location, the rest are edge hits, and no
  origin payload read.
- Not yet run: the T0/T1/T2 catch-up repeats on the dev host, the live cells on the enterprise plan, and R2 (no R2 token:
  parent NEED). Section 8 lists the commands.

## 2. Binaries and hosts

| run | binary | host | host load during the run |
| --- | --- | --- | --- |
| dev, before the chunk cache | `56a1b6c4c` (rt2/free-reads-4 before its harness commits; server = 1f5cab388) | mariogk-dev (46 CPUs), local gru + sjc in one process | shared with other lanes; not sampled |
| dev, with the chunk cache | `0d09176f7` (rt2/free-reads-5) | mariogk-dev | load 43 (r1), 17.6 (r2) at step start |
| cells, main baseline | `888ea413` (image eb0e8e62) | ts-sjc-1 (2 CPUs, arm64) → sjc cell :9465 | **contaminated**: lane B's convergence load ran 03:00–03:31Z, this run 03:13–03:22Z |
| cells, after | `f06647f22` = rt2/cells-1 = 0d09176f7 + a16947f52 (billing fix, no read-path change) | ts-sjc-1 → sjc cell | load 3.4 on 2 CPUs before the run |

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

## 4. Catch-up readers (T0, T1, T2)

`shape-bench catchup`: readers join behind the head and read a snapshot page plus `-chunks` sealed chunks. Smoke run on
beta, 3 readers, 10 chunks, 1 repeat (`0d09176f7`, real Tigris test bucket, live edge), so 11 objects per reader:

| tier | origin shape_body reads | origin requests | object GETs | bucket GETs | edge hits | bucket PUTs |
| --- | --- | --- | --- | --- | --- | --- |
| t0 | 33 | 33 | 0 | 0 | 0 | 3 |
| t2 | 0 | 0 (6 manifest calls) | 33 | 11 | 22 (0.667) | 0 |

t2 is the target shape: 3 readers × 11 objects cost 11 bucket GETs (one per object, one Cloudflare location) and no
origin payload read. The t1 smoke hit a `429 shape_busy` in the seal driver (since fixed with retries). The 3-repeat run
for N = 1…1000 is in progress (section 8).

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

## 8. Pending runs and their commands

Dev host (catch-up, all tiers; bucket env file holds `BUCKET_NAME`, `AWS_*`, `TERRABASE_MASTER_KEY`):

```sh
~/.local/bin/ts-heavy-any bash -c 'make terrabase-build CONFIGURATION=Release && dotnet build tests/TerraBase.E2E -c Release && cd benchmark && set -a && . ~/.config/terrascale/rt2-free-reads-s3.env && set +a && go run ./cmd/shape-bench catchup -chunks 10 -repeats 3 -out bench/2026-10-23-rt2-catchup' --repo terrascale --branch rt2/free-reads-bench5
```

Live cells, from ts-sjc-1, once the org is on enterprise (`~/rt2/rt2fr-run.sh <label> t0,t1,t2`):

```sh
shape-bench load -origin http://10.200.109.193:9465 -metrics http://127.0.0.1:19091/metrics -project 01M5K94R1P7TBK9K69CXRB2F3A -database rt2 -collection <new> -start <unix+15> -steps 1,10,100,1000 -out load.json
shape-bench catchup -origin http://10.200.109.193:9465 -metrics http://127.0.0.1:19091/metrics -project 01M5K94R1P7TBK9K69CXRB2F3A -database rt2 -tiers t0,t1,t2 -chunks 10 -repeats 3 -out catchup
```

R2: the same catch-up with an env file naming the R2 endpoint, bucket and keys; no code change. Blocked on an R2 token.
