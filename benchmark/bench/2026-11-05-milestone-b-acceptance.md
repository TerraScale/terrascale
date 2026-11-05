# Milestone B end-to-end acceptance — 2026-11-05

**Overall: FAIL.** The public product accepts writes and serves local reads in all three regions before the home move, and SQL reads do not increase billable write units. Default acknowledgement tails fail #1105. The original home move loses two acknowledged rows (#1106); three independent post-fix moves now preserve every acknowledged row, with one refused insert disclosed. The initial CDN deletion deadline also failed (#1107); a fresh post-fix deletion now returns 404 in every region within 20 seconds. The first post-fix latency run overlaps a rollout and still fails; baseline-first confirmation and final account closure are pending.

| Item | Result | Independently observed evidence |
|---|---|---|
| Fresh account, Free → Pro | PASS | Hosted sign-up, real email verification, dashboard onboarding, API key creation, and public Stripe **sandbox** checkout; Pro active. No real-money activation. |
| 1. Write each region; read other regions | PASS baseline; FAIL during rollout | Each plan: 900/900 writes; 1,800 cross-region read requests, all served in the requested region. Final pre-move reconciliation includes every acknowledged ID exactly once. Eventual visibility, not immediate cross-region read-your-writes. Post-fix rollout run: 898/900 writes; 4 cross-read SQL errors; all 958 acknowledged rows reconcile exactly in all regions. |
| 2. Default local ack | FAIL; confirmation held | Free SJC/FRA server p99 351.95/391.63 ms; Pro 169.51/455.34 ms, versus approximately 200 ms home call. First post-fix run during rollout: SJC/FRA pooled app p99 607.32/462.11 ms. [#1105](https://github.com/TerraScale/terrascale/issues/1105). |
| 2. Opt-in home-durable ack | PASS | 30 accepted home-durable writes per plan; additional Pro functional check: 9/9 writes immediately visible at home; post-fix run adds 30/30 immediate home reads. |
| 3. SQL reads are not billed | PASS | Pro read-only workload: 300/300 successful reads; write units stay 981; usage charges stay $0.00. Read fair-use units are separate. |
| 3. CDN cache in all regions | PASS | SDK-used shape ciphertext: 200/HIT at GRU, SJC and FRA with `terrascale-js/0.0.19`; matching bytes and digest. |
| 4. Move home under light writes | PASS after fix; initial FAIL | Original move loses 2 accepted rows. Post-fix: 3/3 moves, 45 accepted load writes, all 1,003 IDs/values exactly once in every region; 9/9 inventories target-local, including pinned SJC. One of 46 insert attempts is refused during move 2. [#1106](https://github.com/TerraScale/terrascale/issues/1106#issuecomment-6667871258). |
| 5. Database data and keys | PASS after fix; initial FAIL | Original deletion: APIs/key routes 404, but all 21 cached-object GETs remain 200/HIT at 527–530 s. Fresh post-fix deletion: APIs/key routes 404; warmed CDN URL 404/BYPASS in all three POPs at 17.34–19.45 s. [#1107](https://github.com/TerraScale/terrascale/issues/1107). |
| 5. Account closure | PENDING | Account retained for the explicitly requested post-fix #1105 rerun; original database is deleted. |

## Method and scope

Fresh identity: `mariogk+mb-acceptance-20261105-b1c4@terrascale.tech`, organization `01M6PENTY9976KJDP65GTQDQVK`, project `01M6PENV06CRPSJYDDRZTF75R4`. Created with `ts-throwaway-account mb-acceptance-20261105-b1c4`. Original database `01M6PER4PDC8FM24ZYKX1PJ6JE` (`milestone-b-acceptance`) was created in the dashboard with home gru and All regions, returning replicas fra/sjc. The fresh follow-up database is `01M6PFQFV240ECH0BCQ5AKFYHX` (`milestone-b-ack-retest`), home gru, All regions.

Only hosted auth, public dashboard/API, official Python/JavaScript SDK source, and stock psql were used as product interfaces. Python SDK source: `c27dbfa6220ca1a7857b6dc0c95bef220e8684cd`; JavaScript SDK 0.0.19. Public health identifies region and version 0.1.0; it does not identify the deployed backend source SHA. No earlier thread report substitutes for the measurements here. No product code was changed.

Public load probes ran through `ts-heavy-any` on dev hosts, primarily beta (the move probe ran on delta). Three client workers ran concurrently, one per write region. Each plan had three measured repeats of 100 writes per region, with read-only warmups and no application-request retries. Each write used a fresh ULID in `acceptance_rows (id TEXT PRIMARY KEY, writer TEXT, sequence INTEGER, body TEXT)` and was followed by SELECT-by-ID from each other region. Percentiles use nearest rank and include the first measured write. Client times include the dev-host network path; they are not claimed as in-region client times. Server `app` timings isolate origin work; the forced-home `home` timings provide the region-to-home comparison.

The regional HTTP clients use `base_url=https://<region>.api.terrascale.tech` and the SDK's `region=<region>` pin. Default inserts use `INSERT INTO acceptance_rows (id, writer, sequence, body) VALUES (?, ?, ?, ?)`. Cross reads use `SELECT id, writer, sequence, body FROM acceptance_rows WHERE id = ?`. Home-durable comparison requests add `TerraScale-Durability: home`; forced-home comparison requests add `TerraScale-Write: home`. Local attribution uses `X-TerraBase-Region`, `X-TerraBase-Machine` and the region in `Server-Timing`; `TerraScale-Write-Route` records the selected route and commit mode. A successful HTTP response containing an empty row set counts as a first-read miss.

CDN checks used existing gru/sjc/fra nodes solely as network vantage points for a handful of unauthenticated public HTTPS GETs of SDK-returned sealed object URLs. No internal product endpoints or storage were read. The JavaScript SDK was used with its supported fetch adapter to record responses and identify requests with `terrascale-js/0.0.19`. Epoch-key values and credentials were never logged. psql used verify-full TLS with system roots and a temporary mode-0600 pgpass file, removed afterward.

DECISION: Use server app p99 <100 ms as the post-fix primary local-ack gate, with client p99 < the measured beta-to-region path baseline +100 ms. Reason: the initial load client is beta; its path to SJC/FRA already costs roughly 200 ms. The post-fix run will measure 100 read-only baseline requests per region and retain both end-to-end and server timing. This follows the parent’s Q-RTT decision. A later Q-BETA decision authorizes mariogk-dev for the independent post-fix run when beta lacks admission headroom, using mariogk-dev’s own path baseline and the same <100 ms origin / baseline +100 ms client gates. Production nodes do not run load clients.

## Writes and local acknowledgement

| Plan | Endpoint | Accepted | Client p50 / p99 ms | Server app p50 / p99 ms | Non-home `commit=local` |
|---|---|---:|---:|---:|---:|
| Free | gru | 300/300 | 33.488 / 183.873 | 12.67 / 36.31 | home execution |
| Free | sjc | 300/300 | 214.261 / 545.670 | 19.94 / 351.95 | 300/300 |
| Free | fra | 300/300 | 231.447 / 602.066 | 19.83 / 391.63 | 300/300 |
| Pro | gru | 300/300 | 36.822 / 280.697 | 13.91 / 63.85 | home execution |
| Pro | sjc | 300/300 | 216.623 / 527.401 | 20.48 / 169.51 | 300/300 |
| Pro | fra | 300/300 | 231.828 / 671.412 | 20.42 / 455.34 | 297/300 |

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
| Pro | fra | 1 | 235.379 / 672.349 | 22.58 / 459.40 |
| Pro | fra | 2 | 230.019 / 429.584 | 19.48 / 39.14 |
| Pro | fra | 3 | 230.764 / 471.876 | 19.98 / 35.65 |

## Independent post-fix Pro run during a rollout

SDK source: `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`. Server deployment handoff: `9a335684e9b3a8b9eff9fe191444e91b7794628d`, confirmed by the fixer on all six nodes at 19:01:47Z. The following measurements are independent acceptance requests, not the fixer’s results. This run began before the parent’s request to measure the path baseline first; it retained its original baseline-after-writes order. A separate baseline-first confirmation is held for further readiness. The run also overlaps the afa0171 rollout; no failed or slow samples are discarded. A newly created all-regions database captures first inserts after read-only warmups.

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
| sjc | 2 | 99/100 | 218.369 / 2211.595 | 21.79 / 881.77 | 80/100 | FAIL / FAIL |
| sjc | 3 | 100/100 | 219.605 / 816.145 | 21.15 / 218.11 | 100/100 | FAIL / FAIL |
| sjc | pooled | 299/300 | 218.062 / 1077.745 | 21.66 / 607.32 | 280/300 | FAIL / FAIL |
| fra | 1 | 100/100 | 232.397 / 267.299 | 20.82 / 53.97 | 100/100 | PASS / PASS |
| fra | 2 | 100/100 | 233.769 / 877.049 | 20.41 / 293.02 | 100/100 | FAIL / FAIL |
| fra | 3 | 99/100 | 242.038 / 1928.428 | 21.62 / 620.04 | 70/100 | FAIL / FAIL |
| fra | pooled | 299/300 | 235.487 / 968.217 | 20.95 / 462.11 | 270/300 | FAIL / FAIL |

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

Account closure and deletion of the fresh follow-up database remain pending the explicitly requested post-fix #1105 rerun. The original, reserved, latency-rerun and dedicated CDN-rerun databases are the only databases used on this one account. The dedicated CDN database is also deleted.

## Independent home-move rerun after #1106 fix

Deployed readiness: `b06bd58a8`, green CI [42096907611](https://github.com/TerraScale/terrascale/actions/runs/42096907611), GRU/SJC/FRA confirmed by the fixer. The independent public SDK workload runs on delta with SDK `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`, starting at 19:56:36.996959Z. Its exact baseline is 958 rows. A single pinned SJC writer pauses 500 ms between attempts; each move starts after its third insert attempt and continues through three attempts after the move response. No failed insert is retried.

| Move | Accepted / attempted writes | Move client ms | Exact rows in each region | Missing / duplicate / changed |
|---|---:|---:|---:|---|
| gru → fra | 14/14 | 7,403.772 | 972 | 0 / 0 / 0 |
| fra → gru | 14/15 | 7,527.563 | 986 | 0 / 0 / 0 |
| gru → fra | 17/17 | 9,668.226 | 1,003 | 0 / 0 / 0 |

All nine inventories match the complete expected ID/value set. Each pinned inventory response identifies the requested region in `X-TerraBase-Region`, including SJC after all three moves. The no-loss/no-duplicate gate is **PASS after fix**.

All **45/45 accepted SJC writes** report exactly `TerraScale-Write-Route: route=local; reason=ulid_insert; commit=local`: 14/14, 14/14 and 17/17 by move. No accepted insert forwards home or lacks this header. The SQL names all four columns explicitly and supplies a fresh ULID primary key. The one refused write has no route/commit header.

Stock psql with `sslmode=verify-full` also succeeds through all three regional PostgreSQL endpoints at 20:00:30–20:00:32Z, returning `1003|1003` for total/distinct IDs. Client durations including connection establishment are GRU 400.646 ms, SJC 1,768.470 ms and FRA 1,855.242 ms. The original SJC timeout remains in the baseline evidence.

One SJC insert during move 2 returns HTTP 400 `sql_error`: request `01M6PMJE24DEMZDJ63M4MDZ7CH`, row `01M6PMJDYQKVTJ0B1EB6VTKVSZ`, client UTC 19:56:59.223754Z → 19:56:59.425580Z, 201.805 ms client / 9.45 ms origin app. Sanitized SDK message: `the database does not exist`; SQLSTATE `0A000`, statement 0. It is absent from all final inventories and was never acknowledged. This serving defect is recorded in [#1105](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6667871689) and the [R9 evidence](https://github.com/TerraScale/terrascale/issues/1106#issuecomment-6667871258); passing acknowledged-row preservation does not erase it.

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

## Evidence and shipping

[Individual measured samples](2026-11-05-milestone-b-acceptance-samples.md) preserve timing, region, route, first-read visibility, row ID and request ID. The [detailed evidence](2026-11-05-milestone-b-acceptance-evidence.md) preserves sanitized HTTP responses, inventory digests, usage counters, deletion status, and CDN elapsed times/cache headers. Final cleanup confirmation will be completed before shipping. Raw sanitized files are retained on beta at `/home/mariogk/.t3/worktrees/terrascale/t3-b1c4ae61/benchmark/bench/milestone-b-acceptance-20261105/`. Credentials remain in private temporary files and the throwaway helper state, never in committed evidence.

Shipping: pending. This change contains documentation and screenshot assets only; no product fixes or test-suite changes.
