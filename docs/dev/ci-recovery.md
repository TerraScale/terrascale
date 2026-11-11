# Recover CI after a missing push event

A missing GitHub push event does not qualify a commit.
Recover the actual `main` head with:

```sh
gh workflow run ci.yml --repo TerraScale/terrascale --ref main
```

Use the branch name: GitHub rejects a raw SHA as a dispatch ref. Normal dispatch
selects both services, TerraBase and ts-auth, because it has no `push.before` to diff.
Images recovery dispatches must start at the real main head; branch and tag
dispatches cannot release. The existing `native_artifacts` maintenance dispatch remains
unqualified and cannot satisfy the release gate.

On a dispatch the aggregate `test` job requires `changes` and the policy checks.
Normal dispatch then runs both service jobs (`main`, then `main-auth-ordered`), which
publish from their own Release build and release only from the real main ref.

Dashboard release requires that revision's own push or dispatch Images run,
whose `test` job (`dashboard-ci.yml`) builds and checks the application it releases.
The Images gate accepts push CI after the complete run and its required `test`
job succeed. Normal dispatch additionally requires successful `main` and
`main-auth-ordered` jobs. Those main-ref-only service jobs prevent a branch or tag
dispatch from posing as main qualification. Failed CI remains disqualifying, and native
maintenance, pull requests and skipped quality gates cannot qualify.

After CI completes, Images' `workflow_run` heal reruns an existing push/dispatch
Images run. If GitHub never created one, it dispatches Images on `main` only when
the qualified revision is still the head. The resulting push/dispatch OIDC token
signs the same SHA as the image label; a `workflow_run` token never deploys.
Each run keeps signing its original main SHA after unrelated pushes, just as a
push run does. Images rechecks CI, newest qualification and deployed ancestry
immediately before release, so a newer qualified revision still supersedes it.
When the missing run belongs to an older commit, recover the current head; a head dispatch cannot sign an older image.
Older qualifications with no push or dispatch Images run are skipped during
selection, so they cannot strand a newer recovery. A qualified current head
stays eligible: heal can create its missing dispatch. Existing signed main
runs remain eligible after unrelated pushes.

For a manually recovered head whose CI completed before this heal was available:

```sh
gh workflow run images-ghcr.yml --repo TerraScale/terrascale --ref main
```

Inspect the CI and Images run URLs and the final live readiness revision before
reporting recovery. A successful dispatch request alone is not deployment proof.

Use the controlled missed-push drill to prove recovery
without a push CI run and record the exact revision and deployment evidence.

Core service publication waits for the aggregate `test` job. When both services
are selected, TerraBase must finish its rollout before ts-auth starts, so a
failed TerraBase check cannot deploy only a new scope issuer. Auth-only runs
continue after `test` when TerraBase was intentionally not selected.

## Redeploy the sites

The docs, landing and status sites deploy from `.github/workflows/sites.yml`
(`Sites`), not from CI. To redeploy all three, dispatch it on `main`:

```sh
gh workflow run sites.yml --repo TerraScale/terrascale --ref main
```

`deploy-docs`, `deploy-landing` and `deploy-status` each wait for their site's
lock, check out the tip of `main` when they start, build it and deploy it. A
dispatch deploys that tip without checking its CI, as a push does: CI on the
pull request checks a change before it reaches `main`. Each job then runs
`scripts/ci/prove-worker-deploy.mjs`. It takes the version each Worker got from
the deploy's own record and reads Cloudflare's records for it: the Worker's
active deployment must serve that version at 100%, and the version's message
must be the commit the job checked out. It never requests the public sites, so
a new version still reaching Cloudflare's edge cannot fail it. To retry a
failed site deploy, re-run its failed job with `gh run rerun <run id> --failed`;
like every deploy job, the re-run checks out the tip of `main` when it starts.
