# Deployment speed, 2026-10-14

UTC timestamps come from GitHub job/step records and the deploy script's pinned regional checks. The baseline is one
sample, not a latency distribution. Post-change timings remain pending a green-main rollout after the approved reset.

| Service | Baseline main | Merge UTC | GRU live UTC / elapsed | SJC live UTC / elapsed | FRA live UTC / elapsed | Post-change all-region elapsed |
|---|---|---|---|---|---|---|
| TerraBase | `ad2b9ee841379f7308a416bcb3f0838c60353714` | 2026-10-13 21:27:18 | 21:37:13 / 595 s | 21:37:55 / 637 s | 21:38:38 / 680 s | Pending |
| ts-auth | `ad2b9ee841379f7308a416bcb3f0838c60353714` | 2026-10-13 21:27:18 | 21:41:10 / 832 s | 21:41:40 / 862 s | 21:42:06 / 888 s | Pending |
| ts-dashboard | `09e8a04980116458409e82cf3e02eb4ed1d4ba39` | 2026-10-13 17:44:39 | No deployment | No deployment | No deployment | Pending first rollout |

The TerraScale [baseline run](https://github.com/TerraScale/terrascale/actions/runs/38470728889) deployed both services
successfully, but the workflow failed: TerraBase Fast passed 1,592/1,593. The gate accepted a successful PR-head check
on the identical tree before main's own later failure. That failure stays visible; this is deploy timing evidence,
not evidence of green main. The live replication matrix passed 13/13. The full E2E suite recorded five failures and
49 skips, so its successful job status is not a full-suite pass.

Dashboard's [baseline CI](https://github.com/TerraScale/ts-dashboard/actions/runs/38441116866) passed and completed
575 seconds after its main commit. That workflow had no deployment job, so there is no numeric merge-to-live baseline.
Fly inspection at 2026-10-14 07:13Z found the app suspended with zero machines.

| Baseline critical-path segment | Duration | Implemented change | Measured effect |
|---|---:|---|---|
| Merge to deploy job start | 236 s | Existing supersession cancellation/freshness checks retained | Pending |
| TerraBase image build | 264 s | Reuse the area's compiled/published cache; push one SHA-tagged runtime image | Pending |
| TerraBase regional rollout step | 154 s | Retain GRU first and each region's health/backup/SHA gate | Pending |
| ts-auth image build after TerraBase rollout | 117 s | Reuse each area's published output and push both images concurrently | Pending |
| ts-auth regional rollout step | 89 s | Reuse the published image; retain ordered regional health/SHA gates | Pending |

The final main `deploy-status` guard fails if the current commit's quality gate, deployment or live E2E is missing,
skipped, cancelled or failed. Obsolete green runs report supersession; GitHub API errors fail. Reset approval names
the exact target `StorageFormat.Id`, preventing approval for format 9 from permitting format 10 or another schema.
The guard self-checks pass locally; hosted cache reuse remains to be measured. No production speedup is claimed before the new workflow and rollout run.

Reproduce the baseline observations with `gh run view 38470728889 -R TerraScale/terrascale --json jobs`,
`gh run view 38470728889 -R TerraScale/terrascale --job 115548797510 --log`, and
`gh run view 38441116866 -R TerraScale/ts-dashboard --json jobs`. Regional elapsed values above are live timestamp minus
merge timestamp; rollout duration alone excludes runner queue, image builds and gate waits.
