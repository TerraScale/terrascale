# Deployment speed, 2026-10-14

UTC timestamps come from GitHub job/step records and the deploy script's pinned regional checks. The baseline is one
sample, not a latency distribution. Post-change timings remain pending the #291 build-once rollout and cache proof.

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

The later [#231 main run](https://github.com/TerraScale/terrascale/actions/runs/38609315584) deployed all six
TerraBase/auth machines at `c6ba61dbe5355d301239758a2f2a92cd06d3c139`: merge 17:45:09Z, final region live
17:56:04Z, 655 seconds. Fast passed; the full run failed E2E and TerraBase Slow. This precedes #291 and is
another before measurement, not evidence of a speedup from the build-once changes.

| Hosted CI sample | TerraBase job minutes | Auth job minutes | Libs job minutes | After #291 |
|---|---:|---:|---:|---|
| Cache handoff baseline (no run URL supplied) | 17.00 | 13.40 | 8.00 | Pending |
| #231 main, run `38609315584` | 13.17 | 11.32 | 6.73 | Pending |
| #291 before cache patch, run `38573026022` | 19.83 | 17.10 | 7.52 | Pending |

Job minutes are `completedAt - startedAt`, including setup, build, tests and cache export, excluding runner queue.
Different commits and workloads limit comparisons; the after measurement must report its run and cache hits.

| Cache inventory before change | Total decimal GB | Main GB | Other refs GB | Cleanup/after |
|---|---:|---:|---:|---|
| Initial handoff (49 entries) | 10.40 | Not supplied | About 6 GB PR BuildKit blobs | Pending |
| Fresh inventory (63 entries) | 14.990 | 4.089 | 10.901 | Pending |
| Follow-up inventory (54 entries) | 11.737 | 1.834 | 9.903 | Pending |

No caches were deleted for these observations. Automatic eviction changes the inventory between reads.
Only main exports the three area caches after this patch; every consumer restores them. NuGet's Docker layer
uses project, MSBuild and lockfile contents, so source-only edits retain package restore. Native E2E hashes
both repositories' lockfiles. The development-only Aspire tool is not installed in CI. Old PR-ref caches will
be deleted after the cache policy lands, then the second PR run must show dependency hits and no cache export.
Cache usage is not a permanent size guarantee: new main commits accumulate layers until GitHub evicts older ones.

| Baseline critical-path segment | Duration | Implemented change | Measured effect |
|---|---:|---|---|
| Merge to deploy job start | 236 s | Superseded PR cancellation and main freshness checks; active main rollouts finish | Pending |
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
