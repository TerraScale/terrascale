# Shipping

Every change ships as a pull request to `main` from your own worktree or clone:

```bash
git fetch origin
git switch -c <topic> origin/main
# build or type-check only the project you changed, e.g. make terrabase-build
git push -u origin HEAD
gh pr create --base main --fill
```

Don't run test suites before pushing: GitHub Actions runs the affected areas'
Fast suites on the PR (the `changes` job of `ci.yml` selects the areas) and
again on main after the merge. The site checks run on PRs only; on main,
`sites.yml` builds and deploys the sites a push changed. A PR squash-merges as soon as its checks are
green and its review passed (`gh pr merge <pr> --squash --delete-branch`). If
it conflicts, rebase it on `origin/main` and push the branch again. Never push
directly to `main`.

Deploy only green main. If your merge turns main red, fix or revert it within
about 30 minutes. No workflow runs the Slow suites.

Each single test finishes in under 10 s, each area's Fast suite in under 30 s
and every CI test job in under 5 minutes; anything slower is a bug. Fast covers
only the happy path and critical paths, Slow keeps only heavy critical tests.
A new feature adds at most one happy-path and one critical-failure test.

A `ci-time` issue means a successful `CI`, `Sites` or `Images (GHCR)` run took
more than 30 s longer than the median of its latest earlier runs with the same
jobs (`CI time`, `.github/workflows/ci-time.yml`). Read its per-job table,
largest growth first: find the job that grew, fix the cause in a pull request,
then close the issue. A job's time starts when the job starts and the run's
time when the run is created, so if no job grew, the run waited for a runner
or a concurrency lock. The check never blocks a merge.

For a missing GitHub push event, see [CI recovery](ci-recovery.md).
