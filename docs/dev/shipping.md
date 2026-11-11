# Shipping

Every change ships as a pull request to `main` from your own worktree or clone:

```bash
git fetch origin
git switch -c <topic> origin/main
# build or type-check only the project you changed, e.g. make terrabase-build
git push -u origin HEAD
gh pr create --base main --fill
```

GitHub Actions builds and checks the affected areas and runs the journey on the
PR (the `changes` job of `ci.yml` selects them); on main, the release jobs build
and publish the services a push changed. The site checks run on PRs only; on main,
`sites.yml` builds and deploys the sites a push changed. A PR squash-merges as soon as its checks are
green and its review passed (`gh pr merge <pr> --squash --delete-branch`). If
it conflicts, rebase it on `origin/main` and push the branch again. Never push
directly to `main`.

Deploy only green main. If your merge turns main red, fix or revert it within
about 30 minutes.

The journey in `e2e/` is the only test; a behaviour that matters becomes a
journey step ([`e2e/AGENTS.md`](../../e2e/AGENTS.md)).

A `CI time regression: <workflow> (<event>)` issue (label `ci-time`) means a
successful first-attempt `CI`, `Sites` or `Images (GHCR)` run took more than 30 s
longer than the median of its latest five earlier runs with the same jobs, or one
of its jobs took more than 30 s longer than the slowest of its own latest five
runs (`CI time`, `.github/workflows/ci-time.yml`). Read its per-job table: find
the job that grew, fix the cause in a pull request, then close the issue. A
job's time starts when the job starts and the run's time when the run is
created, so a slow run whose jobs did not grow waited for a runner or a
concurrency lock (the queued seconds show it). The check never blocks a merge.

For a missing GitHub push event, see [CI recovery](ci-recovery.md).
