# Dashboard CI AFTER measurements

PR [#111](https://github.com/TerraScale/ts-dashboard/pull/111) merged as `03a29b917ee682a784d0b71ce13ce2a2c7cdbaf3`.

**Final PR `test`: 192s runner wall, 4s queue, 3.200 runner job-minutes.** The natural main Slow run passed in 190s, with 4s queue and 3.167 job-minutes. The <=240s PR target was met in this one observed PR run.

Images remains disabled by its owner during gru restoration. Per COS: **publish proof pending Images re-enable**. No image digest, <=4-minute publish, or live readiness is claimed. The existing publishing workflow name/path/push identity is preserved.

## BEFORE versus AFTER by event

Wall = first runner job start to last job end. Queue = run creation to first job start. Job-minutes = sum of runner job durations / 60. BEFORE is one diagnostic sample per event; its median is that sample, not the last-100 population median. Dispatches are not PR/push runs.

| Workflow/event | BEFORE n; median wall / queue / job-min | AFTER n; median wall / queue / job-min | Interpretation |
| --- | --- | --- | --- |
| CI / pull_request | 1; 355s / 3s / 5.917 | 1; **192s / 4s / 3.200** | Successful final PR |
| CI / push | 1; 314s / 25s / 5.233 (failed) | 0; removed | Publisher consumes verified exact-tree proof; full-check fallback |
| CI / workflow_dispatch | no sample | 2; 182.5s / 47s / 3.042 | One canceled, one failed; incomplete performance cohort |
| Slow / pull_request | 1; 223s / 208s / 3.717 (failed) | 0; removed | Complete coverage moved to affected main changes |
| Slow / push | 1; 254s / 77s / 4.233 | 1; **190s / 4s / 3.167** | Successful merged revision |
| Slow / workflow_dispatch | no sample | 2; 90s / 30s / 1.500 | Both canceled; truncated occupation, not suite completion |
| Images (GHCR) / push | 1; 179s / 38s / 2.983 (rollout failed) | 0; owner hold | COS owns activation and publish/live verification |
| CodeQL / workflow_dispatch | 1; 699s / 242s / 6.300 | no new run | Unchanged weekly policy; no extra measurement dispatch |

## Three real attempts per changed workflow

| Workflow | Event composition | Median occupied wall / queue / job-min | Outcomes |
| --- | --- | --- | --- |
| CI | 1 PR + 2 main dispatches | 192s / 5s / 3.200 | 1 success, 1 failure, 1 canceled |
| Slow | 1 main push + 2 main dispatches | 170s / 4s / 2.833 | 1 success, 2 canceled |

Canceled runs provide resource-occupation evidence only. These are **not three successful full executions** and the mixed-event medians do not establish a three-run PR/main speed guarantee. No additional measurement runs were triggered after these three attempts per workflow.

| Workflow | Event | Run | Outcome | Wall | Queue | Job-min |
| --- | --- | --- | --- | ---: | ---: | ---: |
| CI | pull_request | [39887617496](https://github.com/TerraScale/ts-dashboard/actions/runs/39887617496) | success | 192s | 4s | 3.200 |
| Slow | push | [39888050450](https://github.com/TerraScale/ts-dashboard/actions/runs/39888050450) | success | 190s | 4s | 3.167 |
| CI | workflow_dispatch | [39888674973](https://github.com/TerraScale/ts-dashboard/actions/runs/39888674973) | cancelled | 168s | 5s | 2.800 |
| Slow | workflow_dispatch | [39888677473](https://github.com/TerraScale/ts-dashboard/actions/runs/39888677473) | cancelled | 170s | 4s | 2.833 |
| CI | workflow_dispatch | [39888853054](https://github.com/TerraScale/ts-dashboard/actions/runs/39888853054) | failure | 197s | 89s | 3.283 |
| Slow | workflow_dispatch | [39888855295](https://github.com/TerraScale/ts-dashboard/actions/runs/39888855295) | cancelled | 10s | 56s | 0.167 |

The first dispatch pair was superseded by the second under `cancel-in-progress: true`. A later natural main push for #114 superseded the second Slow dispatch. CI's second dispatch failed because the browser-security assertion compared Razor-encoded `&#x2B;` against the raw CSP nonce. Upstream #114 already decodes the HTML attribute before retaining the same assertion; this CI lane does not change application code.

The next natural main Slow run [39888951210](https://github.com/TerraScale/ts-dashboard/actions/runs/39888951210) failed at the new four-minute Chromium/system dependency installation timeout. Recovery increases that bound to eight minutes and the whole-job budget to fifty, retaining time for redacted reporting. This later run is outside the original three-attempt cohort, and its completed runner timings were not retrieved after the ten-call timing budget was exhausted.

The intermediate PR run 39886319011 took 254s wall / 205s queue / 4.233 job-min before changed-file formatting. It is not final-policy proof.

## Failure coverage and validation

[The baseline audit](dashboard-baseline-audit.md) explains all 47 historical failures. Two narrowly scoped races remain quarantined under owner MarioGK in [#112](https://github.com/TerraScale/ts-dashboard/issues/112), expiring automatically at **2026-10-24T01:48:00Z**; all assertions remain. No root fix or five-repeat proof is claimed for these two races.

Actionlint 1.7.12 with ShellCheck 0.11.0 validated the workflows. Prior proof self-checks reject forged artifact names/source-tree mismatches; independent reviews cleared the receipt, timeout-reporting reserve, Markdown filters, and changed-file formatter. GitHub CI is the application-test gate.

Measurement budget: one last-100 list per workflow; **10 of 10 job/timing calls used** (six BEFORE, intermediate AFTER, final PR/main batch, completed cohort batch, and newer-main batch while its job was still running). No job/timing calls remain. Sanitized baseline data is committed; raw logs remain outside git.
