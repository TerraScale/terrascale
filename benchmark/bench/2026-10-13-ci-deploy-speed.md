# CI and deployment speed — 2026-10-13 baseline

Status: measured before changes. After results and merge → all-three-regions deployment are pending T0's main fix and T2's deployment re-enable/rebuild. No speedup or all-green result is claimed.

## Baseline method

Twenty latest `CI` runs on main, created 2026-10-12 22:08:11Z through 2026-10-13 15:51:43Z, collected with `gh run list` and `gh api .../actions/runs/<id>/jobs?per_page=100` at approximately 17:15Z. All used the `alpha` runner label through `CI_RUNNER`; these are not Blacksmith measurements. Job runtime = completed minus started; queue = started minus created, excluding dependency waits. Median/p90 runtime and queue include successful jobs only; p90 is nearest rank. Inputs differ across commits, so this sample describes current performance and reliability, not a controlled optimization experiment.

[API timestamps, actual job conclusions, selected Docker step timestamps and allowlisted BuildKit phases](2026-10-13-ci-deploy-speed-baseline.json) preserve evidence without raw logs or credential values.

| Job | Success / failure / skipped | Runtime median / p90 (s) | Queue median / p90 (s) |
|---|---:|---:|---:|
| changes | 20 / 0 / 0 | 7.5 / 8 | 48.5 / 1029 |
| libs | 20 / 0 / 0 | 177.5 / 202 | 42.5 / 1701 |
| auth | 17 / 3 / 0 | 480 / 572 | 201 / 1808 |
| terrabase | 11 / 9 / 0 | 521 / 664 | 3 / 1439 |
| test | 11 / 9 / 0 | 14 / 16 | 132 / 1736 |
| slow | 0 / 11 / 9 | — | — |

The eleven successful `test` checks took 422–3,677 seconds (7m02s–61m17s) from workflow creation, median 2,428 seconds (40m28s). Every one had a failed Slow job. An overall Actions conclusion of “success” does not mean every job passed: Slow is deliberately non-blocking.

## Exact recent runs

| Observation | Measurement | Actual result |
|---|---|---|
| [Latest main 29e9da2f, run 38425656704](https://github.com/TerraScale/terrascale/actions/runs/38425656704) | Merge 15:51:38Z → workflow completed 16:00:37Z: 539 s; libs/auth/TerraBase jobs 169/155/488 s | Libs passed; auth compile failed; TerraBase Fast failed with missing `_tb_feed_floor`; `test` failed; Slow/deploy/e2e skipped |
| [ab66b18e, run 38393279132](https://github.com/TerraScale/terrascale/actions/runs/38393279132) | Workflow → `test` 3,429 s; libs/auth/TerraBase jobs 184/386/548 s; Slow job 228 s | Fast passed; auth Slow 23/25 passed, 2 failed, 0 skipped; TerraBase Slow never executed |
| Same successful-Fast run, warm Docker phases | Auth build cached, auth fresh test RUN 358 s; TerraBase build 220.1 s, fresh test RUN 300.3 s; Slow RUN 185 s, auth suite 36.827 s | Restored layers cached; auth Slow failure stops sequential `make test-slow` before TerraBase |
| Merge → TerraBase + auth healthy in gru/sjc/fra | Not measurable during deployment hold | Deploy/e2e skipped throughout sample; T2 deleted old machines and has not rebuilt |

Auth Slow failures in that run: `Concurrent_rotations_on_two_replicas_lose_no_key` and `Concurrent_first_use_on_two_replicas_publishes_every_key_and_converges_on_one_signer`, both HTTP 404 from test-only key endpoints. Existing reports: [#200](https://github.com/TerraScale/terrascale/issues/200), [#199](https://github.com/TerraScale/terrascale/issues/199). Local reproduction past T0's fixes confirmed missing project records in the fixture; seeding them makes Auth Slow pass 25/25. A separate code branch also fixes the SQL cancellation reader cleanup in [#201](https://github.com/TerraScale/terrascale/issues/201), with both native and original Slow regressions passing. T0 #206 is now on main. Current-source full Docker test passed (816 s; Auth664/664, TerraBase1563/1563, libs134/134, native7/7). Local `make check` failed only T0's independently reproduced shutdown race; publication waits for that required gate. Full Auth Slow passes25/25; real Tigris backup passes1/1 against an isolated bucket. Final full TerraBase Slow is running. No assertion, retry or parallelism was weakened; two boundary-cancellation cases strengthen coverage.

## Prepared decisions, unpushed

1. Restore fixed Blacksmith labels already used successfully in repository history: 2 vCPU bookkeeping/cleanup, 4 vCPU libs/auth/preview/deploy/e2e, 8 vCPU TerraBase. Remove `CI_RUNNER` and remote-alpha builder selection. Historical 2026-10-12 audit measured auth 239–246 s on 4 vCPU and TerraBase 137–153 s on 8 vCPU; different source/test inputs mean no current speedup claim. Validate current sizes before further upgrades.
2. Start Slow after changes so failed Fast cannot suppress evidence. Run auth and TerraBase Slow independently in parallel with `fail-fast: false`, separate cache scopes, and forced fresh test RUNs. This fixes demonstrated missing coverage without weakening either suite; keep failures non-blocking and visible, report every failed test, and report builds that produce no result.
3. Exclude `benchmark/bench/` from Docker build inputs. Source searches found no build/test dependency on reports; evidence-only edits should not invalidate compilation layers. Keep benchmark code and contracts included.
4. Keep the existing one-image-per-app, gru → sjc → fra rollout and live/backup gates. T2 adds UTC regional start/end timestamps. No machine deletion/restart by T6, and no removal of health, backup, storage-format or exact-tree Fast gates.
5. Preserve T2's preview hold until live validation. Local preparation maps auth secrets, repairs independently missing keys, provisions the first signing key only on new preview creation, and requires both services' readiness before publishing the URL. Previews wait for required Fast success to avoid deploying failed PRs and paying for their images/machines. The approved shared 1 CPU / 1 GB shape, idle stop and 1 GB volume remain. Script fixtures, ShellCheck and actionlint pass; no live preview is claimed. Current warm restore hits do not justify a new restore/cache architecture yet.

## Remaining acceptance

Local preparation checks passed: actionlint with the installed ShellCheck 0.11.0 and explicit Blacksmith label declarations; 13 fixtures using dorny's actual v3 matcher; five parallel fixtures exercising the actual inline Slow reporter (pass, existing/new issues with redaction, eleven failures, missing build output, branch-only reporting); deploy, volume-reset, storage-format and e2e-summary self-checks; `git diff --check`. The isolated current-source Docker `test` target passed, and actual local Slow results are recorded above. Full `make check` passed all static/Auth/libs/native gates and failed only the T0-owned Fast shutdown race. Current-head hosted checks, live e2e, preview lifecycle and after deployment timings remain pending. No push or PR yet; both prerequisite threads are still active. Local Docker results validate the candidate but do not measure Blacksmith performance.

T0 main `test` SUCCESS → T2 deploy re-enable/rebuild merged → measure their first valid rollout before T6 workflow changes. Rebase preparation on their merged main, reproduce `docker build --target test` through `make docker-test` with the isolated pinned SDK, run required `make check`, fix remaining Slow/e2e root causes, and verify actual job conclusions. After the optimization merge, record merge timestamp, all six app/region verified-health timestamps, per-job runtime/queue, cache hits and executed suite counts; repeat an unchanged SHA to distinguish cold and warm preparation. Publish before/after results here with comparable-source limits, PR URLs and any unresolved failures. Bigger runners require measured elapsed-time benefit; billing savings require invoice evidence.

## CI storage decision (COS override, 19:01Z)

Use one durable private `ts-terrabase-ci` bucket in org `terrascale`, with bucket-scoped GitHub credentials. Each run owns a unique prefix and always deletes only its own objects. Missing credentials must fail the real Tigris test. Per-run app/bucket provisioning was removed; CI receives no org-level storage-create token. Implementation and shared-bucket isolation validation follow the top-priority GC/runner PR.

## Reconciled candidate after the prerequisites merged

The user selected alpha only, superseding the historical runner proposal above. #212 merged with
hosted ProcessorCount/HeapCount 8; #210 merged with the deployment re-enabled. The candidate keeps
main's shared remote builder, runner selection, SDK pin, production deploy steps, rollout order and
live gates. Hosted GC/job comparisons and live gru ProcessorCount 2 are recorded separately in
`2026-10-13-test-host-gc-hosted.md` and `2026-10-13-fly-runtime-cpu-gru.md`.

The durable private `ts-terrabase-ci` bucket was provisioned without a Fly app in org `terrascale`.
Its vendor-issued credentials were stored privately and supplied as `TIGRIS_CI_CREDENTIALS` to
GitHub. A read-only qualification returned HTTP 200 for the CI bucket and HTTP 403 for the
production bucket, confirming these credentials cannot read production bucket metadata. Fly's
bucket status reports `Public False`. No org-level provisioning credential is in this secret.
The real-storage test now requires credentials, scopes every object with a fresh ULID prefix,
joins the server's cleanup before deleting that prefix, and never deletes by modification time.

Slow reporting and preview script fixtures, actionlint and ShellCheck passed after reconciliation.
The reviewed follow-up enables same-repository PR previews after `test` success. Live preview
creation/close cleanup, hosted full Slow, and complete production rollout timings remain pending;
no result from them is claimed here. Pre-push validation follows the user's build-plus-targeted-tests
CI-gate decision; the older full local results remain evidence only for their recorded trees.
