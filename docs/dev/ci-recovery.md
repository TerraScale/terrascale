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
A release job that did not succeed starts no production journey; its own
attempt issue and job summary show the failure.

Every decision goes to the job summary with the thing, its live revision and
the target: deploying (with the attempt number), already live, live ahead,
inputs unchanged, suppressed after two attempts, waiting on an older target,
no qualified target, or waiting for a manual run.

## Attempts and suppression

Each thing (a Worker name, `auth` or `dashboard`) has at most one open issue
titled `Deployment attempts: <thing>`, labeled `deployment-alert`, opened by
`github-actions[bot]`. Its body holds one fenced JSON line with exactly
`thing`, `target` and `attempts`; the rest of the body is for people, including
a link to each attempt's run. Nothing reads the run links for decisions.

Only the deploy job writes this issue, while holding that thing's lock, after it
reads live and selects target again. The decision job only reads it to decide
whether to start the deploy job. Under the lock, in order:

1. If live already serves target (equal, ahead on main, or the same inputs) and
   the issue records target or an older commit, the job closes the issue
   naming the live commit and stops.
2. With no open issue, it records target with one attempt.
3. With the same target recorded, it records a second attempt. After two
   attempts it deploys nothing and the summary says suppressed.
4. With a newer target than the recorded one, it records target with one
   attempt.
5. With an older or unrelated target, it deploys nothing, changes nothing and
   the summary says it is waiting.

The count is written before the deployment starts. A job that fails, times
out, is cancelled or crashes has already been counted. Every first attempt
therefore opens the issue; the next reconcile after a successful deploy closes
it. For a Worker that is the next schedule; for a service it is the Sites run
that the release workflow's completion starts.

Before any decision the job checks the issue: exactly one open match, not a
pull request, opened by `github-actions[bot]`, exactly one state block with
those three keys, the thing's own name, a full commit hash and one or two
attempts. Anything else stops that thing with an error that names the issue,
also when live already equals target: the deploy job fails without deploying,
and the decision job starts no deploy job for it and fails after starting the
other things. Other things still deploy.
A reopened issue counts like any other.

To release a suppressed thing, either land a newer commit that qualifies, or
fix the cause and close its issue: the next reconcile then starts again at one
attempt. Do not edit the state line; a wrong edit stops the thing until the
issue closes. An unqualified commit cannot bypass the gate by closing an issue.

Service dispatches wait while a manual run of their workflow on main is queued
or running (for auth, any manual CI run). The summary links the oldest such run.
When it has been active for more than two hours the decision job fails with
that link; the red scheduled run is the alert. Worker deploys still start.
Each status Worker has its own lock, so all three can deploy in one tick.

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
An equal live revision does not deploy again. An issue with two attempts for
that target still blocks these requests until the issue closes or target changes.
Inspect the summary, release proof and production journey before reporting
recovery.

## Read and runner budget

With the newest main commit qualified and nothing changed, a schedule uses one
runner job, three GitHub API reads (one candidate CI run, one CI jobs page and
one open `deployment-alert` issue page), ten Cloudflare reads for five existing
Workers, and two readiness requests. It installs nothing, builds nothing,
writes nothing and dispatches nothing. CI time excludes scheduled Sites runs,
so measurement does not add a second no-op runner. Extra open issue pages add
one read per 100 issues. Checking the newest ten commits takes at most twenty
qualification reads. Git supplies history and input comparisons.

With exactly one Worker due, there are two runner jobs: decision and that
Worker's deploy. They total six GitHub reads (three each), twelve Cloudflare
reads before deployment and two proof reads afterward, plus the deployment.
The deploy job writes the issue once before deploying: one update, or on a
first attempt a label create (accepted when it exists) and an issue create.
Closing the issue later costs one more deploy job with three reads and one
update.

With exactly one service due, the decision uses three reads, five active-run
reads and one dispatch. Its release job reads the issue and qualification
again (three reads) and writes the issue once before releasing: eleven GitHub
reads across decision and deploy, plus four readiness requests including
proof. The dispatched CI auth reconcile retains three runner jobs (`main-auth`,
`test`, `all-green`); dashboard has one. A release completion starts a Sites
decision, which closes the issue through one more dispatch of the same
workflow (no build or release), and the production journey's detection,
suite and report jobs. Detection reads the jobs page and, for services, the
revision artifact metadata and ZIP. These follow-up jobs do not occur from a
no-op schedule.

Image reuse reads the manifest and its labeled arm64 image. A new build adds
its push and final manifest read. The release client fetches its qualified
orchestrator release and region list, then performs regional rollout requests.
These registry, release asset and rollout calls do not occur on a no-op run.
Qualification job pages over 100 jobs fail loudly; active-run checks read one
page for each status. Issue-list pagination is the only unbounded history
read and reflects the number of currently open alerts, not commit history.

## Manual releases and rollback

TerraBase release-only and SDK publication retain exact-commit qualification.
They are not scheduled reconciles. Build-only and release-only maintenance
reuse main-push proof. Native-artifact maintenance never qualifies a deploy.
Push SDK tags only after main CI is green for the exact tagged commit.

To undo this change, revert PR #2011's squash merge commit in a new PR and
merge it through normal CI. Revert the workflows and the shared reconcile
script together; this restores the prior triggers, failure handling and
schedule. Close any remaining `Deployment attempts` issues afterwards.
Reverting removes the two-attempt bound, so watch failing deploys immediately.
Do not change repository rules or required check names.
To undo application code, revert that code through a PR instead. Once the new
main commit qualifies, reconciliation deploys the resulting inputs forward.
