# Concurrent regional local-write tails (#1105)

Status: #1105 closed after the reader-drain fix passed every100-write repeat in both the worker and independent Pro cohorts. The original failed cohorts and withdrawn pooled completion claim remain below.

Date: 2026-11-05. Original comparable client: beta. GRU home, SJC/FRA replicas. Public Python SDK, public regional HTTPS APIs, fresh synthetic throwaway accounts. No load client ran on a serving node.

DECISION: Judge the primary acceptance gate on server `app` p99 <100 ms. Judge client p99 against the measured beta→region reused-connection baseline median +100 ms, replacing the fixed 200 ms client limit. Reason: beta's approximately 192 ms SJC /212 ms FRA network path already consumes the old client budget. Keep first measured insert <300 ms. Report baseline p99 too; retain all samples.

DECISION: Renew project access tokens in the background in Python (sync/async), JavaScript, Go, .NET and Rust, single-flight with initial/expired exchanges. Keep returning a still-valid token until actual expiry; explicitly refused tokens cannot be reused. Reason: a 60-second token's synchronous half-life renewal put issuer RTT on otherwise local requests. User authorized all five SDKs; the independent server fix shipped first.

DECISION: The independent acceptance rerun may use mariogk-dev rather than wait for beta admission. Measure that client’s own regional baseline first and apply the same server <100 ms and client <baseline +100 ms gates. Reason: the user explicitly authorized the placement change after beta lacked admission headroom. The comparable before/after evidence in this report remains beta-to-beta.

## Method

`benchmark/local-write-tails/run.py` preserves #1105's workload: three concurrent regional workers, each performing three measured repeats of 100 fresh ULID inserts. Each write is followed by SELECTs through the other two regions. There are 900 default writes and 1,800 cross-region reads per run. SDK retries are set to one attempt. Read-only warmup waits for a local replica (`replica_open`, no `home` span); no write primes the measured writer. The original comparable before/after runs used 30 health requests on a reused connection to measure each beta path; initial connection samples remain separately recorded. Ten home-durable and ten forced-home samples per region are separate diagnostics.

The original comparable before/after tables use the retained conservative order statistic `sorted[ceil(q*(n-1))]`: with 100 samples, this is the observed maximum. Follow-up acceptance tables use empirical p99 `sorted[ceil(.99*n)-1]`, with the conservative statistic and maxima retained separately. We do not drop first inserts, auth renewal, overload, failed requests or tail samples. `app` is the producer's server application time; nested `local_allowance` and `local_native` must not be summed into their parent `local_execute` again.

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

SJC repeat 1 does **not** pass a per-repeat p99 <100 ms test under this conservative order statistic. The pooled completion claim from this run was subsequently withdrawn: acceptance requires every 100-write repeat to pass. The run retains one `commit=home` fallback (`01M6PHGMZY6BWATGKKWKBXFBSY`, app 223.13 ms), followed by a local commit (`01M6PHGNNJVP382N6KM1T5DK2C`, app 627.93 ms, `local_begin` 609.81 ms). Serving-node bounded stdout confirms `TursoNativeException` at writer open at 19:03:35.179209Z. `HomePush.BeginAsync` then synchronously pulls from home after forwarding, so the next local request includes that catch-up. These two samples are 0.67% of SJC's default writes. The fallback reason is distinct from custody refresh: its following local seal is 0.12 ms. This residual is disclosed for independent acceptance; it must not be misreported as an all-requests-under-100-ms result.

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

The native writer-open fallback and synchronous post-forward home catch-up remained unresolved in this original run; the reader-drain follow-up below addresses their admission boundary. No evidence implicated #1103 forwarding-pool contention. Pooled results alone do not satisfy acceptance.

Acceptance handoff: server deployed readiness and shipped SDK pin were delivered with `mode:steer` to beta acceptance `aeb225be` (environment `e70ee7fd-aace-4435-8419-2c2182950c48`) to rerun item 2 on a fresh Pro database using its own account. The user subsequently authorized mariogk-dev client placement with that host’s own measured path baselines. Its stronger exact-ID/value reconciliation and 100 SELECT-1 path baselines are independent proof, not results claimed here.


## Independent acceptance failure; previous completion withdrawn

The independent fresh Pro run after this handoff failed: 898/900 default writes succeeded, with two failed writes and four cross-read HTTP 400 responses in SJC/FRA. Original error messages were not retained; status/code, request IDs, timing and UTC intervals were retained in the [failed independent cohort](local-write-tails-20261105/independent-failed-cohort.jsonl.gz). Therefore the earlier pooled completion claim does not establish Milestone B acceptance. #1105 is reopened and remains open pending a new stable deployment proof.

SJC cold first insert was 436.084 ms client /239.85 ms server app, including 177.68 ms server authentication. Its read warmup had let the two-second KeyAuthority answer age out; this was a server status check, not SDK token exchange. The candidate keeps recently used, validated key tokens warm in a bounded background set through signed expiry, sharing existing single-flight checks. It preserves the two-second revocation/fail-closed bound.

SJC failures at 19:34:36.61–19:34:55.05Z coincide with the afa0171a5 rollout: the old process stopped listening at 19:34:36.522Z and replacement listeners opened at 19:34:53.246Z. The failed requests have no replica-open span. Persisted placement was gated behind an already-open replica, so pre-replica requests attempted the absent primary instead of forwarding to the home. The candidate reads persisted placement before that gate. Zero-downtime routing/draining is owned by the separate ts-orchestrator worker.

DECISION: retain the agreed server p99 <100 ms target and client p99 <the admitted client host’s measured SELECT-1 baseline median +100 ms, plus first insert <300 ms. Reason: beta’s SJC/FRA network path alone is about 195–210 ms, and the user authorized mariogk-dev placement with its own baseline. Load clients never run on serving nodes.

The next proof retains 100 SELECT-1 baselines before writes, sanitized SQL messages, issuer-exchange durations and precise UTC receipt times. No replacement live result is claimed in this section yet.

A later independent public home-move run reproduced the same admission error without a rollout: SJC request `01M6PMJE24DEMZDJ63M4MDZ7CH`, 19:56:59.223754–19:56:59.425580Z, HTTP 400 `sql_error`, sanitized message `the database does not exist`, app 9.45 ms. All 45 acknowledged rows reconciled after the three moves. This strengthens the pre-replica routing diagnosis independently of the edge restart gap; [acceptance evidence](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6667871689).


## Admission and authority follow-up qualification

Server/harness fix shipped as `26338d3151770a15137c6cb077b94d6da47c1257`. Exact-revision ts-ship gates passed: TerraBase 65 tests /10.865 s; driver 4 tests /5.486 s; auth 29 tests /17.488 s; benchmark Fast 0.241 s; the existing local E2E proof passed in 3.826 s. The new cold-replica forwarding test and all four key-revocation/authority tests each passed five consecutive runs (25 retained regression executions total). No expiry/revocation bound, test, admission hold or gate was weakened.

One preceding standalone full run passed all 65 functional tests but failed the 30-second wall budget at 30.153 s. This remains a failed qualification attempt; the exact-revision ts-ship service proof above subsequently passed within budget. Two earlier fixture-only errors (manual hosted-worker shutdown and a hardcoded home identity) were corrected before the five repeats and shipment.

The HTTP routing regression uses the actual home and away apps with a durable placement and no open replica. Its INSERT and SELECT forward to the home successfully and leave the away replica unopened. The authority regression proves background renewal across a warmup-to-write pause and then verifies that issuer failure still refuses after the renewed two-second answer expires.


## Frozen-window confirmation: repeat-level native tail remains open

CI42100601357 passed and all six nodes were healthy on `26338d315` at20:18:59Z; Auth release was skipped. Own Free and independent fresh Pro cohorts ran on delta inside the stable interval, with Python runtime source identical to SDK `ddbc3a87d`. Both completed900 default writes and1800 cross reads without SQL/HTTP errors. Independent confirmation reconciled all960 acknowledged IDs/values in every region. Its complete results, first inserts, per-repeat statistics and corrected endpoint timings are in the [acceptance report](2026-11-05-milestone-b-acceptance.md).

**#1105 stays OPEN; every100-write repeat must pass.** Independent SJC repeat1 failed at p99 202.83ms endpoint app /400.024ms client, with maxima551.04 /745.894ms. The first insert passed (235.391ms client /39.61ms app). At n21, request `01M6PNZ85YKBDC4T1B180CH7NP`, native writer setup refused and successful fallback paid184ms home RTT. At n22, `01M6PNZ8T08P9HZG281SWP32AM`, mandatory post-forward catch-up took `local_begin=531.58ms`. Serving logs confirm a `TursoNativeException` at n21. This is a later reader/writer contention tail, not a cold first write. The candidate drains pooled SQL readers with the existing exclusive replica gate through short local native commit, releasing it before home-durable waiting. Mandatory catch-up, stream publication and partner holds remain intact. At that point the reader-drain candidate had no live proof; its subsequent proofs are recorded below.

The own workload passed every repeat. Its added post-cohort validator then hit `KeyError: n` because diagnostic events lacked a sequence index, after every measured write/read had completed. Full GRU inventory verifies960 IDs/values offline; away inventories were not reached. The diagnostic metadata bug is fixed. Both own synthetic accounts are confirmed deleted.

DECISION: use the last endpoint `app` when forwarding includes multiple entries, path baseline = SELECT-1 elapsed minus endpoint app, and empirical p99 = sorted[ceil(.99*n)-1], matching acceptance. Reason: the first home app understates endpoint time. Keep the older conservative statistic and maxima as separate fields. SJC repeat1 fails under either convention. Recomputing acceptance baselines from rounded SDK/body durations differs from its authoritative header-receipt baselines by less than0.05ms.

Retained evidence: [own raw](local-write-tails-20261105/admission-own.jsonl.gz), [summary](local-write-tails-20261105/admission-own-summary.json), [validation](local-write-tails-20261105/admission-own-validation.json), [original runner log](local-write-tails-20261105/admission-own-runner.log.gz), [independent raw](local-write-tails-20261105/admission-independent-confirmation.jsonl.gz), [independent summary](local-write-tails-20261105/admission-independent-summary.json), [start snapshot](local-write-tails-20261105/admission-proof-start-peers.json), [end snapshot](local-write-tails-20261105/admission-proof-end-peers.json). One diagnostic JSON line split by runner stderr was rejoined without altering request fields; its original stream is retained. No measured write/read sample was lost.


## Reader-drain qualification and deployment

The reader-drain change shipped in `006bc37f24d99d9984b876ca9d432e580a4f8155` via ts-ship. Exact-revision qualification passed all 66 TerraBase service tests in 17.020 s, all four native driver tests in 7.296 s, auth's 29 tests in 7.412 s, and benchmark Fast in 0.300 s. The regression holds an actual replica reader, cancels writer admission, then releases the reader and verifies a new writer commits and the row is readable. All three replica regression tests passed five consecutive positive runs. Removing the reader drain makes the new regression fail because writer admission incorrectly succeeds while the reader is active; this negative control proves the missing exclusion contract, rather than recreating the exact native refusal from production. [Qualification evidence](local-write-tails-20261105/reader-drain-qualification.json).

DECISION: retain the mandatory post-forward causal catch-up and partner acknowledgment holds; drain readers before native writer setup instead. Reason: eliminating the catch-up would hide the latency while weakening read/write ordering. The exclusive gate is released when the local writer is disposed, before any requested home-durable wait. No format bump, SDK change, or rollout change is part of this follow-up.

CI [42106659778](https://github.com/TerraScale/terrascale/actions/runs/42106659778) passed, including production release; Auth release was skipped. All six nodes reported healthy on the shipped SHA at20:57:45Z. The bounded deployment follow-up was disabled after readiness. The parent then froze both repositories for25 minutes before the proofs below.


## Frozen reader-drain worker proof: every repeat passes

The own Free cohort ran on admitted **mariogk-dev**, through ts-heavy-any, starting20:59:05Z. Harness `527653f2740d28a657543643a0c3d5b7636c286a` uses Python SDK source byte-equivalent to `ddbc3a87d`. All300 SELECT-1 baseline requests finish before any measured write; no insert warms the measured writer. Six-node snapshots at20:58:49Z and21:02:50Z both show healthy `006bc37f2` throughout all regions.

**900/900 default writes and1800/1800 cross reads completed with zero SQL/HTTP errors. Every100-write repeat passes both gates, and all three first inserts are under300ms. All960 acknowledged IDs/values reconcile exactly in every region.** All600 SJC/FRA default writes report `commit=local`; there is no home fallback. Twenty-three immediate cross reads were empty while replication caught up; final exact comparisons have no missing, unexpected, duplicate or changed rows.

| Region | Path baseline median ms | Client limit ms | First insert client /app ms |
|---|---:|---:|---:|
|GRU|22.298|122.298|100.515 /28.99|
|SJC|192.531|292.531|260.583 /67.21|
|FRA|212.368|312.368|272.532 /58.47|

| Region | Repeat | Server app p99 /max ms | Client p99 /max ms | Both gates |
|---|---:|---:|---:|---|
|GRU|1|34.250 /37.080|63.680 /100.515|PASS|
|GRU|2|35.730 /52.160|58.641 /76.195|PASS|
|GRU|3|51.740 /54.310|75.273 /79.454|PASS|
|SJC|1|46.850 /67.210|241.392 /260.583|PASS|
|SJC|2|45.880 /46.460|240.400 /241.611|PASS|
|SJC|3|30.830 /40.750|223.231 /234.181|PASS|
|FRA|1|58.470 /62.460|272.532 /275.101|PASS|
|FRA|2|45.820 /53.760|257.357 /266.770|PASS|
|FRA|3|35.850 /44.150|248.772 /257.642|PASS|

Maximum local_begin was29.77ms in SJC /25.24ms in FRA; maximum local_seal was2.03 /2.29ms. Maximum server auth was11.29 /1.92ms. These are retained maxima, not sums of nested stages.

The harness completed successfully, but the runner returned1 because synthetic account deletion remained pending after its cleanup submissions. That exit is retained; it is not a workload failure or a silently ignored check. Cleanup is being completed separately. Acceptance received `mode:steer` HANDOFF/GO for its independent fresh Pro item2 rerun at21:03Z, inside the same freeze. #1105 remained open at this worker handoff until independent confirmation was checked.

Evidence: [raw JSONL](local-write-tails-20261105/reader-drain-own.jsonl.gz), [summary with all stages and routes](local-write-tails-20261105/reader-drain-own-summary.json), [gate and exact-inventory validation](local-write-tails-20261105/reader-drain-own-validation.json), [runner log](local-write-tails-20261105/reader-drain-own-runner.log.gz), [deployed readiness](local-write-tails-20261105/reader-drain-deployed-peers.json), [proof start](local-write-tails-20261105/reader-drain-proof-start-peers.json), [proof end](local-write-tails-20261105/reader-drain-proof-end-peers.json).


## Independent Pro confirmation: item2 passes; #1105 closed

The independent acceptance worker used the official SDK pinned at `ddbc3a87d7c16cedf918cb3754bad364f7c068eb`, client **delta**, and fresh Pro GRU-home/all-regions database `01M6PRBXSDAGS3GY5K1FP4ZDKG`. Measurement ran21:03:20.097479Z–21:06:28.796946Z, after the worker cohort and inside the same frozen interval. The [authoritative independent evidence](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6669577310) records every sample and all nine repeat gates passing. Its full updated [acceptance report](2026-11-05-milestone-b-acceptance.md) is shipped by that worker independently.

**900/900 default writes,1800/1800 cross reads, zero application/API/auth errors. All960 acknowledged IDs and values match exactly once in all three regions. All600 away defaults commit locally. All30 home-durable writes are immediately visible from GRU.** Twenty-four immediate cross reads were empty while replication caught up; none of these responses were discarded. All300 path baselines precede all writes.

| Region | Path baseline ms | Client limit ms | First insert client /app ms |
|---|---:|---:|---:|
| GRU |21.829|121.829|45.563 /23.36|
| SJC |192.960|292.960|227.096 /33.19|
| FRA |212.005|312.005|241.648 /28.76|

| Region | Repeat | Server app p99 ms | Client p99 ms | Both gates |
|---|---:|---:|---:|---|
| GRU |1|29.24|53.764|PASS|
| GRU |2|37.96|60.430|PASS|
| GRU |3|28.93|51.895|PASS|
| SJC |1|36.76|230.475|PASS|
| SJC |2|52.92|247.525|PASS|
| SJC |3|44.31|237.768|PASS|
| FRA |1|37.77|250.988|PASS|
| FRA |2|37.68|251.568|PASS|
| FRA |3|31.49|244.848|PASS|

Pooled SJC/FRA endpoint app p99 is51.61 /37.77ms; pooled client p99 is246.117 /251.568ms. These pooled values supplement the required repeat-level verdicts. First inserts are below300ms. Native setup no longer falls back home in either cohort, and the mandatory causal barrier remains intact.

Raw [independent evidence](local-write-tails-20261105/reader-drain-independent.jsonl.gz) is retained exactly as supplied, including two runner metadata lines. The [analysis normalization record](local-write-tails-20261105/reader-drain-independent-normalization.json) identifies those two non-product lines; all product events are included in the [recomputed summary](local-write-tails-20261105/reader-drain-independent-summary.json). Authoritative acceptance baseline limits above use its nearest-rank p50/header-receipt calculation; rounded SDK durations used by the supplementary summarizer differ by only microseconds.

[Issue #1105 closed with both cohorts and all repeat numbers](https://github.com/TerraScale/terrascale/issues/1105#issuecomment-6669592226). Deployed readiness and item2 HANDOFF were delivered to acceptance with `mode:steer`; the independent item2 rerun passed. Account cleanup follows separately under the parent's instruction and does not change the SQL verdict.
