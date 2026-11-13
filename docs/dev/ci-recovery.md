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
OIDC policy. The release is signed with the dispatched run's commit, and each
region refuses an image whose revision label names another commit. So Sites
requests a main dispatch only when work is due and main's head is the target;
otherwise the decision says it is waiting for main's head to qualify. The
dispatched job reads live inside its release lock and releases only its own
commit, and only if that commit is qualified; if not, it records nothing and
the next reconcile after main CI tries again. It checks out that commit,
reuses an image labeled with it, or builds and checks one. Only then does it
move the mutable main tag and release the pinned arm64 digest. The availability
of a prior Images run does not matter.

Every site deploy records target as the Worker version message. The proof
requires that version at 100% traffic. Auth and dashboard verify readiness and
write `deploy-revision`. The production journey reads this receipt, so it uses
the deployed source.
A release job that did not succeed starts no production journey; its own
state issue and job summary show the failure.

Every decision goes to the job summary with the thing, its live revision and
the target: deploying (with the attempt number), already live, live ahead,
inputs unchanged, suppressed after two attempts, waiting on an older target,
no qualified target, waiting for main's head to qualify, or waiting for a
manual run.

## Attempts and suppression

Each thing (a Worker name, `auth` or `dashboard`) has one persistent issue
`Deployment state: <thing>`, labeled `deployment-alert`, created by
`github-actions[bot]`. Automation never closes it. The machine-readable block
contains exactly `thing`, `target`, `attempts` (an integer from zero to two), and
`notified` (a boolean). Attempt run links outside the block are informational.

When healthy, an operator sees no deployment notification. Initial creation
happens once per thing; subsequent changes edit only the issue body. The first
reconcile after a successful deployment records zero attempts. Repeated healthy
ticks do not write anything. Suppression adds at most one comment naming the
thing, target, both attempt runs when known, and the release instructions. The
state records `notified: true` before the comment is posted, so a failed post
is not repeated and that one notification can be missed; the state issue and
the run summary still show the suppression. Later suppressed ticks are quiet.

Only a locked job writes state. The decision job reads and validates all things
before taking any target-dependent branch, including when no commit qualifies.
The locked job validates again before deciding. Under the lock, after the fresh
live read and target selection:

1. If live equals target, write `(target, 0, false)` if needed and stop.
2. If there is no state, record `(target, 1, false)` before deploying.
3. For the same target with fewer than two attempts, increment attempts before
   deploying. With two attempts, deploy nothing; if `notified` is false, write
   `notified: true` and then add the suppression comment.
4. For a strict descendant of the recorded target, record `(target, 1, false)`
   before deploying.
5. For an older or unrelated target, change nothing and report waiting.

The live and input comparisons still apply: live ahead on main and unchanged
build inputs do not deploy. A first reconcile in those states initializes the
issue with zero attempts; an existing record is preserved unless live equals
target. A timeout, cancellation or crash after the count write consumes an
attempt. No deployment starts unless its state write succeeds.

Discovery lists all open and closed issues created by the workflow bot, without
filtering title or label. A second open-and-closed labeled listing also catches
state created by a person. Both are paginated and shared across all things.
Candidates name the thing in their title, in a fenced block's `thing` field, or
anywhere while carrying the state label. Duplicate candidates fail with all
issue links. A single candidate must have the exact title, label, open state,
bot identity, no pull request identity, exactly one JSON block with the four
keys, its own thing name, a full 40-character hexadecimal target, valid attempt
count and boolean notification value. A removed label, edited title or closed
issue remains discoverable. Closing is an error saying to reopen the issue.
Invalid state blocks that thing even if live already serves target or no commit
qualifies; other valid things can still proceed.

To release suppression, fix the cause and either land a newer commit that
qualifies or run the manual reset for that thing:

```sh
gh workflow run sites.yml --repo TerraScale/terrascale --ref main -f reset_thing=ts-docs
```

A first job accepts only an exact, case-sensitive known name and supplies the
lock; any other value fails there, before a deployment lock is taken.

Accepted names are `ts-docs`, `ts-landing`, `ts-status-vantage-sfo`,
`ts-status-vantage-gru`, `ts-status`, `auth` and `dashboard`. The reset job takes
that thing's existing deployment lock, validates its state and writes the same
recorded target with zero attempts and `notified: false`. It deploys nothing;
the next normal reconcile can attempt again. Reset does not require a newly
qualified target. Missing or invalid state fails reset. Reopen a closed issue
and restore invalid fields before resetting. Hand edits go through the same
validation; they are not an operator release procedure.

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
that target still blocks these requests until a locked reset or a newer target.
Inspect the summary, release proof and production journey before reporting
recovery.

## Read and runner budget

Counts below assume the newest main commit qualifies and each issue listing
fits one page. Let P be the total pages of bot-created issues plus labeled
issues, including closed history; P is at least two. Issue discovery costs P
GitHub reads per script invocation, shared by every thing. Qualification costs
two reads for the newest green commit and at most twenty over the bounded ten
commits. This adds one minimum read versus the previous label-only lookup to
also reject person-created state. History pagination adds one read per page;
it is not bounded by the number of currently open state issues.

An established no-op schedule uses one runner, P+2 GitHub reads (four), ten
Cloudflare reads for five existing Workers and two readiness requests. It
installs, builds, writes and dispatches nothing. CI time excludes scheduled
Sites runs. Initial state creation is a one-time exception: the decision
starts each thing's locked job to initialize its persistent issue.

One existing Worker deployment uses decision and deploy jobs: 2(P+2) GitHub reads
(eight), one body PATCH before deploying, twelve Cloudflare live reads and two
proof reads, plus the provider deployment. Initial state instead costs two
writes (ensure label and create issue). The next successful-live reconcile
costs another decision and locked job (eight reads) and one healthy-state body
PATCH, with no issue close or comment.

One service deployment uses 2(P+2)+5 GitHub reads (thirteen): decision,
five active-run checks and locked release. Writes are one workflow dispatch
and one state-body PATCH; first initialization replaces the PATCH with label
and issue creation. There are four readiness requests including proof.
Auth retains three dispatch runner jobs (`main-auth`, `test`, `all-green`),
dashboard one. Success triggers another Sites decision and a locked state
refresh, with the same thirteen reads, one dispatch and one body PATCH, no
build or release. The release also starts production journey detection, suite
and report; detection reads jobs and the revision artifact metadata and ZIP.
These follow-ups are outside the deployment gate counts.

A suppressed, already-notified thing adds no writes or deploy job to the
four-read decision. On first suppression, a Worker adds its four-read locked
job and two writes (one comment, one body PATCH); a service adds five active-run
reads, the four-read locked job and three writes (dispatch, comment, PATCH).
The reset job uses P reads (two) and one body PATCH without live or CI reads.

For fifteen successful deployments of every one of the seven things, an
established day creates zero issues, adds zero comments and makes 210 body
edits (two per deployment). If those fifteen deployments include each thing's
first ever reconcile, seven issues are created and 203 body edits occur, with
zero comments and seven label-ensure calls. For fewer changed things, count
two body edits per successful deployment, replacing its first edit with issue
creation when state is absent. Merely qualifying a merge creates no writes
when inputs are unchanged and state is already initialized.

Image reuse reads the manifest and its labeled arm64 image. A new build adds
its push and final manifest read. The release client fetches its qualified
orchestrator release and region list, then performs regional rollout requests.
These registry, release asset and rollout calls do not occur on a no-op run.
Qualification job pages over 100 jobs fail loudly; active-run checks read one
page for each status. Issue-list pagination is the only unbounded history
read and includes the bot and labeled issue histories.

## Manual releases and rollback

TerraBase release-only and SDK publication retain exact-commit qualification.
They are not scheduled reconciles. Build-only and release-only maintenance
reuse main-push proof. Native-artifact maintenance never qualifies a deploy.
Push SDK tags only after main CI is green for the exact tagged commit.

To undo this change, revert PR #2011's squash merge commit in a new PR and
merge it through normal CI. Revert the workflows and the shared reconcile
script together; this restores the prior triggers, failure handling and
schedule. After reverting, manually close the remaining `Deployment state` issues; the
reconciler never closes them itself.
Reverting removes the two-attempt bound, so watch failing deploys immediately.
Do not change repository rules or required check names.
To undo application code, revert that code through a PR instead. Once the new
main commit qualifies, reconciliation deploys the resulting inputs forward.
