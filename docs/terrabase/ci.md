# CI and shipping

Fetch origin and work from fresh `origin/main` in an isolated checkout. Commit a focused change with `DECISION: <choice>. Reason: <why>`, then run `ts-ship`. It rebases, runs affected Fast suites on a dev host and pushes main with race handling. Never force-push main.

Docs-only changes skip tests. Code changes use the root Makefile's affected area target, such as `make terrabase-test-fast`. Heavy builds, Slow suites and benchmarks use `ts-heavy-any`; never move them to GitHub Actions. Fast runs fully parallel and must meet the repository timing policy.

CI runs on every main push. Deploy only green main. Use `ts-main --repo terrascale` to distinguish BUILD from live PROBES. A failing Slow run needs a matching GitHub issue with SHA, environment, reproduction, expected/actual result, redacted logs and the run URL.

Production data resets and storage-format bumps belong to their assigned lane. Older data directories are refused; no migration is implied. SDK publishing and launch formalities remain [deferred](../decisions/go-live.md).
