# Concurrent regional local-write tails (#1105)

Date: 2026-11-05. Client: beta. GRU home, SJC/FRA replicas. Public Python SDK, public regional HTTPS APIs, fresh synthetic throwaway accounts. No load client ran on a serving node.

DECISION: Judge the primary acceptance gate on server `app` p99 <100 ms. Judge client p99 against the measured beta→region reused-connection baseline median +100 ms, replacing the fixed 200 ms client limit. Reason: beta's approximately 192 ms SJC /212 ms FRA network path already consumes the old client budget. Keep first measured insert <300 ms. Report baseline p99 too; retain all samples.

DECISION: Renew project access tokens in the background in Python (sync/async), JavaScript, Go, .NET and Rust, single-flight with initial/expired exchanges. Keep returning a still-valid token until actual expiry; explicitly refused tokens cannot be reused. Reason: a 60-second token's synchronous half-life renewal put issuer RTT on otherwise local requests. User authorized all five SDKs; the independent server fix shipped first.

DECISION: The independent acceptance rerun may use mariogk-dev rather than wait for beta admission. Measure that client’s own regional baseline first and apply the same server <100 ms and client <baseline +100 ms gates. Reason: the user explicitly authorized the placement change after beta lacked admission headroom. The comparable before/after evidence in this report remains beta-to-beta.

## Method

`benchmark/local-write-tails/run.py` preserves #1105's workload: three concurrent regional workers, each performing three measured repeats of 100 fresh ULID inserts. Each write is followed by SELECTs through the other two regions. There are 900 default writes and 1,800 cross-region reads per run. SDK retries are set to one attempt. Read-only warmup waits for a local replica (`replica_open`, no `home` span); no write primes the measured writer. Before warmup, 30 health requests on a reused connection measure each beta path; initial connection samples remain separately recorded. Ten home-durable and ten forced-home samples per region are separate diagnostics.

All percentiles use the retained observed order statistic `sorted[ceil(q*(n-1))]`: with 100 samples, p99 is the observed maximum. We do not drop first inserts, auth renewal, overload, failed requests or tail samples. `app` is the producer's server application time; nested `local_allowance` and `local_native` must not be summed into their parent `local_execute` again.

Comparable before run: beta 18:36:47Z, SDK/harness `3b037d6a616ae14aea2a100d927b52688edff66d`, server all six nodes `559eddd5d12062af3c53befed0b7387772ef4a43`, storage format 23. The earlier delta reproduction remains supplementary evidence, not the comparable client baseline.

## Root causes and changes

1. `DataKeys.CurrentAsync` invalidated the selected custody key every minute and synchronously listed R2 and checked the shred record while `TransactionCrypto.SealAsync` held the replica writer. Before, SJC local sealing took 847.55 ms cold and 318.41/263.56 ms in later repeats; FRA took 1,066.12 ms cold and 485.79/481.35 ms later. These are WAN custody operations, not F11 crypto CPU. Sealing now reuses only a rotation-valid cached key and queues selection refresh outside acknowledgement (64 deduplicated queued scopes, eight workers). Cold/missing/rotation-expired keys still require authoritative selection. Backups retain the existing authoritative selection. Synchronous peer revocation still evicts both caches before deletion succeeds.
2. First write also checked monthly organization usage by fanning out to peers. Read-only `SELECT 1` did not prepare this cache. Replica preparation now primes the durable sealing key and checks write allowance before making its copy available. Preparation consumes no units and does not bypass subsequent operation allowance checks. `local_allowance` and `local_native` spans distinguish these from SQL execution.
3. Python synchronously renewed 60-second access tokens at their 30-second half-life, adding about 150–200 ms outside `Server-Timing` repeatedly. JavaScript exchanged on every authenticated operation; Go/Rust already renewed in the background but waited before actual expiry; Rust also allowed foreground/background exchange overlap. All five SDKs now share renewal and keep still-valid tokens usable. Background failures are observed; permanent refusals remain authoritative; caller cancellation does not cancel another caller's exchange.

No evidence implicated #1103's forwarded-write HTTP pool in these local sealing tails. HLC cold reservation was approximately 4–7 ms, well below the custody stalls. No storage format bump or reset was required.

## Before (beta, all 300 writes per region)

| Region | Baseline p50 /p99 ms | Server app p50 /p99 /max ms | Client p50 /p99 /max ms | First insert client ms |
|---|---:|---:|---:|---:|
| GRU |22.795 /23.333|13.660 /44.770 /53.890|37.203 /210.616 /242.944|49.341|
| SJC |191.534 /192.883|19.750 /282.850 /876.260|212.272 /475.220 /1068.674|1068.674|
| FRA |211.709 /213.605|20.185 /500.450 /1094.270|233.214 /712.779 /1307.079|1307.079|

All 900 writes and 1,800 cross reads succeeded. Thirty-nine immediate cross reads returned no row before replication caught up; all final regional inventories contained all rows once. All 600 non-home default writes reported `route=local; reason=ulid_insert; commit=local`.

## After (beta, all 300 writes per region)

Live workload began at 19:03:11Z (writes received 19:03:19.741Z–19:05:43.997Z) using SDK source `061333746eb222faddc5d3f9c7d9c0a3ed339fb6` on beta. Before/after internal health snapshots at 19:01:47Z and 19:06:44Z confirm the same server `9a335684e` on all six nodes. SDK source equivalence to the final shipped Python implementation is checked separately below.

| Region | Baseline p50 /p99 ms | Client budget ms | Server app p50 /p99 /max ms | Client p50 /p99 /max ms | First insert client /server ms |
|---|---:|---:|---:|---:|---:|
| GRU |22.172 /85.875|122.172|12.615 /52.320 /65.130|35.441 /75.450 /90.198|43.038 /20.550|
| SJC |196.500 /198.592|296.500|20.330 /56.150 /627.930|217.838 /253.271 /826.010|253.271 /56.150|
| FRA |208.958 /209.908|308.958|20.015 /52.800 /88.430|230.014 /263.993 /297.859|263.993 /52.800|

The pooled 300-write gates pass for both non-home regions: server p99 <100 ms; client p99 <baseline median +100 ms; first insert <300 ms. Server p99 is also well below forced-home: the ten forced-home diagnostics had median home-call /server app time 200.35 /202.385 ms (SJC) and 195.05 /198.64 ms (FRA). Home-durable diagnostics intentionally waited for home, with median server app 212.695 /204.100 ms and client 406.406 /414.545 ms.

### Three measured repeats, without dropping outliers

| Region | Repeat | Client p99 ms | Server app p99 ms | Max local_seal ms | Max local_begin ms |
|---|---:|---:|---:|---:|---:|
| SJC |1|826.010|627.930|2.210|609.810|
| SJC |2|237.593|38.330|1.880|22.850|
| SJC |3|244.201|46.710|0.140|9.520|
| FRA |1|294.588|83.930|2.020|60.780|
| FRA |2|254.580|43.550|0.180|27.990|
| FRA |3|297.859|88.430|0.680|65.370|

SJC repeat 1 does **not** pass a per-repeat p99 <100 ms test under this conservative order statistic. The acceptance claim above is the specified pooled 300-write distribution; it is not a maximum-latency guarantee. The run retains one `commit=home` fallback (`01M6PHGMZY6BWATGKKWKBXFBSY`, app 223.13 ms), followed by a local commit (`01M6PHGNNJVP382N6KM1T5DK2C`, app 627.93 ms, `local_begin` 609.81 ms). Serving-node bounded stdout confirms `TursoNativeException` at writer open at 19:03:35.179209Z. `HomePush.BeginAsync` then synchronously pulls from home after forwarding, so the next local request includes that catch-up. These two samples are 0.67% of SJC's default writes. The fallback reason is distinct from custody refresh: its following local seal is 0.12 ms. This residual is disclosed for independent acceptance; it must not be misreported as an all-requests-under-100-ms result.

All 900 writes and 1,800 cross reads succeeded. Seventy-nine immediate reads were empty before eventual replication; this run does not claim immediate cross-region read-after-write consistency. Each final regional inventory has 300 acceptance rows for each writer, plus ten rows per writer for each diagnostic mode, with count equal to distinct primary-key count in every group (960 total). The independent acceptance rerun also reconciles every expected ID and value.

The two serving SJC/FRA nodes each completed two successful background custody refreshes during the interval: SJC 787.1905 ms total, FRA 814.7277 ms total. No refresh failures were recorded. Maximum local sealing over all non-home writes was 2.21 ms, including cold measured inserts, while these WAN refreshes ran outside acknowledgement. The updated SDK eliminates the earlier recurring client-side half-life stalls; per-HTTP token/API duration records remain in raw evidence.

All three throwaway accounts are confirmed deleted: `localtails-1105-20261105` (delta baseline), `localtails-beta-before-20261105`, and `localtails-beta-after-20261105`. Pending deletion was completed by rerunning the helper; no acceptance-thread credentials or database were used.

## Verification

Server shipment `9a335684e9b3a8b9eff9fe191444e91b7794628d`: TerraBase driver 4 tests /5.765 s, service 62 tests /9.826 s, E2E 1 test /4.205 s; auth 29 tests /7.327 s; benchmark Fast 0.265 s. A preceding qualification also passed but was discarded when its candidate changed; no unqualified main push occurred. Extended existing key deletion test proves sealing completes while custody listing is blocked and refuses after deletion; peer deletion/revocation critical paths remain green.

SDK shipment `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`: all five SDK Fast proofs are green on GitHub. Python runtime source is byte-identical to measured source `061333746` (`git diff 061333746 ddbc3a87d -- sdk/python/src` is empty). Each SDK has a held-renewal proof: a valid request finishes while issuer response is held, and expiry shares that exchange; existing happy-path tests remain retained.

| Area | Fast seconds |
|---|---:|
| .NET (both target frameworks combined) |6.742|
| JavaScript |8.894|
| Go |2.913|
| Python |1.139|
| Rust |2.439|
| Affected dashboard |7.810|
| Benchmark |0.274|

Python `ruff check src tests` and `mypy` passed. JavaScript declaration build and full checked-JavaScript typecheck passed. Rust formatting and `git diff --check` passed. SDK package publishing was not invoked. A Rust fixture import-path error was fixed before shipment; a JS test typing error was fixed after static checking, before the final qualified push. Qualification rebased and reran affected suites after the concurrent C1 cleanup push.

Server deployment CI: [42088276912](https://github.com/TerraScale/terrascale/actions/runs/42088276912) completed successfully. SDK proof runs: [Python](https://github.com/TerraScale/terrascale/actions/runs/42091985260), [JS](https://github.com/TerraScale/terrascale/actions/runs/42091985278), [Go](https://github.com/TerraScale/terrascale/actions/runs/42091985218), [.NET](https://github.com/TerraScale/terrascale/actions/runs/42091985188), [Rust](https://github.com/TerraScale/terrascale/actions/runs/42091985271). Unrelated older Slow probe failures reported by `ts-main` remain separate from these Fast/deployment proofs.

SDK main CI initially failed auth/libs/TerraBase because `ts-ship` omitted their exact-commit receipts for a .NET SDK change; the CI shared path filter correctly required them. There was no failing test. The installed runner rebuilt and ran all three Fast suites at the exact shipped `ddbc3a87d` revision through `ts-heavy-any`, then published genuine checked receipts: libs 6.210 s (build 27.870 s), auth 18.161 s (build 159.347 s), TerraBase 10.871 s (build 72.378 s). CI attempt 2 accepted all three at 19:25:58–19:25:59Z; its aggregate test gate passed at 19:26:07Z. A read-only readiness snapshot at 19:30:16Z confirmed `ddbc3a87d` healthy on all six GRU/SJC/FRA nodes, retaining format 23. No status was copied or manufactured and no test/workflow gate was weakened. The helper’s initial affected-area map needs to include these consumers for future .NET SDK shipments.

Raw before/after JSONL, summaries, six-node health and bounded refresh counter snapshots accompany this report. Secrets are absent. Accounts are synthetic and deleted after proof.


## Evidence and reproduction

- [Comparable beta before](local-write-tails-20261105/beta-before-summary.json), [raw JSONL gzip](local-write-tails-20261105/beta-before.jsonl.gz).
- [Beta after](local-write-tails-20261105/beta-after-summary.json), [raw JSONL gzip](local-write-tails-20261105/beta-after.jsonl.gz).
- [Before start](local-write-tails-20261105/beta-before-start-peers.json) and [end](local-write-tails-20261105/beta-before-end-peers.json) peer snapshots.
- [After start](local-write-tails-20261105/beta-after-start-peers.json) and [end](local-write-tails-20261105/beta-after-end-peers.json) peer/refresh snapshots.
- [SDK repair deployment readiness](local-write-tails-20261105/repaired-sdk-deployed-peers.json).
- [Residual writer-open busy log](local-write-tails-20261105/residual-writer-busy.json).
- [Earlier delta reproduction](local-write-tails-20261105/before-delta-summary.json), [raw gzip](local-write-tails-20261105/before-delta.jsonl.gz).
- [Harness instructions](../local-write-tails/README.md): run through `TS_HEAVY_HOST=beta ts-heavy-any`, with a beta hostname guard, against the pinned SDK source. All credentials stay in the helper's private files. Run `summarize.py` over retained samples.

Parked: rare native writer-open busy fallback plus synchronous post-forward home catch-up; #1103 forwarding-pool investigation remains independent. The specified pooled p99 and first-insert targets are met, with maxima and the failed SJC per-repeat distribution explicitly retained above.

Acceptance handoff: server deployed readiness and shipped SDK pin were delivered with `mode:steer` to beta acceptance `aeb225be` (environment `e70ee7fd-aace-4435-8419-2c2182950c48`) to rerun item 2 on a fresh Pro database using its own account. The user subsequently authorized mariogk-dev client placement with that host’s own measured path baselines. Its stronger exact-ID/value reconciliation and 100 SELECT-1 path baselines are independent proof, not results claimed here.


## Independent acceptance failure; previous completion withdrawn

The independent fresh Pro run after this handoff failed: 898/900 default writes succeeded, with two failed writes and four cross-read HTTP 400 responses in SJC/FRA. Original error messages were not retained; status/code, request IDs, timing and UTC intervals were retained in the [failed independent cohort](local-write-tails-20261105/independent-failed-cohort.jsonl.gz). Therefore the earlier pooled completion claim does not establish Milestone B acceptance. #1105 is reopened and remains open pending a new stable deployment proof.

SJC cold first insert was 436.084 ms client /239.85 ms server app, including 177.68 ms server authentication. Its read warmup had let the two-second KeyAuthority answer age out; this was a server status check, not SDK token exchange. The candidate keeps recently used, validated key tokens warm in a bounded background set through signed expiry, sharing existing single-flight checks. It preserves the two-second revocation/fail-closed bound.

SJC failures at 19:34:36.61–19:34:55.05Z coincide with the afa0171a5 rollout: the old process stopped listening at 19:34:36.522Z and replacement listeners opened at 19:34:53.246Z. The failed requests have no replica-open span. Persisted placement was gated behind an already-open replica, so pre-replica requests attempted the absent primary instead of forwarding to the home. The candidate reads persisted placement before that gate. Zero-downtime routing/draining is owned by the separate ts-orchestrator worker.

DECISION: retain the agreed server p99 <100 ms target and client p99 <the admitted client host’s measured SELECT-1 baseline median +100 ms, plus first insert <300 ms. Reason: beta’s SJC/FRA network path alone is about 195–210 ms, and the user authorized mariogk-dev placement with its own baseline. Load clients never run on serving nodes.

The next proof retains 100 SELECT-1 baselines before writes, sanitized SQL messages, issuer-exchange durations and precise UTC receipt times. No replacement live result is claimed in this section yet.

A later independent public home-move run reproduced the same admission error without a rollout: SJC request `01M6PMJE24DEMZDJ63M4MDZ7CH`, 19:56:59.223754–19:56:59.425580Z, HTTP 400 `sql_error`, sanitized message `the database does not exist`, app 9.45 ms. All 45 acknowledged rows reconciled after the three moves. This strengthens the pre-replica routing diagnosis independently of the edge restart gap; [acceptance evidence](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6667871689).
