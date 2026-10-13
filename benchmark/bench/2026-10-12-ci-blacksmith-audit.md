# TerraScale CI / Blacksmith audit — 2026-10-12

Status: original-Docker baseline and cache-split Docker builds verified on two hosted attempts each. The split did not reuse preparation on its repeat; SDK Git-metadata exclusion is now under verification. No speedup claimed.

## Scope and ownership

The assigned worktree is `perf/ci-blacksmith-audit`, based on main `c691f94716c5d6adb41146baa6436304d1d06c88`. The user relayed Delta's ownership handoff at 12:56:31, message position 1534: this task owns independent Docker and test/build caching changes. Strict owns release integration, `ci.yml` result classification and overlapping Makefile hunks (PRs [105](https://github.com/TerraScale/terrascale/pull/105), [108](https://github.com/TerraScale/terrascale/pull/108)); benchmark PR [79](https://github.com/TerraScale/terrascale/pull/79) also touches Makefile. Those hunks are untouched. Delta retains deployment, previews, cleanup and recovery, including parked `b84cae52` and reset scripts. T1 and R10a application/test files are untouched. Dashboard replacement remains held.

Production and preview creation stay held by [122](https://github.com/TerraScale/terrascale/pull/122) and [123](https://github.com/TerraScale/terrascale/pull/123). Both `if: false &&` expressions remain byte-for-byte intact. No deployment, preview, resource cleanup, credential work, access changes, runner upgrades or new paid services were performed.

## Method and limits

Inventory: all 13 repositories returned by `gh repo list TerraScale --limit 100`, with default-branch SHAs, complete Git trees (including alternate CI locations), current workflow files and applicable root/area instructions. There are 12 current workflows in eight repositories; every declared runner is Blacksmith. Five repositories have no current GitHub Actions or alternate workflow definitions. Archived deployment sources in ts-resources are historical material, not active jobs.

Timing sample: newest 40 TerraScale repository runs, expanded with the newest 20 main CI and newest 20 cleanup runs; all available runs for other repositories (each fewer than 40), deduplicated by run ID. Every available rerun attempt is included. Total: 137 distinct runs, 143 attempts, 757 job records, created 2026-10-11 21:20:18 UTC through 2026-10-12 12:52:16 UTC. Main hold runs were refreshed after completion at 13:06 UTC; other records retain their collection snapshot. This is a recent sample, not lifetime failure frequency.

Job queue = `started_at - created_at` (ready-job provisioning/concurrency delay; excludes time waiting on upstream jobs). Runtime = `completed_at - started_at`. Missing/reversed timestamps, skipped jobs and cancellations do not enter runtime quantiles. Runtime medians/p90 below use successful jobs only unless explicitly labelled failed. p90 uses nearest rank. Attempts with different inputs or earlier workflow definitions are descriptive history, not an optimization comparison. Cache hits and in-container phases come from allowlisted numeric BuildKit log records for the pinned main baseline; raw logs and credential stores were not saved in the report.

[Compact API evidence and baseline log timings](2026-10-12-ci-blacksmith-evidence.json) preserve all sampled run SHAs/URLs and per-job/per-step summaries. Full API job/step timestamps are also retained locally under `/tmp/terrascale-ci-audit-4dcf500a/`. Suite-level CPU/memory telemetry is available for the pinned TerraBase Fast run (below); full-run CPU/memory utilization, actual cache storage, cache-hit rate across all runs and invoice billing are unavailable. Reserved runner occupancy is not CPU utilization. No deployed-health check was performed. Branch-protection and ruleset APIs returned HTTP 403 with GitHub's plan restriction; required review configuration is unverified.

## Repository / workflow inventory

| Repository at exact default-branch SHA | Workflow and all job families | Deployment / hold |
|---|---|---|
| [ku `54c0b47336662a93a573158f51dce88e5d45756c`](https://github.com/TerraScale/ku/commit/54c0b47336662a93a573158f51dce88e5d45756c) | `ci.yml`: guard 2; check/test, three e2e shards, build/release 4; report-release-failure 2. `slow.yml`: backend/frontend/live 4, report 2. `security.yml`: go-security 4, frontend-security/report 2. `image-security.yml`: 12 image matrix legs 4, report 2. | Harbor image → external Alpha homelab-deployer → release. Slow and security reporting stay independent. Security workflows exist but have zero runs in available history. |
| [terrascale `c691f94716c5d6adb41146baa6436304d1d06c88`](https://github.com/TerraScale/terrascale/commit/c691f94716c5d6adb41146baa6436304d1d06c88) | `ci.yml`: changes 2, libs/auth 4, terrabase 8, test 2, slow 4, preview/deploy 8, e2e 4 vCPU. `previews.yml`: cleanup 2. | Fly production and preview creation held. Cleanup remains defined and Delta-owned; no cleanup was invoked. E2E follows deploy and is skipped by the hold. |
| [ts-cos `20674fe9e2fb45a83434447192862f2fa30bf56c`](https://github.com/TerraScale/ts-cos/commit/20674fe9e2fb45a83434447192862f2fa30bf56c) | No current workflow, no historical Actions runs. | Manual install/service and deployment code exist; no hosted job to measure. |
| [ts-dashboard `f0ef8be4edcef111f3d9f58c13151f90b9ccafd9`](https://github.com/TerraScale/ts-dashboard/commit/f0ef8be4edcef111f3d9f58c13151f90b9ccafd9) | No current workflow, no historical Actions runs. | Go checkout; replacement held. Deployment documentation/scripts exist, but no hosted job to measure. |
| [ts-docs `ce50c862f08f8e55d34e27ff7453efdcd9e6bc9e`](https://github.com/TerraScale/ts-docs/commit/ce50c862f08f8e55d34e27ff7453efdcd9e6bc9e) | `ci.yml`: ci/deploy 2 vCPU. | Current main uses VitePress build/test then Wrangler deployment. Open PR 7 proposes Pages/artifact changes; avoid duplicate work. |
| [ts-dotnet `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`](https://github.com/TerraScale/ts-dotnet/commit/bcd20603f11f3aa54dc54cec6e410b4fe9e24151) | `ci.yml`: test 4 vCPU; both net10/net11. | No deployment job. |
| [ts-go `93c368d39bfa2580d76907d65537cee6586d86fc`](https://github.com/TerraScale/ts-go/commit/93c368d39bfa2580d76907d65537cee6586d86fc) | `ci.yml`: test 2 vCPU, formatting/vet/race tests. | No deployment job. |
| [ts-js `8c8ded2c4d6bfc7fd1277329d60c92818cf3d524`](https://github.com/TerraScale/ts-js/commit/8c8ded2c4d6bfc7fd1277329d60c92818cf3d524) | No current workflow, no historical Actions runs. | Publication/deployment metadata exists; no hosted job to measure. |
| [ts-landing `3e3581daee241902db0a6949c05bb90f2afb4d7a`](https://github.com/TerraScale/ts-landing/commit/3e3581daee241902db0a6949c05bb90f2afb4d7a) | `ci.yml`: ci/deploy 2 vCPU. | Cloudflare Pages after CI on main. Read only. |
| [ts-resources `f3b54ea3dcc1d81cb13b5fa35645fb234286fc1b`](https://github.com/TerraScale/ts-resources/commit/f3b54ea3dcc1d81cb13b5fa35645fb234286fc1b) | No current workflow, no historical Actions runs. | Archived platform/deployment source; no active Blacksmith evidence. |
| [ts-reviewer `edae11a57e9a9683a903c105cc6a17bda77ef1c2`](https://github.com/TerraScale/ts-reviewer/commit/edae11a57e9a9683a903c105cc6a17bda77ef1c2) | `ci.yml`: checks, image, deployment-gate, deploy, all 4 vCPU. | GHCR image and immutable OCI artifact → environment-gated Alpha deployment with existing identity, health/rollback and current-main checks. Latest deploy failed at identity preparation; application deployment did not run. |
| [ts-rust `8a5d983684a5b26f2992a9e7ee07969a7bc2c12a`](https://github.com/TerraScale/ts-rust/commit/8a5d983684a5b26f2992a9e7ee07969a7bc2c12a) | No current workflow, no historical Actions runs. | No hosted deployment evidence. |
| [ts-status `bd0611945057dd86c1bad77cb94c41db5d7882f0`](https://github.com/TerraScale/ts-status/commit/bd0611945057dd86c1bad77cb94c41db5d7882f0) | `ci.yml`: ci/deploy 2 vCPU. | Cloudflare Workers, D1 bootstrap and vantage publication; deployment after CI on main. Read only. |

## Hosted job baseline

Outcome counts include every sampled attempt, including earlier workflow versions. S = success; F = failure; C = cancelled; K = skipped; running = incomplete snapshot. These are job outcomes, not test-case outcomes.

| Repository / workflow / job | Success n | Median / p90 seconds | Queue median seconds | Outcomes |
|---|---:|---:|---:|---|
| ku / CI / build | 9 | 103.0 / 110.0 | 7.0 | S 9 |
| ku / CI / check | 8 | 46.0 / 51.0 | 7.0 | F 1, S 8 |
| ku / CI / e2e shard 1 | 9 | 55.0 / 60.0 | 7.0 | S 9 |
| ku / CI / e2e shard 2 | 9 | 94.0 / 105.0 | 7.0 | S 9 |
| ku / CI / e2e shard 3 | 9 | 37.0 / 38.0 | 7.0 | S 9 |
| ku / CI / guard | 9 | 7.0 / 7.0 | 7.0 | S 9 |
| ku / CI / release | 4 | 139.5 / 145.0 | 7.0 | F 5, S 4 |
| ku / CI / report-release-failure | 5 | 9.0 / 10.0 | 7.0 | S 5, K 4 |
| ku / CI / test | 5 | 86.0 / 95.0 | 7.0 | F 4, S 5 |
| ku / Slow Tests / backend | 9 | 36.0 / 80.0 | 7.0 | S 9 |
| ku / Slow Tests / frontend | 2 | 94.5 / 97.0 | 8.0 | F 7, S 2 |
| ku / Slow Tests / live | 0 | no successful sample | 8.0 | F 9 |
| ku / Slow Tests / report | 9 | 9.0 / 10.0 | 7.0 | S 9 |
| terrascale / CI / auth | 47 | 210.0 / 224.0 | 7.0 | S 47, C 4, K 7 |
| terrascale / CI / changes | 57 | 6.0 / 7.0 | 7.0 | S 57, C 1 |
| terrascale / CI / deploy | 0 | no successful sample | 28.0 | C 5, F 13, K 40 |
| terrascale / CI / e2e | 0 | no successful sample | unavailable | K 56, C 2 |
| terrascale / CI / libs | 34 | 49.0 / 53.0 | 7.0 | S 34, K 23, C 1 |
| terrascale / CI / preview | 26 | 162.0 / 180.0 | 8.0 | K 22, S 26, F 9, C 1 |
| terrascale / CI / slow | 0 | no successful sample | 7.0 | F 11, C 4, K 43 |
| terrascale / CI / terrabase | 48 | 141.5 / 155.0 | 8.0 | S 48, C 2, F 8 |
| terrascale / CI / test | 43 | 9.0 / 10.0 | 7.0 | S 43, C 4, F 11 |
| terrascale / Previews / cleanup | 20 | 11.0 / 12.0 | 7.5 | S 20 |
| ts-docs / CI / ci | 13 | 29.0 / 29.0 | 7.0 | S 13 |
| ts-docs / CI / deploy | 4 | 36.0 / 38.0 | 7.0 | K 6, F 3, S 4 |
| ts-dotnet / CI / test | 4 | 40.5 / 46.0 | 7.5 | S 4 |
| ts-go / CI / test | 6 | 25.0 / 72.0 | 7.5 | S 6 |
| ts-landing / CI / ci | 3 | 37.0 / 39.0 | 7.5 | S 3 |
| ts-landing / CI / deploy | 1 | 25.0 / 25.0 | 8.0 | K 1, F 1, S 1 |
| ts-reviewer / ts-reviewer CI / Check and build ts-reviewer | 6 | 67.0 / 81.0 | 7.0 | F 1, S 6 |
| ts-reviewer / ts-reviewer CI / Deploy Alpha with health check and rollback | 0 | no successful sample | 10.0 | K 6, F 1 |
| ts-reviewer / ts-reviewer CI / Publish the exact checked image | 1 | 72.0 / 72.0 | 7.0 | K 6, S 1 |
| ts-reviewer / ts-reviewer CI / Verify Alpha environment setup | 1 | 4.0 / 4.0 | 8.0 | K 6, S 1 |
| ts-status / CI / ci | 14 | 25.0 / 28.0 | 7.0 | S 14 |
| ts-status / CI / deploy | 6 | 37.0 / 39.0 | 7.0 | K 7, F 1, S 6 |

TerraScale sampled reruns: 3/75 run IDs; ts-docs 1/12, ts-status 1/13, ts-landing 1/2; the other observed repositories have none. Reruns are not automatically flakes. TerraScale's 13 completed deploy attempts all failed (median 264 s, p90 438 s); five others cancelled. Three stopped at the Fast gate; ten reached and failed TerraBase rollout. E2E has no successful sampled execution. Preview had nine failed attempts before its hold; completed failed previews spent time building images before machine startup failed. Slow has 11 failed attempts (median 213 s, p90 779 s), four cancellations and 43 skips. The overall workflow can say success while Slow fails. Existing failure issues [68](https://github.com/TerraScale/terrascale/issues/68), [102](https://github.com/TerraScale/terrascale/issues/102), [103](https://github.com/TerraScale/terrascale/issues/103), [104](https://github.com/TerraScale/terrascale/issues/104) remain owned by correctness work; none were closed or masked.

## Pinned main: phase timings and actual Fast execution

[Run 38251442518](https://github.com/TerraScale/terrascale/actions/runs/38251442518), commit [`c691f94716c5d6adb41146baa6436304d1d06c88`](https://github.com/TerraScale/terrascale/commit/c691f94716c5d6adb41146baa6436304d1d06c88), required `test` completed 254 s after run creation. Deploy, preview and E2E skipped; Slow failed after 779 s.

| Area / exact job | Runner | Job / Docker action seconds | Restore / whitespace seconds | Compilation / checks | Actual tests |
|---|---:|---:|---:|---|---|
| [auth](https://github.com/TerraScale/terrascale/actions/runs/38251442518/job/114818598598) | 4 vCPU | 216 / 203 | ~3.3 / ~9.3 | audit ~4.7 s; auth build 29.83 s; TerraBase build 21.19 s; Fast build 0.47 s; preparation through 72.45 s | 577 passed, 0 skipped, 126.743 s |
| [terrabase](https://github.com/TerraScale/terrascale/actions/runs/38251442518/job/114818597750) | 8 vCPU | 144 / 131 | ~2.7 / ~6.6 | area build 56.76 s; analyzer fixture and Fast incremental build ~1.3 s; Fast starts at 67.76 s | 1,418 passed, 0 skipped, 60.111 s |
| [libs](https://github.com/TerraScale/terrascale/actions/runs/38251442518/job/114818597705) | 4 vCPU | 56 / 44 | ~3.5 / ~7.9 | area build 8.63 s plus six style checks | Endpoints 88 passed in 0.820 s; Logging 46 passed in 0.260 s; 0 skipped |

Two checkouts plus builder setup take 5–6 s per area. SDK/tool installation layers (apt, Chromium, Redocly) are cached in all three baseline jobs; context copies take ~0.1 s. No measured reason to add a runner upgrade or another host. TerraBase Fast suite diagnostics sampled 61 points over 59.682 s: its test process averaged 3.88 CPU cores with eight visible, peak process working set 3.54 GiB, peak cgroup memory 8.65 GiB, 4.17 s GC pauses and zero observed OOM-kill counter increase. This covers the test process/window, not compiler work or every runner process; it does not prove a safe smaller runner. Each area restores the full solution and formats its whitespace; the Makefile shares prerequisites within a single invocation, so its many target dependencies are not evidence of repeated restores within that invocation.

## Other phase / duplicate-work observations

| Repository / exact example run | Measured phases | Implication |
|---|---|---|
| [ts-dotnet 38195553386](https://github.com/TerraScale/ts-dotnet/actions/runs/38195553386), `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` | SDK setup 17 s; build 14 s; net10/net11 tests 4/3 s; checkout 1 s | Small pipeline; SDK setup dominates. No upgrade or framework test removal proposed. |
| [ts-go 38188256755](https://github.com/TerraScale/ts-go/actions/runs/38188256755), `93c368d39bfa2580d76907d65537cee6586d86fc` | setup 10 s; vet 2 s; race tests 5 s; checkout 1 s | Small, already cached toolchain; keep race coverage. |
| [ts-reviewer 38251711558](https://github.com/TerraScale/ts-reviewer/actions/runs/38251711558), `edae11a57e9a9683a903c105cc6a17bda77ef1c2` | tests/fixtures 33 s; audit 5 s; publish 17 s; tooling build 6 s; upload 8 s; image tooling 12 s, container publish 11 s, OCI export 12 s, artifact upload 4 s; deploy downloads 7+6 s, identity preparation fails in 1 s | Tooling is rebuilt in checks and image; no successful deployment timing. Trust repair is outside scope. |
| [ts-docs 38185016494](https://github.com/TerraScale/ts-docs/actions/runs/38185016494), `ce50c862f08f8e55d34e27ff7453efdcd9e6bc9e` | Node setup 9/10 s in ci/deploy; install 2/2 s; CI build 9 s; deploy 19 s includes another build | Current main builds twice; PR 7 already proposes artifact reuse. Do not duplicate it. |
| [ts-status 38192103773](https://github.com/TerraScale/ts-status/actions/runs/38192103773), `bd0611945057dd86c1bad77cb94c41db5d7882f0` | setup 6/6 s; install 3/3 s; CI build 1 s; deploy 20 s includes build, D1 bootstrap and vantages | Duplicate build is only 1 s; low-value artifact plumbing. Preserve all deployment steps. |
| [ts-landing 38181293343](https://github.com/TerraScale/ts-landing/actions/runs/38181293343), `3e3581daee241902db0a6949c05bb90f2afb4d7a` | setup 9/9 s; install 3/2 s; CI check 11 s includes build; deploy 8 s includes another build | Reuse might help, but build portion of deploy is not separately measured. |
| [ku 38195243226](https://github.com/TerraScale/ku/actions/runs/38195243226), `a9cee5e4a7a8425e8985ac76396f6b7e9dc22801` | browser shard build 14/13/11 s; browser test 31/77/12 s; main image 91 s; release image rebuild 93 s; deploy-status wait 31 s | Shard imbalance is larger than checkout overhead. Release image embeds a new version/commit; do not replace it by retagging an older image. |

## Ranked improvements

| Rank / change | Measured opportunity, not promised gain | Effort / risk | Decision |
|---|---|---|---|
| 1. Cache preparation separately from fresh Fast execution | Baseline TerraBase preparation ~67 s; auth format/build/OpenAPI ~45 s before its TerraBase/test work; libs preparation dominates its 40 s RUN. Repeat attempts can reuse those exact inputs. | ~1 hour including hosted comparison; low code risk, moderate validation risk; split invocations add restore/incremental-build overhead on misses. | Docker split passed twice, but repeated preparation missed cache. SDK metadata copy is being excluded and verified before another comparison. |
| 2. Measure auth concurrency on the same existing runner | 126.743 s for 577 Fast cases, the critical path. CPU/RAM measurements unavailable; cannot infer a safe higher cap. | 30–60 min controlled comparisons; medium resource/isolation risk. | Deferred; Makefile ownership/reconciliation required for an exact proposed hunk. No new cap or tuning option added. |
| 3. Balance ku browser shards without reducing coverage | Successful-job medians 55/94/37 s; browser-test medians 29/68/10 s. | 1–2 hours to inspect per-test timings and validate equal membership; medium risk. | Recommendation only; outside accepted file ownership. |
| 4. Reuse existing build cache for ku image/release compilation | Main image median 91 s; release image rebuild median 100 s. Different embedded release identity must still be rebuilt. | 1–2 hours; medium cache/provenance risk. | Recommendation only; no new paid cache service authorized. Measure existing platform cache first. |
| 5. Reuse checked static outputs where already being developed | ts-docs measured CI build ~8–9 s and another build during deployment; PR 7 already overlaps. Status duplicate build only 1 s. | 30–60 min; low benefit outside docs, artifact identity must remain exact. | Defer to ts-docs PR 7; skip status artifact plumbing. |

No Makefile or result-classification diff is proposed in this change. Production failure recovery and preview resource deletion remain Delta/Strict tasks, not performance shortcuts.

## Implemented cache boundary and verification

`check-libs`, `check-auth`, `check-terrabase` retain whitespace/build/analyzer/OpenAPI/style/handoff gates with the same Release settings and locked restore. `test-*` inherits preparation, runs the existing Make Fast target, and touches `/passed` only after success. Auth's dependency audit moves to its fresh test stage. The existing per-job Blacksmith cache keys are unchanged; every area build sets `no-cache-filters: test-${{ github.job }}` so a cached `/passed` cannot replace current-attempt tests. Slow's existing `no-cache-filters: slow-run` and reporting stay untouched. Tool-installation and publish/image stages are unchanged. The shared test base now excludes SDK `.git` metadata from its named-context copy; all tracked SDK source remains pinned and included. Slow stage instructions/reporting/coverage are unchanged, though Slow inherits the same SDK metadata exclusion. Published-image SDK copy remains unchanged.

Local static scope assertions passed: only the two-line cache option was added to the workflow, both holds and all result/deployment sections are unchanged, all original check targets remain, and Slow/publish stages match the original. `git diff --check` passed. Actionlint 1.7.12 YAML/expression checks passed with declared Blacksmith labels. Shellcheck integration was unavailable (mise shim has no configured version); no shell run commands were changed. Docker is absent locally. The split built successfully on two hosted attempts, with fresh Fast output each time. The subsequent SDK copy exclusion still needs hosted parsing/build/cache verification.

`nice -n 19 make check CONFIGURATION=Release` passed locally using pinned ts-dotnet `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` via `TsDotnetRoot`: Endpoints 88, Logging 46, Auth Fast 577 and TerraBase Fast 1,418 passed, zero failed/skipped; formatting/build/analyzers/security/OpenAPI/benchmark gates passed. Local Auth/TerraBase suite times were 265.613/89.333 s and are not comparable to hosted timings. Hosted comparison: draft [PR 125](https://github.com/TerraScale/terrascale/pull/125), base `main`, baseline head `21aa4592ee2aeab75836d271780c300261f6d477`, [run 38254848939](https://github.com/TerraScale/terrascale/actions/runs/38254848939), adds only fresh-stage execution to the original Dockerfile; a subsequent commit adds the split. Compare the same service/test/SDK inputs, runners, suite counts and fresh stage execution, including a repeat attempt to confirm preparation hits. Do not compare a skipped or fully cached test layer with executed tests. No live/preview/Slow jobs run on these PR events.

## Comparable original-Docker baseline

Both attempts use head [`21aa4592ee2aeab75836d271780c300261f6d477`](https://github.com/TerraScale/terrascale/commit/21aa4592ee2aeab75836d271780c300261f6d477), the original Docker layout and `no-cache-filters` forcing current-attempt Fast execution. Exact service/test/SDK inputs, runner sizes and counts are unchanged. Each attempt passed all 2,129 tests with zero failed/skipped; deploy, preview, E2E and Slow skipped.

| Area | [Attempt 1](https://github.com/TerraScale/terrascale/actions/runs/38254848939/attempts/1) job / Docker action | [Attempt 2](https://github.com/TerraScale/terrascale/actions/runs/38254848939/attempts/2) job / Docker action | Suite duration, attempt 1 / 2 | Passed |
|---|---:|---:|---:|---:|
| Auth, 4 vCPU | 246 / 232 s | 239 / 225 s | 136.438 / 137.208 s | 577 |
| TerraBase, 8 vCPU | 153 / 138 s | 137 / 124 s | 57.069 / 50.171 s | 1,418 |
| Libs, 4 vCPU | 64 / 51 s | 67 / 51 s | Endpoints 0.794 / 0.937 s; Logging 0.259 / 0.241 s | 88 + 46 |

All four measured attempts (including the split below) used .NET SDK image `mcr.microsoft.com/dotnet/sdk:11.0@sha256:d0a39ca4acefe992f26030f8fb8c60e1b14ac088822cb3fdfbe536ecf843a675`. These two baseline attempts establish variability, not a speedup. The split must pass a first build and an unchanged-SHA rerun with preparation cache hits and fresh test output before an improvement is claimed. The audit documents are also build-context inputs; a subsequent evidence-only update can invalidate preparation, so it must receive current-head CI verification.

## Cache split: first build and repeat without reuse

Head [`f5f3bfb7091317292a730dfa2f3c084db951067b`](https://github.com/TerraScale/terrascale/commit/f5f3bfb7091317292a730dfa2f3c084db951067b), [run 38256624986](https://github.com/TerraScale/terrascale/actions/runs/38256624986), used the split and unchanged service/test/SDK source, runner sizes, test caps and suite membership. Both attempts passed all 2,129 tests, zero failed/skipped. Deploy, preview, E2E and Slow skipped. Docker stages built correctly; each test RUN executed rather than using a cached result.

| Area | [Attempt 1](https://github.com/TerraScale/terrascale/actions/runs/38256624986/attempts/1) job / Docker action | [Attempt 2](https://github.com/TerraScale/terrascale/actions/runs/38256624986/attempts/2) job / Docker action | Preparation RUN, attempt 1 / 2 | Suite duration, attempt 1 / 2 |
|---|---:|---:|---:|---:|
| Auth | 238 / 225 s | 255 / 240 s | 55.0 / 57.8 s, both uncached | 133.910 / 143.522 s |
| TerraBase | 151 / 137 s | 156 / 142 s | 80.2 / 77.9 s, both uncached | 50.735 / 58.719 s |
| Libs | 62 / 50 s | 71 / 58 s | 40.7 / 47.2 s, both uncached | Endpoints 1.247 / 1.289 s; Logging 0.235 / 0.242 s |

This unchanged-SHA repeat did **not** validate warm preparation reuse. SDK `COPY --from=ts-dotnet . /src/ts-dotnet` also missed cache in every area on both attempts, while apt/browser/tool layers hit. The named SDK context includes checkout-specific `.git` metadata and has no `.dockerignore`. The next minimal Docker change adds `--exclude=.git` to that copy in the shared test base; its published-image copy stays unchanged. This preserves all 117 tracked files at the pinned revision, including embedded contracts. SDK Git-derived informational-version metadata in test binaries can differ; no shipped-image or SDK package metadata is changed. Local source-equivalence assertions passed for every tracked file and executable flag. `nice -n 19 make check CONFIGURATION=Release TsDotnetRoot=/tmp/terrascale-ci-audit-4dcf500a/ts-dotnet-ci-source` passed with the SDK source export excluding Git metadata: all 2,129 tests passed, zero failed/skipped, plus required formatting/build/analyzer/security/OpenAPI/benchmark checks. Hosted cold/repeat evidence is pending for the Docker copy exclusion.

There is no demonstrated speedup from these four attempts. The first split build's extra Make invocation repeats locked restore and incremental compilation; observed differences also include test-duration variation. Further hosted work must show both preparation hits and current-attempt test output, or the unhelpful split should be removed while preserving fresh Fast execution.

## Documented cost implications

[Blacksmith pricing](https://www.blacksmith.sh/pricing), fetched 2026-10-12: Ubuntu x64 2/4/8/16/32 vCPU list rates $0.004/$0.008/$0.016/$0.032/$0.064 per minute; advertised free allowance 3,000 minutes/month; Docker layer/sticky-disk storage $0.50/GB/month. These are vendor list terms, not the organization's bill. The billing-document URL returned HTTP 403. Actual rounding, free-credit consumption, agreements and stored GB are unverified. No invoice savings claimed.

Applying list rates proportionally to observed runtime, the pinned main Fast path reserves 37.77 vCPU-minutes: approximately $0.0755 before billing rules/credits. Its failed non-blocking Slow job adds 51.93 vCPU-minutes, approximately $0.1039. Reduced build work can lower compute; extra intermediate cache layers can affect existing storage charges, which were not observable. Baseline occupancy does not establish actual CPU utilization.

## Remaining gates

Original-Docker baseline [PR run 38254848939, attempt 1](https://github.com/TerraScale/terrascale/actions/runs/38254848939) passed: auth 246 s, TerraBase 153 s, libs 64 s; actual tests 577/1,418/88+46 passed, zero failed/skipped. Actual merge `f61c1b80ad8d295756786df4d804597f0d8f70a6` combines main `c691f947` with head `21aa4592`; both holds verified there. A second baseline attempt passed: auth 239 s, TerraBase 137 s, libs 67 s; identical test counts, zero failed/skipped, and provisioning/Slow skipped. The initial split built and tested twice but did not reuse preparation; SDK metadata exclusion and comparable warm-cache timings remain pending. Required reviews/check policy must be confirmed by Strict/user because GitHub policy APIs returned 403. A focused performance PR must remain separate from the frozen release integration SHA; neither deployment hold is resolved by this audit. No merge or auto-merge is authorized while the applicable holds/release ownership checks remain unresolved. The user's later coordination requires actual base `main` and verification of both gates in the exact head plus actual base merge before every push. Do not close this PR: the existing closed-PR cleanup remains destructive and Delta-owned.
