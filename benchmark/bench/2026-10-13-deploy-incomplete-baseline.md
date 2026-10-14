# Incomplete deployment baseline — 2026-10-13

The first deployment after re-enable did not complete. At the end of this run TerraBase was accepted in gru/sjc/fra and auth in gru/sjc; auth fra remained pending. This is failed-rollout evidence. A later run established the [first complete deployment baseline](2026-10-13-deploy-complete-baseline.md).

[Run 38467073536](https://github.com/TerraScale/terrascale/actions/runs/38467073536) deploys #210 merge `a537727bf70a2ea37f5ee9f75712291dd78f4057`, merged at **20:58:47Z**. T2 retains raw UTC logs and pinned live-health proof in `.cos/t2-greenfield-evidence/`. This report copies only allowlisted observations and does not operate any Fly machine.

| First-attempt job | Start UTC | End UTC | Wall seconds | Actual result |
|---|---|---|---:|---|
| changes | 20:58:53 | 20:59:01 | 8 | success |
| deploy | 20:59:08 | 21:11:10 | 722 | failure |
| auth | 21:00:57 | 21:08:44 | 467 | success |
| libs | 21:01:19 | 21:03:45 | 146 | success |
| terrabase | 21:00:29 | 21:09:13 | 524 | success |
| test | 21:09:16 | 21:09:39 | 23 | success |
| slow | 21:09:42 | 21:12:26 | 164 | failure |

The deploy job includes waiting for the exact-tree Fast gate, image build and rollout. These job durations must not be interpreted as regional rollout time. Main Fast passed; Slow failed the two auth fixture cases (23/25), and the old sequential stage did not execute TerraBase Slow. Live e2e was skipped because deploy failed.

| Deploy attempt | Start UTC | End UTC | Wall seconds | Why it failed |
|---|---|---|---:|---|
| 1 | 20:59:08 | 21:11:10 | 722 | Fly refused a start because TerraBase gru was still attempting to start |
| 2 | 21:12:47 | 21:20:58 | 491 | Missing auth callback; T2 reports it fixed |
| 3 | 21:25:45 | 21:29:48 | 243 | Fly refused a start because auth sjc was still attempting to start; fra was never updated |

Regional script checks in attempt 2 took TerraBase gru/sjc/fra **34/40/39 s**; attempt 3 took **38/36/38 s**, followed by auth gru **18 s**. TerraBase fra first completed its scripted live check at 21:15:29Z, **1,002 s after merge**. This only measures TerraBase completion within a failed overall rollout. Auth sjc was accepted by T2’s later pinned probes despite the failed flyctl start call; there is no auth-fra acceptance.

This run has no complete six app/region acceptance and cannot provide merge-to-all-regions time. The failures and manual retry intervals remain part of this run’s elapsed time; they are not silently removed. [Allowlisted evidence](2026-10-13-deploy-incomplete-baseline.json) includes each attempt’s job timestamps, regional log lines and five-region pinned probe observations.
