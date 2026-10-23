# Dashboard CI baseline audit

Fix status: main commit `d5c4c341` (#84) repairs the stale `/auth/overview` assertion and checks the issuer and unavailable-management text. Four baseline CI failures were caused by that assertion, not a product regression. The CI PR carries no copy of that upstream repair. The gru release failure is an external runtime blocker; its missing TerraBase listener was handed off by the parent. No app, workflow, issue, PR, infrastructure, credential, or storage changes were made by this audit.

## Baseline boundary and evidence

- Repository: `TerraScale/ts-dashboard`; audited commit: `46c126dde0b310790e6ab08d4d5214839dc9c4ce`.
- Collection started: **2026-10-23T01:35:06.653051+00:00**; last of four queries returned **2026-10-23T01:35:17.129019+00:00**. These are four sequential snapshots, not an atomic cutoff. No run-list query was repeated.
- CI and Slow each contain their newest 100 runs. Images has only 26 runs and CodeQL only one. Two CI runs were still in progress at collection; they are retained as in-progress and their later results were not fetched.
- Detailed raw metadata, redacted logs for all 47 failed runs, label timelines and historical workflow snapshots are scratch only at `/home/mariogk/.cache/terrascale-ci-audit/dashboard-20261023T013506Z/`. No raw logs remain under the repository. Public artifacts here contain only this summary, sanitized measurements, sample CSV, and the API-command budget ledger.

| Workflow | Runs | Created-at window UTC | Outcomes at collection |
|---|---:|---|---|
| CI | 100 | 2026-10-21T12:50:50Z → 2026-10-23T01:34:21Z | in_progress: 2, failure: 19, success: 50, cancelled: 29 |
| Slow | 100 | 2026-10-21T12:56:57Z → 2026-10-23T01:34:21Z | skipped: 27, success: 15, cancelled: 31, failure: 27 |
| Images (GHCR) | 26 | 2026-10-21T12:56:35Z → 2026-10-23T01:20:25Z | failure: 1, success: 25 |
| CodeQL | 1 | 2026-10-22T16:44:42Z → 2026-10-22T16:44:42Z | success: 1 |

## Measured timing: sampled, not full-population medians

Exactly **6 of the 10 permitted job/timing calls** were used. The lead retains **4 calls for AFTER**. Selection was diagnostic: latest failed main CI, latest successful PR CI, latest successful main Slow, latest failed PR Slow, latest failed Images, and the only CodeQL run. Event/conclusion mixtures make these descriptive sample medians, not a controlled benchmark or a claim of savings.

Wall = first job start to last job end. Initial queue = workflow creation to first job start, separately; it includes admission and runner wait. Runner job-minutes = sum of actual non-skipped job durations; excludes waiting and is not GitHub billed minutes. Full-population runner job-minutes and full-population wall medians are unmeasured.

| Workflow | Sample n | Median wall seconds | Median initial queue seconds | Median runner job-minutes | Total measured runner job-minutes |
|---|---:|---:|---:|---:|---:|
| CI | 2 | 334.5 | 14.0 | 5.575 | 11.150 |
| Slow | 2 | 238.5 | 142.5 | 3.975 | 7.950 |
| Images (GHCR) | 1 | 179.0 | 38.0 | 2.983 | 2.983 |
| CodeQL | 1 | 699.0 | 242.0 | 6.300 | 6.300 |

| Run | Event / result | Wall seconds | Initial queue seconds | Runner job-minutes |
|---|---|---:|---:|---:|
| [39883426465](https://github.com/TerraScale/ts-dashboard/actions/runs/39883426465) | CI: push / failure | 314 | 25 | 5.233 |
| [39883088841](https://github.com/TerraScale/ts-dashboard/actions/runs/39883088841) | CI: pull_request / success | 355 | 3 | 5.917 |
| [39883426595](https://github.com/TerraScale/ts-dashboard/actions/runs/39883426595) | Slow: push / success | 254 | 77 | 4.233 |
| [39872842398](https://github.com/TerraScale/ts-dashboard/actions/runs/39872842398) | Slow: pull_request / failure | 223 | 208 | 3.717 |
| [39883426565](https://github.com/TerraScale/ts-dashboard/actions/runs/39883426565) | Images (GHCR): push / failure | 179 | 38 | 2.983 |
| [39816512800](https://github.com/TerraScale/ts-dashboard/actions/runs/39816512800) | CodeQL: workflow_dispatch / success | 699 | 242 | 6.300 |

- The 6 runs cover **9 non-skipped jobs** and **28.383 measured runner job-minutes**. Do not extrapolate this total to the baseline.
- CodeQL serialized four languages: 699s wall, 378s total running, 321s between jobs, plus 242s initial wait. Per-job creation-to-start waits include matrix admission; they must not be reported as pure runner queue. Concurrency is already limited to one job.
- The successful PR CI sample used 293s for `make check`, 36s for image build, and 2s for image readiness. The failed main sample spent 297s in `make check`; reducing image work would not fix that failure.
- Main Slow used 40s build, 11s Chromium/system dependency installation, 182s selected tests. Images used 44s build/push and 114s release. A successful build is not release-health evidence.

## Every failure: fixed versus unresolved

**47 failed runs: CI 19, Slow 27, Images 1, CodeQL 0.** Primary groups below partition all 47 runs exactly once. Independent cause incidence overlaps: the auth barrier appears in 2 runs, webhook transport in 3, with one run containing both. Old dialog animation aborts occur in 9 runs, 8 of which also contain the old SQL assertion failure.

| Primary failure group | Runs | Status / action |
|---|---:|---|
| retired-registry-login | 4 | **Fixed in audited source**. Retired registry login was still executed after Fast passed; received HTTP 401. Workflow no longer includes that hosting step. Never restore credentials for the retired deployment. |
| stale-sql-assertion | 24 | **Fixed on main; historical branch failures**. SQL validation correctly returned HTTP 422 for an unbound parameter; browser audit expected 200. Main fixes #83/#85 assert UnprocessableEntity. Some runs also hit the independently fixed animation abort. |
| stripe-setup-formatting | 6 | **Fixed on main (#93)**. dotnet format rejected StripeSetupCommand.cs whitespace at lines 69, 81, and 137. #93 formats setup-stripe. These are lint failures, not failing application tests. |
| dialog-animation-abort | 1 | **Fixed on main (#85)**. Reopening the credential dialog canceled animation.finished; Promise.all rejected with AbortError. #85 changed both waits to Promise.allSettled. This group is the run whose only cause was that abort. |
| hosted-auth-delegate-compile | 1 | **Fixed later on PR #99 branch**. ASP0016 at AuthEndpoints.cs:30: RequestDelegate discarded a Task<IResult> result. Correct the route-handler delegate type; later PR #99 Fast and Slow runs succeeded. |
| hosted-auth-browser-fixture | 2 | **Fixed later on PR #99 branch; outside audited main**. Hosted-auth change left browser login helpers without the expected landed account/session. Two runs failed 15 method identities each: 404 navigation, missing seeded workspace data, and selector timeouts. 14708901 migrated journeys to landed hosted-page login; later dd3c3d23 removed axe from the JavaScript-disabled page. Both subsequent Slow runs succeeded; no same-head five-repeat flake proof. |
| auth-barrier | 1 | **Quarantined #112; owner MarioGK; deadline 2026-10-24T01:48:00Z**. ConcurrentNativeOperatorRequestsShareTheSinglePersistedRotation never observed userinfoPauses. PlatformRoleJourneyTests.cs:98 waits for the second request at the external producer while the first refresh is held; WaitForObservation polls 100 times with 50ms delay. Inspect the second request outcome and explicit session/producer barriers; make ordering deterministic before extending a timeout. |
| auth-barrier-and-webhook-race | 1 | **Both quarantined #112; owner MarioGK; deadline 2026-10-24T01:48:00Z**. Both the auth producer-barrier failure and the oversized webhook transport failure occurred. Count this run once in unique-run totals, but in both root-cause incidence totals. |
| webhook-transport-race | 2 | **Quarantined #112; owner MarioGK; deadline 2026-10-24T01:48:00Z**. OversizedWebhookAllowsFollowUpPostOnSameClientPool(False, 65537) threw HttpRequestException/Broken pipe while sending the rejected upload. StripeWebhookEndpoints.cs:23/50 immediately returns 413 plus Connection: close beyond its drain allowance; HttpClient can still be writing. Fix/reproduce the upload-versus-close handshake or rejection drain lifecycle; keep the follow-up POST check and do not mask it with generic retries. |
| stale-auth-console-assertion | 4 | **Fixed on main in d5c4c341 (#84); CI AFTER not measured**. PR #110 intentionally changed /auth/overview from 503 to 200. DatabaseMcpJourneyTests.cs:180 retained the 503 expectation. Four runs received OK. Main commit d5c4c341 (#84) now checks 200 plus the issuer and unavailable-management text; the snapshot and timing samples remain pre-fix. |
| deployment-readiness | 1 | **External runtime blocker; not a build/test flake**. Image build/push succeeded. Release failed gru application readiness after 60 seconds; rollback failed too. sjc/fra responses were skipped-not-targeted, despite the wrapper success wording. Parent identified a missing TerraBase listener in gru and handed it off through t3-remote. Runtime diagnosis is parent-supplied; this audit only inspected workflow evidence and made no infrastructure change. |

Current test repair is narrow: keep the intended 200 Authentication console behavior and update the stale test. The expected MCP `replace_document` precondition-failure message in those logs is diagnostic output from an intentional negative operation; the failed assertion is the subsequent `/auth/overview` 503 expectation.

The two unresolved race candidates are quarantined under [#112](https://github.com/TerraScale/ts-dashboard/issues/112), owner **MarioGK**, deadline **2026-10-24T01:48:00Z**. Only the concurrent auth method and the nonchunked webhook case with excess >64 KiB skip temporarily; three other webhook parameter cases still run. All assertions stay intact. The quarantine expires automatically at the deadline, so it cannot silently become permanent. These candidates require explicit reproduction by their owner. No local heavy tests or five-repeat flake validation ran here. Do not label a passing later run as a proven flake fix, add blind retries, or change auth/storage behavior from this audit. The historical SQL and dialog fixes are present in audited main; late branch runs based on older source do not mean those fixes are missing on main.

## Slow pull requests and full-ci

1. Main introduced the `full-ci` gate in `12a65efd1791d6ebf6a121b73e54762cba34a8ad` (#95), committed **2026-10-22 17:04:19 UTC**. Earlier workflow versions ran all PRs; the intermediate #95 revision used browser-path/draft filtering. Earlier heavy runs therefore did not require `full-ci`.
2. Before that boundary the baseline contains **44 PR runs: 19 failure, 6 success, 17 cancelled, 2 skipped**. PR #95 run [39799075866](https://github.com/TerraScale/ts-dashboard/actions/runs/39799075866) ran without a `full-ci` label; its complete 11-event timeline has no label event and its checked-out workflow used the intermediate draft/path policy.
3. After the boundary there are **31 PR runs: 25 skipped, 2 failure, 2 success, 2 cancelled**. All six non-skipped runs belong to **PR #99 `feat/ha1-hosted-auth`**. Its complete 14-event timeline records `full-ci` added **2026-10-22 23:26:18 UTC**, three seconds before run 39871778918. No removal is recorded by collection. Thus the six later non-skipped PR runs are expected labeled runs; cancellation does not itself prove that a runner was allocated.
4. PR #99’s two failures were 39871778918 and 39872842398; later successes were 39876733819 and 39883064885. The broad failures were hosted-login fixture migration, not the old 422-versus-200 SQL contract failure. Raw local head snapshots were unavailable for 11 historical heads; those are explicitly marked in scratch, and head files are not claimed to be exact PR merge-ref workflow files.

## Actionable workflow findings for the lead

1. **Publication/release is independent of CI success.** Images triggers directly on main pushes while CI runs separately. The same `46c126dd` main head failed Fast yet already published its image and attempted release. Preserve deployment/security holds; gate advancement on successful CI for the exact commit rather than using image-build success as proof. This audit did not change or activate that path.
2. **Keep the already-working Slow label gate.** The after-gate evidence shows expected behavior; do not remove browser coverage because old unfiltered runs dominate the 100-run window. Current Slow has no markdown path exclusion, so every main markdown push still runs the full browser suite.
3. **PR cache writes remain permitted by setup actions.** CI and Slow set setup-dotnet `cache: true` and setup-node `cache: npm` without a main-only save condition. These actions normally save after successful jobs, including PRs. Chromium explicitly separates restore from main/dispatch save; match the main-only save rule for package caches without deleting security audit steps. This is a source finding; no cache save/hit-rate population was measured.
4. **Dependabot config is absent.** Add one entry per npm/NuGet ecosystem with `open-pull-requests-limit: 0`, weekly schedule and a security-updates group, per `.cos/ci-rules.md`. Preserve the current aggregate check name `check`; this audit did not inspect branch protection or rename checks.
5. **No measured savings claim yet.** CodeQL is already weekly, manual, main-only, four languages serialized; all current jobs have timeouts. The 6 samples are the BEFORE budget. Use the 4 reserved calls on comparable AFTER cohorts and report queue separately. Main/PR suite cost is mostly `make check`; keep each area’s correctness/security coverage when optimizing.

## API budget and exact commands

`dashboard-baseline-api-budget.json` records every command, timestamp and outcome. Four `gh run list --repo TerraScale/ts-dashboard --workflow <ci.yml|slow.yml|images-ghcr.yml|codeql.yml> -L 100 --json databaseId,workflowDatabaseId,workflowName,name,displayTitle,event,status,conclusion,createdAt,startedAt,updatedAt,headBranch,headSha,number,url,attempt` commands ran once each. The commands’ internal workflow-resolution HTTP requests were not traced, so no total wire-request count is invented.

Exactly six direct timing calls were `gh api repos/TerraScale/ts-dashboard/actions/runs/<run-id>/jobs?per_page=100`, with run IDs **39883426465, 39883088841, 39883426595, 39872842398, 39883426565, 39816512800**. All returned successfully and each contained at most 4 jobs, so no job pagination was required. Four timing calls remain reserved for the lead.

Other direct calls: 47 `gh api repos/TerraScale/ts-dashboard/actions/runs/<failed-run-id>/logs` downloads (one per frozen failed run), and two `gh api repos/TerraScale/ts-dashboard/issues/<99|95>/timeline?per_page=100` calls. Each timeline had fewer than 100 events, requiring no pagination. Total: **55 explicit `gh api` commands + 4 `gh run list` commands = 59 CLI commands**. No polling, run-list retries, extra timing/billing calls, secret retrieval, or GitHub mutations.

## Exhaustive failed-run ledger

Each run below has a redacted diagnostic log in external scratch. The measurement JSON also lists every failed method identity for each run, without parameter payloads or test credentials.

| Run | Workflow / event | Primary group | Status |
|---|---|---|---|
| [39883847464](https://github.com/TerraScale/ts-dashboard/actions/runs/39883847464) | CI / pull_request | stale-auth-console-assertion | Fixed on main in d5c4c341 (#84); CI AFTER not measured |
| [39883661586](https://github.com/TerraScale/ts-dashboard/actions/runs/39883661586) | CI / pull_request | stale-auth-console-assertion | Fixed on main in d5c4c341 (#84); CI AFTER not measured |
| [39883426565](https://github.com/TerraScale/ts-dashboard/actions/runs/39883426565) | Images (GHCR) / push | deployment-readiness | External runtime blocker; not a build/test flake |
| [39883426465](https://github.com/TerraScale/ts-dashboard/actions/runs/39883426465) | CI / push | stale-auth-console-assertion | Fixed on main in d5c4c341 (#84); CI AFTER not measured |
| [39883406697](https://github.com/TerraScale/ts-dashboard/actions/runs/39883406697) | CI / pull_request | stale-auth-console-assertion | Fixed on main in d5c4c341 (#84); CI AFTER not measured |
| [39882594788](https://github.com/TerraScale/ts-dashboard/actions/runs/39882594788) | CI / push | webhook-transport-race | Unresolved transport race candidate |
| [39876417752](https://github.com/TerraScale/ts-dashboard/actions/runs/39876417752) | CI / pull_request | webhook-transport-race | Unresolved transport race candidate |
| [39874625679](https://github.com/TerraScale/ts-dashboard/actions/runs/39874625679) | CI / pull_request | auth-barrier-and-webhook-race | Unresolved; two independent test failures |
| [39872842398](https://github.com/TerraScale/ts-dashboard/actions/runs/39872842398) | Slow / pull_request | hosted-auth-browser-fixture | Fixed later on PR #99 branch; outside audited main |
| [39872575340](https://github.com/TerraScale/ts-dashboard/actions/runs/39872575340) | CI / pull_request | auth-barrier | Unresolved race candidate |
| [39871778918](https://github.com/TerraScale/ts-dashboard/actions/runs/39871778918) | Slow / pull_request | hosted-auth-browser-fixture | Fixed later on PR #99 branch; outside audited main |
| [39870851316](https://github.com/TerraScale/ts-dashboard/actions/runs/39870851316) | CI / pull_request | hosted-auth-delegate-compile | Fixed later on PR #99 branch |
| [39801706784](https://github.com/TerraScale/ts-dashboard/actions/runs/39801706784) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39799075866](https://github.com/TerraScale/ts-dashboard/actions/runs/39799075866) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39797379885](https://github.com/TerraScale/ts-dashboard/actions/runs/39797379885) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39796017830](https://github.com/TerraScale/ts-dashboard/actions/runs/39796017830) | Slow / push | stale-sql-assertion | Fixed on main; historical branch failures |
| [39793443800](https://github.com/TerraScale/ts-dashboard/actions/runs/39793443800) | Slow / pull_request | dialog-animation-abort | Fixed on main (#85) |
| [39791253002](https://github.com/TerraScale/ts-dashboard/actions/runs/39791253002) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39789992109](https://github.com/TerraScale/ts-dashboard/actions/runs/39789992109) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39787207240](https://github.com/TerraScale/ts-dashboard/actions/runs/39787207240) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39787203914](https://github.com/TerraScale/ts-dashboard/actions/runs/39787203914) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39785871242](https://github.com/TerraScale/ts-dashboard/actions/runs/39785871242) | CI / pull_request | stripe-setup-formatting | Fixed on main (#93) |
| [39784654715](https://github.com/TerraScale/ts-dashboard/actions/runs/39784654715) | CI / pull_request | stripe-setup-formatting | Fixed on main (#93) |
| [39782315771](https://github.com/TerraScale/ts-dashboard/actions/runs/39782315771) | CI / pull_request | stripe-setup-formatting | Fixed on main (#93) |
| [39782315588](https://github.com/TerraScale/ts-dashboard/actions/runs/39782315588) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39782134594](https://github.com/TerraScale/ts-dashboard/actions/runs/39782134594) | CI / pull_request | stripe-setup-formatting | Fixed on main (#93) |
| [39781974043](https://github.com/TerraScale/ts-dashboard/actions/runs/39781974043) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39781355418](https://github.com/TerraScale/ts-dashboard/actions/runs/39781355418) | Slow / push | stale-sql-assertion | Fixed on main; historical branch failures |
| [39781355356](https://github.com/TerraScale/ts-dashboard/actions/runs/39781355356) | CI / push | stripe-setup-formatting | Fixed on main (#93) |
| [39781333301](https://github.com/TerraScale/ts-dashboard/actions/runs/39781333301) | CI / pull_request | stripe-setup-formatting | Fixed on main (#93) |
| [39781333271](https://github.com/TerraScale/ts-dashboard/actions/runs/39781333271) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39779399796](https://github.com/TerraScale/ts-dashboard/actions/runs/39779399796) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39776847443](https://github.com/TerraScale/ts-dashboard/actions/runs/39776847443) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39776770425](https://github.com/TerraScale/ts-dashboard/actions/runs/39776770425) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39680546539](https://github.com/TerraScale/ts-dashboard/actions/runs/39680546539) | CI / push | retired-registry-login | Fixed in audited source |
| [39680546438](https://github.com/TerraScale/ts-dashboard/actions/runs/39680546438) | Slow / push | stale-sql-assertion | Fixed on main; historical branch failures |
| [39678865117](https://github.com/TerraScale/ts-dashboard/actions/runs/39678865117) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39668609736](https://github.com/TerraScale/ts-dashboard/actions/runs/39668609736) | Slow / push | stale-sql-assertion | Fixed on main; historical branch failures |
| [39668609733](https://github.com/TerraScale/ts-dashboard/actions/runs/39668609733) | CI / push | retired-registry-login | Fixed in audited source |
| [39667913923](https://github.com/TerraScale/ts-dashboard/actions/runs/39667913923) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39667613380](https://github.com/TerraScale/ts-dashboard/actions/runs/39667613380) | CI / push | retired-registry-login | Fixed in audited source |
| [39667613234](https://github.com/TerraScale/ts-dashboard/actions/runs/39667613234) | Slow / push | stale-sql-assertion | Fixed on main; historical branch failures |
| [39666555668](https://github.com/TerraScale/ts-dashboard/actions/runs/39666555668) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39606620635](https://github.com/TerraScale/ts-dashboard/actions/runs/39606620635) | Slow / workflow_dispatch | stale-sql-assertion | Fixed on main; historical branch failures |
| [39605663903](https://github.com/TerraScale/ts-dashboard/actions/runs/39605663903) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39603739320](https://github.com/TerraScale/ts-dashboard/actions/runs/39603739320) | Slow / pull_request | stale-sql-assertion | Fixed on main; historical branch failures |
| [39603691501](https://github.com/TerraScale/ts-dashboard/actions/runs/39603691501) | CI / push | retired-registry-login | Fixed in audited source |

Next: use the four reserved timing calls for AFTER, remove #112 quarantine only after five dev-host repeats of a root-cause fix, and leave gru runtime restoration/live proof to COS as instructed.

## Implementation decisions after the audit

The CI lane keeps the required `test` name specified in the task; live branch rules were empty, so no other required name is removed. It elects main-only Slow coverage instead of the optional historical `full-ci` PR path to reduce org runner contention; all browser and policy tests remain in Slow. Both unresolved intermittent cases are expiring quarantine #112, owner MarioGK, deadline 2026-10-24T01:48:00Z; this is not a claimed root-cause fix. COS authorized merging on green during gru restoration and owns live verification if the single post-merge probe still fails.
