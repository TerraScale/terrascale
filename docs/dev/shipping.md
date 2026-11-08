# Shipping

Ship with plain git from your own worktree or clone:

```bash
git fetch origin
git rebase origin/main
make <area>-test-fast
git push origin HEAD:main
```

If the push is rejected because main advanced, fetch, rebase and push again.
GitHub Actions runs the affected areas' Fast suites for every push to main
(path filters in each workflow select the areas) and holds every deploy and
privileged credential. Deploy only green main. Slow suites run independently.

Each area's Fast suite must finish in under 30 s wall time; each single test must
finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. Fast
covers only the happy path and critical paths, Slow keeps only heavy critical
tests, and everything else is deleted. Delete at least 80% of tests against the
2026-11-04 workspace `test-policy.md` baseline. New features add at most one
happy-path and one critical-failure test.

For a missing GitHub push event, see [CI recovery](ci-recovery.md).
