# Recover CI and deployment

Main-push CI must pass `all-green` and both journey shards for the exact deploy
revision. A PR run or a dispatch cannot replace that proof. Re-run failed jobs
of the main-push CI after fixing the failure.

## Reconcile deployments

`Sites` runs after every main CI and Images completion, every 15 minutes, and on
a dispatch. It reconciles docs, landing, each status Worker, dashboard and auth.
An event starts reconciliation. Its SHA does not select the deployed source.

Each deploy job holds its own lock while it reads live, selects target and
deploys. The shared script is `scripts/ci/site-deploy-gate.mjs`. It fetches main
and walks its newest ten commits, newest first. For each commit it reads the
latest main-push CI run. A completed green run must also have successful
`all-green` and both journey shards. The first qualified commit is target.
No qualified commit within the bound fails the reconcile visibly.

Equal live and target needs no work. Live ahead of target on main stays live.
A strict ancestor advances only when its build inputs differ from target.
The comparison includes changes from commits whose CI failed. Missing or
unreadable revision records, split Worker traffic, and live commits outside
main fail. Only a confirmed absent Worker counts as a first deployment.
Status Workers are compared separately and only the selected Workers deploy.

Auth and dashboard read their readiness revision. Their release jobs run in
`ci.yml` and `images-ghcr.yml`, as required by the release service's existing
OIDC policy. When work is due, Sites requests a main dispatch. That job reads
live and chooses target again inside its release lock. It checks out target,
reuses an image labeled with that exact commit, or builds and checks one.
Only then does it move the mutable main tag and release the pinned arm64 digest.
The trigger commit and the availability of a prior Images run do not matter.

Every site deploy records target as the Worker version message. The proof
requires that version at 100% traffic. Auth and dashboard verify readiness and
write `deploy-revision`. The production journey reads this receipt, so it uses
the deployed source even when the dispatch was created on another commit.
A failing journey or failed release detection opens or updates one alert.

## Force reconciliation

Run all reconcilers now:

```sh
gh workflow run sites.yml --repo TerraScale/terrascale --ref main
```

Run just dashboard or auth:

```sh
gh workflow run images-ghcr.yml --repo TerraScale/terrascale --ref main
gh workflow run ci.yml --repo TerraScale/terrascale --ref main -f reconcile_auth=true
```

These requests still require journey-qualified main and changed inputs.
An equal live revision does not deploy again. Inspect the reconcile decision,
release proof and production journey before reporting recovery.

## Read budget

A reconciler normally makes two GitHub API reads: one candidate run query and
one jobs query. It makes at most twenty reads across ten candidates, with one
run query per candidate and at most one jobs query for each green run. There
are no image-run scans, commit API scans, or paginated history reads. Main
history and input comparisons use git. Qualification fails if a jobs page
would exceed 100 jobs.

Live costs two Cloudflare reads per existing Worker, or one readiness request
per service. With nothing changed and main qualified, a whole scheduled run
costs ten GitHub reads, ten Cloudflare reads for five Workers, and two readiness
requests. It installs nothing, builds nothing and dispatches nothing.

A site deploy adds two Cloudflare proof reads per deployed Worker, plus the
normal deployment requests. A service due at the coordinator adds a second
reconcile in its release lock: normally four GitHub qualification reads in
all, at most forty, and three readiness requests including the final proof.
Image reuse reads the manifest and its labeled arm64 image. A new build adds
its push and final manifest read. The existing release client also fetches its
qualified orchestrator release and region list. Those release asset downloads,
registry traffic and regional rollout requests do not occur on a no-op run.

## Manual releases and rollback

TerraBase release-only and SDK publication retain exact-commit qualification.
They are not scheduled reconciles. Build-only and release-only maintenance
reuse main-push proof. Native-artifact maintenance never qualifies a deploy.
Push SDK tags only after main CI is green for the exact tagged commit.

To undo this deployment rule, revert the PR's squash merge commit in a new PR.
Merge the revert through normal CI. This restores the prior triggers and gate
scripts together. Do not change repository rules or required check names.
To undo application code, revert that code through a PR instead. Once the new
main commit qualifies, reconciliation deploys the resulting inputs forward.
