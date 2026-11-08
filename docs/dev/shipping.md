# Shipping

Ship with plain git from your own worktree or clone:

```bash
git fetch origin
git rebase origin/main
# build or type-check only the project you changed, e.g. make terrabase-build
git push origin HEAD:main
```

If the push is rejected because main advanced, fetch, rebase and push again.
Don't run test suites before pushing: GitHub Actions runs the affected areas'
Fast suites on every push to main (path filters in each workflow select the
areas) and holds every deploy and privileged credential. Deploy only green main.
If your push turns main red, fix or revert it within about 30 minutes. Slow
suites run independently.

Each single test finishes in under 10 s, each area's Fast suite in under 30 s
and every CI test job in under 5 minutes; anything slower is a bug. Fast covers
only the happy path and critical paths, Slow keeps only heavy critical tests.
A new feature adds at most one happy-path and one critical-failure test.

For a missing GitHub push event, see [CI recovery](ci-recovery.md).
