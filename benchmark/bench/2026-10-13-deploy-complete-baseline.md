# First complete three-region deployment baseline — 2026-10-13

**Merge → both apps live in gru/sjc/fra: 888 seconds (14m48s).** [Run 38470728889](https://github.com/TerraScale/terrascale/actions/runs/38470728889) deployed `ad2b9ee841379f7308a416bcb3f0838c60353714` after #220 merged at **21:27:18Z**. The final scripted auth-fra check passed at **21:42:06Z**. T2 later pinned every machine and obtained HTTP 200 on all six app/region pairs at 21:55:50–58Z.

Deployment completed, but the run failed. TerraBase Fast passed 1592/1593; aggregate test failed; Slow was skipped. The E2E job reports success while its full suite records **105 passes, 5 failures and 49 skips out of 159**. Its separate six-direction live replication matrix passed 13/13. Job success does not establish full E2E acceptance.

| Job | Start UTC | End UTC | Wall seconds | GitHub conclusion |
|---|---|---|---:|---|
| changes | 21:29:48 | 21:29:56 | 8 | success |
| deploy | 21:31:14 | 21:42:20 | 666 | success |
| preview | 21:27:22 | 21:27:21 | — | skipped |
| terrabase | 21:32:21 | 21:39:33 | 432 | failure |
| libs | 21:32:24 | 21:35:03 | 159 | success |
| auth | 21:36:57 | 21:44:06 | 429 | success |
| e2e | 21:46:55 | 21:52:46 | 351 | success |
| test | 21:47:24 | 21:47:32 | 8 | failure |
| slow | 21:47:33 | 21:47:32 | — | skipped |

Skipped jobs have no execution duration; GitHub can report their completion before their nominal start. Queue delays remain in the merge-to-live measurement. The deploy job took 666 seconds and included image builds, the exact-tree Fast gate and both rollouts.

| App | Region | Rollout start UTC | Live UTC | Wall seconds |
|---|---|---|---|---:|
| ts-terrabase | gru | 21:36:20 | 21:37:13 | 53 |
| ts-terrabase | sjc | 21:37:13 | 21:37:55 | 42 |
| ts-terrabase | fra | 21:37:55 | 21:38:38 | 43 |
| ts-auth | gru | 21:40:37 | 21:41:10 | 33 |
| ts-auth | sjc | 21:41:10 | 21:41:40 | 30 |
| ts-auth | fra | 21:41:40 | 21:42:06 | 26 |

The six regional checks total 227 seconds. TerraBase and auth each used one image across all three regions. This baseline already includes the eight-heap CI fix; it is the baseline for later workflow/preview changes, not the pre-GC baseline. Source, SDK, queue/load and first-deploy state must be accounted for in a later comparison.

The Fast failure was `PeerStreamTests.A_read_from_below_a_raise_still_returns_the_local_group_after_it` (expected ack 12 after sjc stops). Full E2E failures covered backup/PITR environment qualification, the all-node restart diagnostic, a pinned expected-failure guard, SQL authentication and cross-region cursor expiration. No assertion was weakened and no failure is counted as a pass.

[Allowlisted raw observations](2026-10-13-deploy-complete-baseline.json) retain timestamps, status codes and machine identities; they omit response bodies and credentials. Production machines were inspected read-only. A post-change all-region measurement and actual green main Slow/full E2E remain outstanding.
