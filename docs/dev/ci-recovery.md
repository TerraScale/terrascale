# Recover CI after a missing push event

A missing GitHub push event does not qualify a commit.
Recover the actual `main` head with:

```sh
gh workflow run ci.yml --repo TerraScale/terrascale --ref main
```

Use the branch name: GitHub rejects a raw SHA as a dispatch ref. Normal dispatch
runs every core Fast/build/packaging area because it has no `push.before` to diff.
Images recovery dispatches must start at the real main head; branch and tag
dispatches cannot release. The existing `native_artifacts` maintenance dispatch remains
unqualified and cannot satisfy the release gate.

The aggregate `test` job includes policy checks, native verification, every
selected Fast suite and production packaging. Normal dispatch also runs both
core service jobs (`main` and `main-auth`), which only release from the real main
ref after their required Fast and packaging jobs pass. Existing CI behavior and
quality gates remain intact; the dispatch runs the existing checks.

Dashboard release requires that revision's own push or dispatch Images run,
whose Dashboard Fast job builds and tests the application it releases.
The Images gate accepts push CI after the complete run and its required `test`
job succeed. Normal dispatch additionally requires successful `main` and
`main-auth` jobs. Those main-ref-only service jobs prevent a branch or tag
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
The focused gate regression fixtures live in `scripts/dashboard/ci/release-gate-test.py`.

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

`deploy-landing` and `deploy-status` build and deploy the tip of `main` and
prove the live revision. `deploy-docs` runs `docs-deploy.yml`, which skips the
dispatched revision once a newer commit is the head of `main` and requires every
push workflow of that commit to be green (`docs-site/scripts/check-main.py`). To retry
one failed site deploy, re-run the failed jobs of its `Sites` run: a re-run
landing or status job deploys the tip of `main` at that moment.
