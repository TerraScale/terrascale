# TerraBase free reads: Tigris vs R2 behind Cloudflare's cache (FR-32, RT3a) — 2026-10-23

Goal (FR-32): N readers of the same shape change cost at most 1 origin read per chunk per cache location. Tiers:

- T0: the origin.
- T1: the bucket, read by presigned URL.
- T2: Cloudflare's cache in front of an R2 bucket ([free-reads-cdn.md](../../docs/terrabase/free-reads-cdn.md)).

Tigris and R2 run the same binary. Only the env file differs: endpoint, bucket and keys.

## Result

- **R2 + Cloudflare (T2): 11 bucket reads for 11 objects at N = 1, 10, 100 and 1000, in each of 3 repeats.** That is
  exactly 1 bucket read per object per cache location, with no spread. The CDN hit ratio is 0.9, 0.99 and 0.999 at
  N = 10, 100 and 1000. The origin read no payload.
- R2's own analytics agree. `r2OperationsAdaptiveGroups` counts `GetObject` = 11 on `ts-terrabase-bench` in every T2 step
  window of all 3 repeats. That is the bucket's own count, not the client's `cf-cache-status` (section 4).
- **Tigris (T1): one bucket read per object per reader**: 11, 110, 1,100 and 11,000. At N = 1000 that is 1,000× the T2
  count. The origin read no payload. Tigris has no T2: its objects keep presigned links.
- T0 (origin) at this head grows with N: 3,876–7,035 `shape_body` reads at N = 1000. The closed-chunk cache and the
  one-flight-per-offset fix are in #516 (RT2c), which is not merged into this binary, so T0 here is a baseline only.
- Compared with the RT2/RT2c Worker edge (`rt2c/reads-results`, R2), which needed 2,049 bucket GETs for 11,000 object
  reads at N = 1000: the Worker's Cache API neither collapses concurrent misses nor uses tiered cache. Cloudflare's own
  cache in front of the R2 custom domain does collapse them.

## 1. Setup

| item | value |
| --- | --- |
| binary | `08f47be9` = `feat/rt3a-free-reads-bench`: #521 (`3d671c43f`, merged as `e7686d41`; T2 links) + the catch-up harness in this PR |
| host | delta (16 CPUs), local gru + sjc nodes in one process, shared with other lanes |
| host load (1-min) | Tigris 10.3 at start, 9.6 at end; R2 9.6 at start, 10.3 at end |
| Tigris | `ts-terrabase-bench`, `https://fly.storage.tigris.dev`, the Tigris CI keys |
| R2 | `ts-terrabase-bench` (location hint ENAM), custom domain `shapes-ts-terrabase-bench.terrascale.tech`, zone cache rule and Smart Tiered Cache. S3 keys derived from the Cloudflare CLI token (interim, until the bucket-scoped token) |
| client location | every T2 response came from Cloudflare `CWB` (Curitiba), so there is 1 cache location |
| load | `shape-bench catchup -chunks 10 -repeats 3`: per step a new collection, 1 snapshot page + 10 sealed chunks = 11 objects, mirrored before readers start; N readers join evenly over 10 s and fetch every object, checking size and sha256 against the manifest |

## 2. R2 + Cloudflare (T0, T2)

Median (range) over 3 repeats. "bucket GETs" on T2 counts responses that were not a cache `HIT`.

| tier | readers | object GETs | bucket GETs | bucket GETs per object per location | CDN hit ratio | origin `shape_body` reads | origin requests | manifest calls | failures |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| t2 | 1 | 11 | 11 | 1 | 0 | 0 | 0 | 2 | 0 |
| t2 | 10 | 110 | 11 | 1 | 0.9 | 0 | 0 | 20 | 0 |
| t2 | 100 | 1,100 | 11 | 1 | 0.99 | 0 | 0 | 200 | 0 |
| t2 | 1000 | 11,000 | 11 | 1 | 0.999 | 0 | 0 | 2,000 | 0 |
| t0 | 1 | – | – | – | – | 11 | 11 | 0 | 0 |
| t0 | 10 | – | – | – | – | 103 (99–110) | 110 | 0 | 0 |
| t0 | 100 | – | – | – | – | 735 (674–969) | 1,100 | 0 | 0 |
| t0 | 1000 | – | – | – | – | 3,876 (2,635–4,507) | 11,000 | 0 | 0 |

## 3. Tigris (T0, T1)

| tier | readers | object GETs | bucket GETs | bucket GETs per object | origin `shape_body` reads | origin requests | manifest calls | failures |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| t1 | 1 | 11 | 11 | 1 | 0 | 0 | 2 | 0 |
| t1 | 10 | 110 | 110 | 10 | 0 | 0 | 20 | 0 |
| t1 | 100 | 1,100 (1,094–1,100) | 1,100 (1,094–1,100) | 100 (99.5–100) | 0 | 0 | 200 (199–200) | 0 (0–1) |
| t1 | 1000 | 11,000 | 11,000 | 1,000 | 0 | 0 | 2,000 | 0 |
| t0 | 1 | – | – | – | 11 | 11 | 0 | 0 |
| t0 | 10 | – | – | – | 110 (99–110) | 110 | 0 | 0 |
| t0 | 100 | – | – | – | 890 (462–1,100) | 1,100 | 0 | 0 |
| t0 | 1000 | – | – | – | 7,035 (5,831–7,732) | 11,000 | 0 | 0 |

The one T1 failure (N = 100, repeat 3, object 424): the URL was presigned near the end of an epoch and fetched after it
expired. Tigris answered `403 Request has expired`. A client re-reads the manifest after `expires_at`; the harness does
not. CDN links on R2 do not expire, so T2 cannot fail this way.

## 4. Bucket reads measured by R2

`GetObject` on `ts-terrabase-bench` in each T2 step's reader window (Cloudflare GraphQL `r2OperationsAdaptiveGroups`),
next to the harness's count of non-HIT responses:

| repeat | N = 1 | N = 10 | N = 100 | N = 1000 |
| --- | --- | --- | --- | --- |
| 1 | 11 / 11 | 11 / 11 | 11 / 11 | 11 / 11 |
| 2 | 11 / 11 | 11 / 11 | 11 / 11 | 11 / 11 |
| 3 | 11 / 11 | 11 / 11 | 11 / 11 | 11 / 11 |

(R2 `GetObject` / harness misses.)

## 5. What this does not show

- One client host means one Cloudflare location. With readers at K locations, the bound is 11 × K bucket reads.
  Smart Tiered Cache should keep it closer to 11, but that was not measured here.
- Live reads at the head stay on the origin (`private, no-store`). T2 covers catch-up reads of sealed objects only.
- T0 at this head is a baseline. The T0 fixes are in #516.

## 6. Commands

```sh
# delta; env files are mode 0600 outside the repository. The master key is fixed per bench bucket, because the bucket
# keeps a data key wrapped by it.
ts-heavy-any bash -c 'make terrabase-build CONFIGURATION=Release && dotnet build tests/TerraBase.E2E -c Release && cd benchmark &&
  for e in tigris-bench:t0,t1 r2-bench-local:t0,t2; do (set -a; . ~/.config/terrascale/${e%%:*}.env; . ~/.config/terrascale/bench-master-key.env; set +a;
  go run ./cmd/shape-bench catchup -tiers ${e#*:} -chunks 10 -repeats 3 -out $HOME/rt3a-bench/${e%%:*}); done' --repo terrascale --branch <branch>
```

R2's own count: the GraphQL query `r2OperationsAdaptiveGroups(filter: {bucketName, datetime_geq, datetime_leq})` with
`sum { requests }` by `actionType`, over each step's `Start`/`End` from the run's JSON.
