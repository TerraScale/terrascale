# Milestone B end-to-end acceptance — 2026-11-05

**Latest default-ack result: PASS in every repeat.** The independent fresh Pro reader-drain confirmation accepts 900/900 writes and 1,800/1,800 cross reads with zero errors; all nine 100-write repeats meet endpoint p99 <100 ms and client p99 <own baseline+100 ms. Every one of 960 acknowledged IDs and full values matches exactly in all three regions. The original failed cohorts, including the earlier SJC repeat failure, remain below. SQL reads are unbilled, three post-fix home moves preserve all acknowledged rows, and CDN deletion passes within 20 seconds. **The separately assigned item 1 rerun during a zero-downtime rollout is pending.**

| Item | Result | Independently observed evidence |
|---|---|---|
| Fresh account, Free → Pro | PASS | Hosted sign-up, real email verification, dashboard onboarding, API key creation, and public Stripe **sandbox** checkout; Pro active. No real-money activation. |
| 1. Write each region; read other regions | PASS steady state; rollout rerun PENDING | Latest fresh Pro: 900/900 writes,1,800/1,800 cross reads,960 acknowledged IDs/values exactly once in all regions. All1,800 reads identify the requested region;24 first reads are empty (eventual visibility). Earlier rollout FAIL retained; independent zero-downtime rollout rerun awaits HANDOFF. |
| 2. Default local ack | PASS after reader-drain fix, all 9 repeats | Latest fresh Pro: 900/900 writes; pooled endpoint p99 GRU/SJC/FRA 34.82/51.61/37.77 ms, client55.313/246.117/251.568 ms. Every100-write repeat passes both gates. All600 away writes commit locally. Earlier SJC repeat1 FAIL retained. [#1105 independent proof](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6669577310). |
| 2. Opt-in home-durable ack | PASS | 30 accepted home-durable writes per plan; additional Pro functional check: 9/9 writes immediately visible at home; each post-fix cohort adds 30/30 immediate home reads. |
| 3. SQL reads are not billed | PASS | Pro read-only workload: 300/300 successful reads; write units stay 981; usage charges stay $0.00. Read fair-use units are separate. |
| 3. CDN cache in all regions | PASS | SDK-used shape ciphertext: 200/HIT at GRU, SJC and FRA with `terrascale-js/0.0.19`; matching bytes and digest. |
| 4. Move home under light writes | PASS after fix; initial FAIL | Original move loses 2 accepted rows. Post-fix: 3/3 moves, 45 accepted load writes, all 1,003 IDs/values exactly once in every region; 9/9 inventories target-local, including pinned SJC. One of 46 insert attempts is refused during move 2. [#1106](https://github.com/TerraScale/terrascale/issues/1106#issuecomment-6667871258). |
| 5. Database data and keys | PASS after fix; initial FAIL | Original deletion: APIs/key routes 404, but all 21 cached-object GETs remain 200/HIT at 527–530 s. Fresh post-fix deletion: APIs/key routes 404; warmed CDN URL 404/BYPASS in all three POPs at 17.34–19.45 s. [#1107](https://github.com/TerraScale/terrascale/issues/1107). |
| 5. Account closure | PASS | Closed at 20:30:37.789928Z. Retained bearer token: 401 invalid_key at all 3 pinned regional APIs. Fresh API-key exchanges: 401 invalid_client for all 3 attempts. All 5 databases deleted. |

## Method and scope

Fresh identity: `mariogk+mb-acceptance-20261105-b1c4@terrascale.tech`, organization `01M6PENTY9976KJDP65GTQDQVK`, project `01M6PENV06CRPSJYDDRZTF75R4`. Created with `ts-throwaway-account mb-acceptance-20261105-b1c4`. Original database `01M6PER4PDC8FM24ZYKX1PJ6JE` (`milestone-b-acceptance`) was created in the dashboard with home gru and All regions, returning replicas fra/sjc. The reserved follow-up database is `01M6PFQFV240ECH0BCQ5AKFYHX` (`milestone-b-ack-retest`). The rollout/R9 database is `01M6PK77TX4H0B1D3GQZ73XZCF`; the final fresh Pro confirmation database is `01M6PNY2NSDD07TVSQZJYHK8JW` (`milestone-b-confirmation`), home gru, All regions.

Only hosted auth, public dashboard/API, official Python/JavaScript SDK source, and stock psql were used as product interfaces. Baseline Python SDK source: `c27dbfa6220ca1a7857b6dc0c95bef220e8684cd`; JavaScript SDK 0.0.19. Post-fix Python probes and final Python/JavaScript cleanup proofs pin shipped source `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`. Public health identifies region and version 0.1.0; it does not identify the deployed backend source SHA. No earlier thread report substitutes for the measurements here. No product code was changed.

Public load probes ran through `ts-heavy-any` on dev hosts, primarily beta (the move probe ran on delta). Three client workers ran concurrently, one per write region. Each plan had three measured repeats of 100 writes per region, with read-only warmups and no application-request retries. Each write used a fresh ULID in `acceptance_rows (id TEXT PRIMARY KEY, writer TEXT, sequence INTEGER, body TEXT)` and was followed by SELECT-by-ID from each other region. Percentiles use nearest rank and include the first measured write. Forwarded responses can contain both home and endpoint `app` timings; endpoint work uses the last `app` entry, attributed to the requested endpoint. This corrects the initial first-entry parser, which understated some forwarded-request timings; raw headers and every attempt are retained. Client times include the dev-host network path; they are not claimed as in-region client times. Server `app` timings isolate origin work; the forced-home `home` timings provide the region-to-home comparison.

The regional HTTP clients use `base_url=https://<region>.api.terrascale.tech` and the SDK's `region=<region>` pin. Default inserts use `INSERT INTO acceptance_rows (id, writer, sequence, body) VALUES (?, ?, ?, ?)`. Cross reads use `SELECT id, writer, sequence, body FROM acceptance_rows WHERE id = ?`. Home-durable comparison requests add `TerraScale-Durability: home`; forced-home comparison requests add `TerraScale-Write: home`. Local attribution uses `X-TerraBase-Region`, `X-TerraBase-Machine` and the region in `Server-Timing`; `TerraScale-Write-Route` records the selected route and commit mode. A successful HTTP response containing an empty row set counts as a first-read miss.

CDN checks used existing gru/sjc/fra nodes solely as network vantage points for a handful of unauthenticated public HTTPS GETs of SDK-returned sealed object URLs. No internal product endpoints or storage were read. The JavaScript SDK was used with its supported fetch adapter to record responses and identify requests with `terrascale-js/0.0.19`. Epoch-key values and credentials were never logged. psql used verify-full TLS with system roots and a temporary mode-0600 pgpass file, removed afterward.

DECISION: Evaluate pooled 300-write endpoint p99 <100 ms and client p99 < the measured client-path baseline+100 ms, while retaining every 100-write repeat and its PASS/FAIL result. Reason: the user's Q-RTT/Q-PLACEMENT decisions account for the dev client's roughly 200 ms path to SJC/FRA; pooled success must not hide the SJC repeat 1 failure. The parent authorizes delta, which runs both post-fix cohorts. Production nodes run only the small anonymous CDN vantage checks.

## Writes and local acknowledgement

| Plan | Endpoint | Accepted | Client p50 / p99 ms | Server app p50 / p99 ms | Non-home `commit=local` |
|---|---|---:|---:|---:|---:|
| Free | gru | 300/300 | 33.488 / 183.873 | 12.67 / 36.31 | home execution |
| Free | sjc | 300/300 | 214.261 / 545.670 | 19.94 / 351.95 | 300/300 |
| Free | fra | 300/300 | 231.447 / 602.066 | 19.83 / 391.63 | 300/300 |
| Pro | gru | 300/300 | 36.822 / 280.697 | 13.91 / 63.85 | home execution |
| Pro | sjc | 300/300 | 216.623 / 527.401 | 20.48 / 169.51 | 300/300 |
| Pro | fra | 300/300 | 231.828 / 671.412 | 20.51 / 455.34 | 297/300 |

All non-home local routes report `route=local; reason=ulid_insert; commit=local`. Pro FRA has three successful home fallbacks. The slow tails include local sealing; the Free first writes took 1,452.035 ms SJC and 1,635.941 ms FRA client time, with local_seal 844.73/1,206.51 ms. Free repeat 2 server p99 is 351.95/391.63 ms SJC/FRA, above approximately 200 ms home calls. This is a measured failure even after separating client distance. See #1105 and the [individual samples](2026-11-05-milestone-b-acceptance-samples.md).

| Plan | Endpoint | Home-durable writes | Client p50 / p99 ms | Forced-home p50 / p99 ms |
|---|---|---:|---:|---:|
| Free | gru | 10/10 | 33.888 / 47.826 | 33.639 / 49.195 |
| Free | sjc | 10/10 | 412.373 / 431.937 | 396.456 / 408.162 |
| Free | fra | 10/10 | 421.698 / 437.224 | 415.161 / 420.709 |
| Pro | gru | 10/10 | 32.506 / 36.955 | 33.626 / 43.843 |
| Pro | sjc | 10/10 | 408.953 / 445.274 | 396.843 / 400.524 |
| Pro | fra | 10/10 | 423.115 / 1261.884 | 422.236 / 625.493 |

These acknowledgement-mode samples are ten per endpoint, not a 100-write p99 qualification. The additional Pro functional test sends `TerraScale-Durability: home`: all nine accepted rows are present on the first immediately subsequent home SELECT.

## Repeat-level default write results

Each row is a separate measured 100-write repeat. Warmups are read-only; cold first writes remain in the measurements.

| Plan | Endpoint | Repeat | Client p50 / p99 ms | Server app p50 / p99 ms |
|---|---|---:|---:|---:|
| Free | gru | 1 | 33.049 / 67.955 | 12.10 / 32.95 |
| Free | gru | 2 | 33.793 / 194.392 | 12.90 / 32.51 |
| Free | gru | 3 | 33.869 / 71.867 | 12.72 / 48.72 |
| Free | sjc | 1 | 215.486 / 376.646 | 20.89 / 56.08 |
| Free | sjc | 2 | 213.754 / 545.670 | 19.36 / 351.95 |
| Free | sjc | 3 | 214.027 / 381.501 | 19.81 / 38.81 |
| Free | fra | 1 | 232.618 / 469.703 | 20.42 / 47.19 |
| Free | fra | 2 | 230.558 / 602.066 | 18.98 / 391.63 |
| Free | fra | 3 | 231.467 / 386.041 | 20.00 / 38.78 |
| Pro | gru | 1 | 36.815 / 66.956 | 14.01 / 43.24 |
| Pro | gru | 2 | 39.404 / 400.312 | 16.15 / 99.01 |
| Pro | gru | 3 | 34.610 / 193.610 | 11.93 / 35.50 |
| Pro | sjc | 1 | 216.123 / 365.052 | 21.23 / 49.24 |
| Pro | sjc | 2 | 216.393 / 527.401 | 19.99 / 43.46 |
| Pro | sjc | 3 | 217.207 / 402.546 | 20.48 / 169.51 |
| Pro | fra | 1 | 235.379 / 672.349 | 22.74 / 459.40 |
| Pro | fra | 2 | 230.019 / 429.584 | 19.48 / 39.14 |
| Pro | fra | 3 | 230.764 / 471.876 | 19.98 / 35.65 |

## Independent post-fix Pro run during a rollout

SDK source: `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`. Server deployment handoff: `9a335684e9b3a8b9eff9fe191444e91b7794628d`, confirmed by the fixer on all six nodes at 19:01:47Z. The following measurements are independent acceptance requests, not the fixer’s results. This run began before the parent’s request to measure the path baseline first; it retained its original baseline-after-writes order. The separate baseline-first fresh-database confirmation follows this retained cohort below. The run also overlaps the afa0171 rollout; no failed or slow samples are discarded. A newly created all-regions database captures first inserts after read-only warmups.

Client host: `delta`. Database `01M6PK77TX4H0B1D3GQZ73XZCF` (`milestone-b-postfix`), home gru.

The path baseline consists of 100 SELECT-1 requests per region after the write workload; each uses the same HTTP/SDK transport. Baseline RTT here is the median of client duration minus origin `app` time. This includes the HTTPS/edge path; it is not an ICMP measurement. The client gate is this baseline +100 ms.

| Region | Baseline successful / attempted | Baseline p50 client / origin ms | Baseline path RTT ms | Client p99 limit ms |
|---|---:|---:|---:|---:|
| gru | 98/100 | 24.927 / 3.13 | 21.540 | 121.540 |
| sjc | 99/100 | 196.889 / 1.44 | 195.447 | 295.447 |
| fra | 99/100 | 213.317 / 1.39 | 211.806 | 311.806 |

| Region | Repeat | Accepted | Client p50 / p99 ms | Origin app p50 / p99 ms | Local commits | Server / client gates |
|---|---:|---:|---:|---:|---:|---|
| gru | 1 | 100/100 | 34.185 / 77.578 | 12.52 / 55.92 | 0/100 | PASS / PASS |
| gru | 2 | 100/100 | 37.264 / 102.271 | 14.23 / 79.31 | 0/100 | PASS / PASS |
| gru | 3 | 100/100 | 37.595 / 57.661 | 13.05 / 34.09 | 0/100 | PASS / PASS |
| gru | pooled | 300/300 | 36.918 / 92.188 | 13.11 / 55.92 | 0/300 | PASS / PASS |
| sjc | 1 | 100/100 | 215.500 / 237.259 | 21.66 / 44.24 | 100/100 | PASS / PASS |
| sjc | 2 | 99/100 | 218.369 / 2211.595 | 23.48 / 881.77 | 80/100 | FAIL / FAIL |
| sjc | 3 | 100/100 | 219.605 / 816.145 | 21.15 / 218.11 | 100/100 | FAIL / FAIL |
| sjc | pooled | 299/300 | 218.062 / 1077.745 | 22.07 / 726.52 | 280/300 | FAIL / FAIL |
| fra | 1 | 100/100 | 232.397 / 267.299 | 20.82 / 53.97 | 100/100 | PASS / PASS |
| fra | 2 | 100/100 | 233.769 / 877.049 | 20.41 / 293.02 | 100/100 | FAIL / FAIL |
| fra | 3 | 99/100 | 242.038 / 1928.428 | 27.37 / 749.63 | 70/100 | FAIL / FAIL |
| fra | pooled | 299/300 | 235.487 / 968.217 | 22.14 / 620.04 | 270/300 | FAIL / FAIL |

| Region | First measured insert client ms | Origin app ms | Route |
|---|---:|---:|---|
| gru | 56.001 | 33.35 | home |
| sjc | 436.084 | 239.85 | `route=local; reason=ulid_insert; commit=local` |
| fra | 267.299 | 53.97 | `route=local; reason=ulid_insert; commit=local` |

Cross-region reads after each accepted write (latencies include failed requests; header attribution includes error responses):

| Write → read | Attempts | Successful | Client p50 / p99 ms | Visible first read | Target-region header |
|---|---:|---:|---:|---:|---:|
| gru → sjc | 300 | 299 | 197.892 / 362.857 | 279/300 | 300/300 |
| gru → fra | 300 | 299 | 216.419 / 415.459 | 297/300 | 300/300 |
| sjc → gru | 299 | 299 | 25.069 / 54.205 | 293/299 | 299/299 |
| sjc → fra | 299 | 298 | 216.851 / 644.553 | 280/299 | 299/299 |
| fra → gru | 299 | 299 | 26.549 / 59.637 | 296/299 | 299/299 |
| fra → sjc | 299 | 298 | 198.101 / 589.576 | 280/299 | 299/299 |

Exact reconciliation after the comparison requests:

```json
[
  {
    "kind": "exact_comparison",
    "region": "gru",
    "expected": 958,
    "rows": 958,
    "distinct": 958,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "sjc",
    "expected": 958,
    "rows": 958,
    "distinct": 958,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "fra",
    "expected": 958,
    "rows": 958,
    "distinct": 958,
    "missing": [],
    "unexpected": [],
    "changed": []
  }
]
```

## Baseline-first independent confirmation

Server readiness: `26338d3151770a15137c6cb077b94d6da47c1257`, all six nodes healthy at 20:18:59Z, CI [42100601357](https://github.com/TerraScale/terrascale/actions/runs/42100601357) successful. The parent freezes the fabric through 20:40Z; all cohort requests finish by 20:24:03.983270Z.

Client `delta`, SDK `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`, starting `2026-11-05T20:20:49Z`. Database `01M6PNY2NSDD07TVSQZJYHK8JW`, home `gru`, created fresh on Pro with all regions. These are its first measured inserts after read-only warmups. The earlier rollout cohort remains above.

One hundred SELECT-1 baseline attempts per region precede all measured writes. Path RTT is the median of successful request client duration minus origin app duration. Client limits are this value +100 ms; origin p99 must be <100 ms. Failures remain in the cohort. Missing origin timing prevents a server PASS; per-repeat and pooled values use nearest-rank percentiles.

| Region | Baseline successful / attempted | Baseline client / app p50 ms | Path RTT ms | Client limit ms |
|---|---:|---:|---:|---:|
| gru | 100/100 | 25.089 / 1.77 | 23.199 | 123.199 |
| sjc | 100/100 | 194.825 / 1.47 | 193.266 | 293.266 |
| fra | 100/100 | 212.935 / 1.47 | 211.437 | 311.437 |

| Region | Repeat | Accepted / attempts | Client p50 / p99 ms | Origin p50 / p99 ms | Origin samples | Client / origin max ms | Local commits | Server / client gates |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| gru | 1 | 100/100 | 35.620 / 65.293 | 13.25 / 38.70 | 100/100 | 70.401 / 47.73 | 0/100 | PASS / PASS |
| gru | 2 | 100/100 | 36.041 / 68.869 | 12.74 / 47.14 | 100/100 | 77.193 / 54.47 | 0/100 | PASS / PASS |
| gru | 3 | 100/100 | 39.510 / 81.493 | 17.61 / 57.93 | 100/100 | 84.913 / 61.03 | 0/100 | PASS / PASS |
| gru | pooled | 300/300 | 36.666 / 77.193 | 14.03 / 54.47 | 300/300 | 84.913 / 61.03 | 0/300 | PASS / PASS |
| sjc | 1 | 100/100 | 219.090 / 400.024 | 22.86 / 202.83 | 100/100 | 745.894 / 551.04 | 99/100 | FAIL / FAIL |
| sjc | 2 | 100/100 | 217.832 / 242.575 | 21.81 / 46.33 | 100/100 | 275.416 / 79.61 | 100/100 | PASS / PASS |
| sjc | 3 | 100/100 | 218.487 / 255.486 | 22.54 / 61.07 | 100/100 | 289.329 / 91.69 | 100/100 | PASS / PASS |
| sjc | pooled | 300/300 | 218.469 / 275.416 | 22.30 / 79.61 | 300/300 | 745.894 / 551.04 | 299/300 | PASS / PASS |
| fra | 1 | 100/100 | 235.568 / 269.991 | 22.02 / 57.08 | 100/100 | 306.775 / 92.77 | 100/100 | PASS / PASS |
| fra | 2 | 100/100 | 234.803 / 260.670 | 21.24 / 47.27 | 100/100 | 308.176 / 84.42 | 100/100 | PASS / PASS |
| fra | 3 | 100/100 | 236.339 / 263.422 | 22.67 / 49.84 | 100/100 | 263.659 / 50.10 | 100/100 | PASS / PASS |
| fra | pooled | 300/300 | 235.531 / 265.568 | 22.05 / 50.64 | 300/300 | 308.176 / 92.77 | 300/300 | PASS / PASS |

| Region | First cohort insert client / origin ms | Route |
|---|---:|---|
| gru | 49.158 / 27.36 | home |
| sjc | 235.391 / 39.61 | `route=local; reason=ulid_insert; commit=local` |
| fra | 269.991 / 57.08 | `route=local; reason=ulid_insert; commit=local` |

| Write → read | Attempts / successful | Client p50 / p99 ms | Visible first read | Target-local header |
|---|---:|---:|---:|---:|
| gru → sjc | 300 / 300 | 199.630 / 240.359 | 294/300 | 300/300 |
| gru → fra | 300 / 300 | 215.796 / 242.950 | 300/300 | 300/300 |
| sjc → gru | 300 / 300 | 26.387 / 53.450 | 291/300 | 300/300 |
| sjc → fra | 300 / 300 | 214.707 / 255.158 | 296/300 | 300/300 |
| fra → gru | 300 / 300 | 26.537 / 52.746 | 296/300 | 300/300 |
| fra → sjc | 300 / 300 | 198.160 / 239.758 | 295/300 | 300/300 |

Exact ID/value reconciliation:

```json
[
  {
    "kind": "exact_comparison",
    "region": "gru",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "sjc",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "fra",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  }
]
```

Home-durable first immediate home checks: 30/30. All failed requests and maxima remain in the evidence and sample documents.

The pooled gates pass; SJC repeat 1 fails both gates. Its n21 request `01M6PNZ85YKBDC4T1B180CH7NP` takes 400.024 ms client /202.83 ms SJC endpoint, with `commit=home` and 184 ms home RTT. The next request `01M6PNZ8T08P9HZG281SWP32AM` takes 745.894 ms /551.04 ms, including 531.58 ms `local_begin`, with `commit=local`. SJC 299/300 and FRA 300/300 default writes commit locally. These residuals are retained in [#1105](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6668472420), every sample, and the repeat-level table. No failed or slow sample is removed.

## Baseline cross-region reads

Latency is the first read request after the write response. Empty first reads are reported, not retried into successful measurements. Every response has `X-TerraBase-Region` equal to the targeted read region and is attributed to that region in `Server-Timing`. Final inventories reconcile the complete acknowledged ID set before the move; first-read misses are eventual-consistency observations.

| Plan | Write → read | Reads | Client p50 / p99 ms | Visible on first read | Served locally |
|---|---|---:|---:|---:|---:|
| Free | gru → sjc | 300 | 205.378 / 379.619 | 271/300 | 300/300 |
| Free | gru → fra | 300 | 211.733 / 356.793 | 296/300 | 300/300 |
| Free | sjc → gru | 300 | 23.295 / 175.133 | 294/300 | 300/300 |
| Free | sjc → fra | 300 | 214.238 / 373.271 | 291/300 | 300/300 |
| Free | fra → gru | 300 | 25.695 / 177.193 | 299/300 | 300/300 |
| Free | fra → sjc | 300 | 197.071 / 350.632 | 287/300 | 300/300 |
| Pro | gru → sjc | 300 | 204.063 / 430.549 | 276/300 | 300/300 |
| Pro | gru → fra | 300 | 214.718 / 429.349 | 293/300 | 300/300 |
| Pro | sjc → gru | 300 | 26.334 / 242.982 | 278/300 | 300/300 |
| Pro | sjc → fra | 300 | 214.482 / 406.572 | 281/300 | 300/300 |
| Pro | fra → gru | 300 | 25.524 / 234.394 | 291/300 | 300/300 |
| Pro | fra → sjc | 300 | 197.133 / 453.446 | 284/300 | 300/300 |

Before the move, all 1,923 acknowledged IDs are present with no unexpected IDs: 1,800 default writes, 120 acknowledgement/comparison writes, and three Free-plan psql writes. Free psql insert plus same-session read succeeds at gru/sjc/fra in 291.40/2,286.54/2,393.65 ms respectively. A later Pro psql check after the move succeeds at gru/fra in 1,155.70/2,015.57 ms; SJC exceeds 30 s and its uncertain insert is absent at the new home when reconciled. That post-move failure belongs to #1106.

## Free reads and CDN

Free billing reports no usage charges. On Pro, the before/after read-only check leaves write units at **981 → 981** and usage charges at **$0.00 → $0.00**. All 300 SQL reads succeed. SQL fair-use usage rises from the displayed 1.8k to an exact 2,077 read units; it is separate from the billable write counter. This proves the reads do not increment billable operation units; it is not a test of crossing the paid allowance or a finalized overage invoice.

Three public CDN GETs from each location return 200/HIT, matching 112,046 ciphertext bytes and SHA-256 `4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26`. The warmed second/third requests take GRU 37.127/49.479 ms, SJC 58.850/51.725 ms, FRA 66.151/71.583 ms. CF-Ray suffixes independently identify GRU/SJC/FRA. Ages are 360/362/362 seconds on the second request. Pro JS subscriptions through all three API regions also reach an up-to-date shape after the move, exposing the same missing-row total of 1,926.

## Home move and deletion

Move gru → fra returns 200 in **2,261.791 ms**, request `01M6PFHG9B0EGGAQYKM5ZHBE7C`. A single SJC writer pauses 500 ms between attempts; the move starts after three accepted inserts. Five inserts succeed, including two during the move. A sixth is refused with 421 and is excluded from expected data. Expected 1,928 rows; actual 1,926 distinct rows. The two missing acknowledged IDs are `01M6PFHGHWM3KE8YC88SDDDP89` and `01M6PFHHENZT9C54C8RFWFZVWZ`. They remain absent from later SQL checks at 18:29:49–18:29:54Z and all three Pro shape snapshots. No duplicate or changed row values are found. Pinned SJC SQL returns 421 `region_pin_mismatch`; unpinned SJC is served by fra.

Exact acknowledgement wall-clock timestamps and JetStream sequences were not captured. The raw sanitized response logs and IDs are retained; approximate completion reconstructed from client ULID creation plus measured duration is 18:29:06.655Z and 18:29:07.376Z. These are not asserted as exact server commit times. The fixer received the paths and this limitation.

Original database deletion completes at **18:33:44Z**, SDK success in **3,080.026 ms**, request `01M6PFSX4GBNX4KZ8RADRN7HWW`. Its SQL, metadata and previously successful shape epoch-key requests return 404 `unknown_database` at all three pinned APIs. The initial CDN recheck completes by 18:34:33.168464Z, at most **49.168464 s** after deletion; both checked sealed objects remain 200/HIT in GRU/SJC/FRA, with first-object Age 846/848/848. The clarified F11 deadline is 420 s, so that early result is not a failed purge gate. The timed recheck begins at **18:42:30.071786Z**, 526.072 s after deletion. All **21 GETs (7 SDK-used URLs × 3 regions)** return **200/HIT at 527.137–529.930 s**. First-object Age is GRU 1326, SJC 1328, FRA 1328; CF-Ray suffixes confirm each POP. This fails the 420-second gate ([#1107 final evidence](https://github.com/TerraScale/terrascale/issues/1107#issuecomment-6666293272)). Returned bytes are ciphertext; no plaintext-access claim is made.

Database cleanup is complete; final account-closure evidence is recorded below. Five databases were used on this one account: original, reserved, latency/R9, dedicated CDN, and fresh confirmation. All five are now deleted.

## Independent home-move rerun after #1106 fix

Deployed readiness: `b06bd58a8`, green CI [42096907611](https://github.com/TerraScale/terrascale/actions/runs/42096907611), GRU/SJC/FRA confirmed by the fixer. The independent public SDK workload runs on delta with SDK `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`, starting at 19:56:36.996959Z. Its exact baseline is 958 rows. A single pinned SJC writer pauses 500 ms between attempts; each move starts after its third insert attempt and continues through three attempts after the move response. No failed insert is retried.

| Move | Accepted / attempted writes | Move client ms | Exact rows in each region | Missing / duplicate / changed |
|---|---:|---:|---:|---|
| gru → fra | 14/14 | 7,403.772 | 972 | 0 / 0 / 0 |
| fra → gru | 14/15 | 7,527.563 | 986 | 0 / 0 / 0 |
| gru → fra | 17/17 | 9,668.226 | 1,003 | 0 / 0 / 0 |

All nine inventories match the complete expected ID/value set. Each pinned inventory response identifies the requested region in `X-TerraBase-Region`, including SJC after all three moves. The no-loss/no-duplicate gate is **PASS after fix**.

All **45/45 accepted SJC writes** report exactly `TerraScale-Write-Route: route=local; reason=ulid_insert; commit=local`: 14/14, 14/14 and 17/17 by move. No accepted insert forwards home or lacks this header. The SQL names all four columns explicitly and supplies a fresh ULID primary key. The one refused write has no route/commit header.

[#1106 is closed](https://github.com/TerraScale/terrascale/issues/1106) with this independent proof. The fixer's [final home-move evidence](2026-11-05-home-move-1106.md) ships at `01f0daa33`; the refused SJC insert remains tracked under #1105.

Stock psql with `sslmode=verify-full` also succeeds through all three regional PostgreSQL endpoints at 20:00:30–20:00:32Z, returning `1003|1003` for total/distinct IDs. Client durations including connection establishment are GRU 400.646 ms, SJC 1,768.470 ms and FRA 1,855.242 ms. The original SJC timeout remains in the baseline evidence.

One SJC insert during move 2 returns HTTP 400 `sql_error`: request `01M6PMJE24DEMZDJ63M4MDZ7CH`, row `01M6PMJDYQKVTJ0B1EB6VTKVSZ`, client UTC 19:56:59.223754Z → 19:56:59.425580Z, 201.805 ms client / 9.45 ms origin app. Sanitized SDK message: `the database does not exist`; SQLSTATE `0A000`, statement 0. It is absent from all final inventories and was never acknowledged. This serving defect is recorded in [#1105](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6667871689) and the [R9 evidence](https://github.com/TerraScale/terrascale/issues/1106#issuecomment-6667871258); passing acknowledged-row preservation does not erase it.

## Independent reader-drain fix confirmation — every repeat

Client `delta`; SDK `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`. Fresh Pro database `01M6PRBXSDAGS3GY5K1FP4ZDKG`, home GRU, all regions. Probe start `2026-11-05T21:03:20.097479+00:00`. Server revision below is the deployment handoff declaration; HTTP measurements are independent.

```json
{
  "kind": "environment",
  "host": "delta",
  "pid": 809563,
  "mode": "local-ack",
  "sdk_revision": "ddbc3a87d7c16cedf918cb3754bad364f7c068eb",
  "declared_server_revision": "006bc37f24d99d9984b876ca9d432e580a4f8155",
  "at": "2026-11-05T21:03:20.097479+00:00",
  "stop_file": "/tmp/mb-acceptance-rerun-env-q7538pfl/probe.stop"
}
```

The 100 SELECT-1 baselines per region precede every measured insert. Path baseline is nearest-rank p50 of successful client duration minus the last endpoint `app` timing. Every repeat is retained. Missing timing or fewer than 100 samples prevents a latency PASS.

| Region | Baseline success / attempts | Client / endpoint p50 ms | Path baseline ms | Client p99 budget ms |
|---|---:|---:|---:|---:|
| gru | 100/100 | 23.552 / 1.73 | 21.829 | 121.829 |
| sjc | 100/100 | 194.503 / 1.49 | 192.960 | 292.960 |
| fra | 100/100 | 213.426 / 1.41 | 212.005 | 312.005 |

| Region | Repeat | Accepted / attempts | Client p50 / p99 ms | Endpoint p50 / p99 ms | Endpoint samples | Client / endpoint max ms | Local / home commits | Endpoint / client gates |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| gru | 1 | 100/100 | 34.576 / 53.764 | 12.34 / 29.24 | 100/100 | 55.313 / 34.82 | 0 / 0 | PASS / PASS |
| gru | 2 | 100/100 | 34.472 / 60.430 | 12.26 / 37.96 | 100/100 | 95.401 / 72.62 | 0 / 0 | PASS / PASS |
| gru | 3 | 100/100 | 34.770 / 51.895 | 12.45 / 28.93 | 100/100 | 71.777 / 49.68 | 0 / 0 | PASS / PASS |
| gru | pooled | 300/300 | 34.609 / 55.313 | 12.38 / 34.82 | 300/300 | 95.401 / 72.62 | 0 / 0 | PASS / PASS |
| sjc | 1 | 100/100 | 216.001 / 230.475 | 21.50 / 36.76 | 100/100 | 240.086 / 44.55 | 100 / 0 | PASS / PASS |
| sjc | 2 | 100/100 | 219.386 / 247.525 | 24.55 / 52.92 | 100/100 | 248.170 / 54.35 | 100 / 0 | PASS / PASS |
| sjc | 3 | 100/100 | 215.697 / 237.768 | 21.54 / 44.31 | 100/100 | 249.419 / 54.83 | 100 / 0 | PASS / PASS |
| sjc | pooled | 300/300 | 216.308 / 246.117 | 21.88 / 51.61 | 300/300 | 249.419 / 54.83 | 300 / 0 | PASS / PASS |
| fra | 1 | 100/100 | 234.154 / 250.988 | 21.06 / 37.77 | 100/100 | 251.625 / 38.52 | 100 / 0 | PASS / PASS |
| fra | 2 | 100/100 | 234.918 / 251.568 | 21.79 / 37.68 | 100/100 | 256.886 / 39.71 | 100 / 0 | PASS / PASS |
| fra | 3 | 100/100 | 234.639 / 244.848 | 21.83 / 31.49 | 100/100 | 264.844 / 49.88 | 100 / 0 | PASS / PASS |
| fra | pooled | 300/300 | 234.529 / 251.568 | 21.63 / 37.77 | 300/300 | 264.844 / 49.88 | 300 / 0 | PASS / PASS |

| Region | First insert client / endpoint ms | Request ID | Route |
|---|---:|---|---|
| gru | 45.563 / 23.36 | `01M6PRCQ9BZFMT51B4GSWHBR4S` | absent (GRU home endpoint) |
| sjc | 227.096 / 33.19 | `01M6PRCQC3J49DAYWC2B42E0NG` | `route=local; reason=ulid_insert; commit=local` |
| fra | 241.648 / 28.76 | `01M6PRCQC90MK3RJKQPZ4ETN4F` | `route=local; reason=ulid_insert; commit=local` |

| Write → read | Successful / attempts | Client p50 / p99 ms | Visible first read | Target-local response header |
|---|---:|---:|---:|---:|
| gru → sjc | 300/300 | 201.809 / 220.378 | 293/300 | 300/300 |
| gru → fra | 300/300 | 214.707 / 237.047 | 300/300 | 300/300 |
| sjc → gru | 300/300 | 22.172 / 41.169 | 298/300 | 300/300 |
| sjc → fra | 300/300 | 214.492 / 236.272 | 293/300 | 300/300 |
| fra → gru | 300/300 | 22.323 / 34.700 | 296/300 | 300/300 |
| fra → sjc | 300/300 | 195.084 / 228.299 | 296/300 | 300/300 |

Exact ID/value comparisons:

```json
[
  {
    "kind": "exact_comparison",
    "phase": "exact_inventory",
    "region": "gru",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "phase": "exact_inventory",
    "region": "sjc",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "phase": "exact_inventory",
    "region": "fra",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  }
]
```

Failed application attempts: **0**. Raw API HTTP-error responses, including any SDK-recovered response: **0**. Auth HTTP-error responses: **0**. First immediate home-durable checks: **30/30**.

Every 100-write repeat latency gate: **PASS**. Baselines finish before every write: **True**. Exact inventories in all three regions: **PASS**. Strict item 2 result: **PASS**.

Deployment handoff: all six nodes healthy on `006bc37f24d99d9984b876ca9d432e580a4f8155` at20:57:45.966967Z; [CI42106659778](https://github.com/TerraScale/terrascale/actions/runs/42106659778) succeeds. The parent grants a freeze, the fixer finishes its cohort first, and this independent cohort runs21:03:20.097479Z–21:06:28.796946Z without overlapping fixer load. Public hosted signup creates a separate Free identity, `mariogk+mb-rerun-20261105-b1c4@terrascale.tech`; public Stripe Sandbox checkout upgrades it to Pro before database creation. Original account cleanup remains complete. The new account/database are retained temporarily for the separately assigned rollout proof; cleanup follows that proof.

All600 away writes carry exactly `route=local; reason=ulid_insert; commit=local`. GRU home writes omit that route header. Every raw response retains UTC, headers and request ID; no error or latency sample is removed. The24 empty first cross reads are eventual-visibility misses; final exact reconciliation has zero missing, duplicate, unexpected or changed rows. [Independent #1105 evidence](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6669577310). The prior SJC repeat failure above remains historical evidence.

## Independent CDN deletion rerun after #1107 fix

The deployment handoff identifies `afa0171a52` with CI [42094199071](https://github.com/TerraScale/terrascale/actions/runs/42094199071). A fresh all-regions Pro database `01M6PKKYX90SWSYCBYH9304XSK` (`milestone-b-cdn-retest`), home gru, has nine seeded rows. Official JavaScript SDK subscriptions reach all nine rows in all three regions. Its one SDK-used sealed URL is warmed twice in every POP, returning **200/HIT in GRU/SJC/FRA** with the SDK user agent; second-request Ages are 39/41/41. No manual purge is performed by acceptance.

DELETE returns **204**, completing at **2026-11-05T19:43:00.385929Z**. SQL, metadata and the previously valid key-release request return **404 unknown_database in every region**. The first post-delete CDN check returns:

| POP | Seconds since DELETE completion | HTTP | Age | CF-Cache-Status | CF-Ray |
|---|---:|---:|---|---|---|
| GRU | 17.341071 | 404 | absent | BYPASS | a55e58b80c6f864b-GRU |
| SJC | 18.936543 | 404 | absent | BYPASS | a55e58c21df5f31f-SJC |
| FRA | 19.445038 | 404 | absent | BYPASS | a55e58c53a1fdc9c-FRA |

This is an independent **PASS within 420 seconds** ([issue evidence](https://github.com/TerraScale/terrascale/issues/1107#issuecomment-6667583612)). It preserves the original failed run and does not count a later manual purge of the old prefix as a pass.

The fixer's separate [CDN evidence](2026-11-05-cdn1107.md), shipped at `d24792baba50d6c771a1941e6b11508924aefbd9`, records seven URLs across three POPs, all 404/BYPASS by 43.299 seconds. [#1107 is closed](https://github.com/TerraScale/terrascale/issues/1107#issuecomment-6667624071). Those measurements corroborate, and do not replace, the independent fresh Pro deletion above.

## Screenshots

Screenshots were captured with preview tools from the fresh account. Placement is available in both supported appearances. All paths below are repository files.

| Evidence | Path |
|---|---|
| Free placement, light/dark | [light](../../docs/qa/milestone-b-acceptance-20261105/placement-free-light.png), [dark](../../docs/qa/milestone-b-acceptance-20261105/placement-free-dark.png) |
| Pro placement, light/dark | [light](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-light.png), [dark](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-dark.png) |
| Pro after home move, light/dark | [light](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-after-home-move-light.png), [dark](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-after-home-move-dark.png) |
| Free billing | [billing](../../docs/qa/milestone-b-acceptance-20261105/billing-free.png) |
| Free usage before/during | [before](../../docs/qa/milestone-b-acceptance-20261105/usage-free-before.png), [during](../../docs/qa/milestone-b-acceptance-20261105/usage-free-during.png) |
| Pro usage before/after read-only workload | [before](../../docs/qa/milestone-b-acceptance-20261105/usage-pro-before.png), [after](../../docs/qa/milestone-b-acceptance-20261105/usage-pro-after-reads-light.png) |
| Pro usage after writes | [dark](../../docs/qa/milestone-b-acceptance-20261105/usage-pro-after-writes-dark.png) |
| Pro billing before/after reads | [before](../../docs/qa/milestone-b-acceptance-20261105/billing-pro-before.png), [after](../../docs/qa/milestone-b-acceptance-20261105/billing-pro-after-reads.png) |

## Original account cleanup

The original and dedicated CDN databases were deleted earlier. The reserved, latency/R9 and final confirmation databases are deleted through the public SDK; SQL and metadata return 404 `unknown_database` in GRU/SJC/FRA for each. The final database's previously valid shape-key requests return 200 before deletion and404 afterward in all 3 regions. All 3 regional database lists are empty. Final confirmation DELETE completes **20:28:33.875825Z**.

The final database's SDK-used ciphertext URL is warmed with 200/HIT from the three regional vantage points before deletion. After deletion, every vantage returns 404/BYPASS at 40.31–42.36 seconds, with no Age. CF-Ray identifies GRU, SJC and GRU respectively; the final FRA-network response identifies a GRU POP. The separate dedicated CDN rerun above independently proves actual GRU/SJC/FRA POP404 within 19.45 seconds.

`ts-throwaway-account --delete mb-acceptance-20261105-b1c4` exits 0 with `DELETED` at **20:30:37.789928Z**. At20:31:18–20:31:20Z, the retained pre-closure bearer is refused with 401 `invalid_key` by all 3 pinned APIs; fresh key exchanges return401 `invalid_client`. The retained bearer checks the project database list, so a deleted-database404 cannot masquerade as account revocation.

After the known-credential/JWT scan passed, all private credential files and probe environments on beta and mariogk-dev were removed at 2026-11-05T20:33:56.978786+00:00. Throwaway-helper tag files remaining:0. Sanitized request logs remain at the documented evidence path. The preview session list is empty; no owned tab remains open.

## Evidence and shipping

[Individual measured samples](2026-11-05-milestone-b-acceptance-samples.md) preserve timing, region, route, first-read visibility, row ID and request ID. The [detailed evidence](2026-11-05-milestone-b-acceptance-evidence.md) preserves sanitized HTTP responses, inventory digests, usage counters, deletion status, and CDN elapsed times/cache headers. Final cleanup and credential removal are recorded below. Raw sanitized files are retained on beta at `/home/mariogk/.t3/worktrees/terrascale/t3-b1c4ae61/benchmark/bench/milestone-b-acceptance-20261105/`. Credentials and epoch-key values are excluded from the committed evidence.

Shipping uses `ts-ship`, docs-only. Product tests are skipped under AGENTS.md; no product fixes or test-suite changes are included. Documentation validation checks sample counts, complete ID/value reconciliation, timing attribution, Markdown links/fences, screenshot hashes, and known credentials.
