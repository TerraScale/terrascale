# PR preview cleanup

`.github/workflows/previews.yml` deletes PR previews (`ts-pr-<n>` apps and their `terrascale-ts-pr-<n>` Tigris buckets in
org `terrascale-previews-515`) with `scripts/fly/preview.sh`. Preview and cleanup jobs select
`${{ vars.CI_RUNNER || 'ubuntu-latest' }}`; the removed runner variable selects GitHub-hosted runners.

- **What goes.** On PR close, that PR's preview. Hourly (and on `workflow_dispatch`), `preview.sh expired` lists every
  preview app and every preview bucket, then reads each PR's state from GitHub: closed PRs go whatever their age and
  whether or not machines or the app still exist; open PRs go once their oldest machine (or, with no machine, their
  oldest volume) is more than 24 hours old.
- **No recreation.** Each deletion is one matrix job in the PR's creation group `preview-<n>` with
  `cancel-in-progress`, so it cancels that PR's running preview build before deleting. Different PRs never cancel
  each other, and one failed deletion does not stop the rest (`fail-fast: false`).
- **Fail closed.** A failed GitHub, Fly app, machine, volume or bucket listing, a listing that is not a JSON array of
  the expected fields, or a missing or unreadable creation date, is an error, never "absent": `expired` still prints
  the PRs it could decide (those are deleted) and fails the run. The bucket list has no JSON form, so it is parsed only if every line has the exact `NAME │ ORG` table shape.
- **Safe identity.** Only a plain PR number (`^[1-9][0-9]{0,8}$`) names an app; anything else exits with usage before
  any Fly call. `destroy` deletes only an app the Machines API reports in `terrascale-previews-515`.
- **Idempotent.** `destroy` deletes the bucket if listed, then the app if it exists; either may already be gone. A
  failed bucket deletion fails the job and keeps the app, so the next run retries both.

An open PR's app with neither machines nor volumes has no age and holds nothing paid, so it is kept until the PR
closes. A preview whose PR number GitHub does not know (404) is kept and fails every hourly run; remove it by hand
after checking its exact app and bucket name. An open PR's leftover bucket without an app has no age and is kept;
that PR's next preview build then fails on `flyctl storage create` (bucket names are global), and closing the PR
removes the bucket. Self-checks: `scripts/fly/preview-test.sh` and `scripts/ci/preview-lifecycle-test.py`.
