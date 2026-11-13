# Recover CI and deployment

Main-push CI must pass `all-green` and both journey shards for the exact deploy
revision. A PR run or a dispatch cannot replace that proof. Re-run failed jobs
of the main-push CI after fixing the failure.

## Reconcile deployments

`Sites` runs after every main CI and Images completion, every 15 minutes, and on
a dispatch. It reconciles docs, landing, each status Worker, dashboard and auth.
An event starts reconciliation. Its SHA does not select the deployed source.

One short decision job reads every live revision and selects target once.
It starts site jobs only for Workers with work and dispatches service workflows
only when a service has work. A no-op schedule runs no other runner jobs.

Each deploy job holds its own lock while it reads live, selects target and
deploys. The shared script is `scripts/ci/site-deploy-gate.mjs`. It fetches main
and walks its newest ten commits, newest first. For each commit it reads the
latest main-push CI run. A completed green run must also have successful
`all-green` and both journey shards. The first qualified commit is target.
If none qualifies within the bound, an on-main live revision stays live and the
decision says "nothing qualified yet, live is <sha>". An unreadable or off-main
live revision still fails visibly. A confirmed absent Worker needs a qualified
target before its first deployment.

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
A failing journey or failed release detection opens or updates one production
journey alert. A failed image build, rollout or deployment proof is reported by
the deploy job itself, before release detection decides whether a proved release
is available for the journey.

## Stuck deployment and retry

A failed deploy opens one issue titled `Deployment failure: <thing>`, labeled
`deployment-alert`. The thing is a Worker name, `auth`, `dashboard`, or
`terrabase`. Its body records `Target: <commit>`, `Failed attempts: <count>`
and the run link. Further failures update the same issue. There is no artifact
or cache storing the attempt count.

The next reconcile can try the same target once more. After two failures, the
reconcile says it is waiting for a new target or a closed issue, dispatches
nothing for that thing, and succeeds quietly. Other things still reconcile.
A newly qualified target starts at zero attempts, even while the old issue
stays open. A successful convergence closes the issue with the deployed commit.

To retry the same target after fixing its cause, close its deployment issue,
then wait for the next schedule or request reconciliation below. Closing the
issue discards the attempt count; do not edit the machine-readable body fields.
An unqualified commit cannot bypass the gate by closing an issue. Manual
TerraBase releases also consult the issue after exact-commit qualification.

Decision jobs serialize dispatch decisions. Before a service dispatch, the
coordinator checks queued, in-progress, waiting, pending and requested dispatch
runs of its deploy workflow. Any active dispatch of that workflow makes it wait.
For auth this conservatively includes other manual CI dispatches. Deploy jobs
also re-select under their existing per-thing locks, so a second reconcile
cannot release stale work after an active deploy completes. Each status Worker
has its own issue and attempt count; their deploy jobs retain the shared status
lock.

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
An equal live revision does not deploy again. An open issue with two failures for
that target still blocks these requests until the issue closes or target changes.
Inspect the reconcile decision,
release proof and production journey before reporting recovery.

## Read and runner budget

With the newest main commit qualified and nothing changed, a schedule uses one
runner job, three GitHub API reads (one candidate CI run, one CI jobs page and
one open deployment-alert issue page), ten Cloudflare reads for five existing
Workers, and two readiness requests. Qualification is shared across all things.
It installs nothing, builds nothing and dispatches nothing. CI time excludes
scheduled Sites runs, so measurement does not add a second no-op runner.
Event-driven Sites runs and service releases retain their CI time job.
Extra open-alert
pages add one read per 100 issues. Checking the newest ten commits takes at most
twenty qualification reads. Git supplies history and input comparisons.

With exactly one site Worker due, there are two runner jobs: decision and that
Worker's deploy. They normally total six GitHub reads, twelve Cloudflare reads
before deployment and two Cloudflare proof reads afterward, plus the deployment
requests. The final outcome reads the issue list once more, for seven GitHub
reads total. Closing an existing alert adds one issue PATCH. First failure adds
a labels read, creates the label if absent, and creates the issue; a later
failure updates that issue.

With exactly one service due, decision uses three GitHub reads plus at most five
active-run reads and one dispatch write. Its release job repeats the three-read
gate under the lock, then reads alerts once to record the outcome: at most twelve
GitHub reads across decision and deploy, plus four readiness requests including
proof. The dispatched CI auth reconcile retains three runner jobs (`main-auth`,
`test`, `all-green`); dashboard has one. Including decision, that is four
runner jobs for auth or two for dashboard. A successful release completion adds a no-op Sites decision job
and the production journey's detection, suite and report jobs. Detection reads
the jobs page and, for services, the revision artifact metadata and ZIP. Report
reads its open journey issues and writes only when needed. These follow-up jobs
do not occur from a no-op schedule.

Image reuse reads the manifest and its labeled arm64 image. A new build adds
its push and final manifest read. The release client fetches its qualified
orchestrator release and region list, then performs regional rollout requests.
These registry, release asset and rollout calls do not occur on a no-op run.
Qualification job pages over 100 jobs fail loudly; active-run checks need only
one result for each status. Alert-list pagination is the only unbounded history
read and reflects the number of currently open alerts, not commit history.

## Manual releases and rollback

TerraBase release-only and SDK publication retain exact-commit qualification.
They are not scheduled reconciles. Build-only and release-only maintenance
reuse main-push proof. Native-artifact maintenance never qualifies a deploy.
Push SDK tags only after main CI is green for the exact tagged commit.

To undo this change, revert PR #2011's squash merge commit in a new PR and
merge it through normal CI. Revert the workflows, shared selector and alert
script together; this restores the prior triggers, failure handling and schedule.
Close any remaining `deployment-alert` issues after inspecting their run links.
Reverting removes the two-attempt bound, so watch failing deploys immediately.
Do not change repository rules or required check names.
To undo application code, revert that code through a PR instead. Once the new
main commit qualifies, reconciliation deploys the resulting inputs forward.
