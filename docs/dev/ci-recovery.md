# Recover CI after a missing push event

Owner: CI/release lane. A missing GitHub push event does not qualify a commit.
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

Dashboard release still requires trusted `ts-ship/dashboard` and
`dashboard-release/dashboard` statuses plus the checksum-verified application
archive for that exact revision. CI does not fabricate or carry those proofs.
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
When the missing run belongs to an older commit, qualify the current head on a
dev host and recover that head; a head dispatch cannot sign an older image.

For a manually recovered head whose CI completed before this heal was available:

```sh
gh workflow run images-ghcr.yml --repo TerraScale/terrascale --ref main
```

Inspect the CI and Images run URLs and the final live readiness revision before
reporting recovery. A successful dispatch request alone is not deployment proof.
The focused gate regression fixtures run during dev-host dashboard qualification.
