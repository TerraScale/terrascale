# Shipping from a worktree

On the TerraScale development hosts, commit the change and run `ts-ship` from
its repository worktree. The command fetches and rebases onto `origin/main`,
runs only the affected Fast suites on the least-loaded dev host, and pushes
main. If main advances, it rebases and validates the new candidate again.

Candidates travel through temporary `refs/ts-ship/` refs, which the command
removes after validation. No temporary branch needs to be published or deleted.
The monorepo maps library, authentication, TerraBase and benchmark changes to
its Makefile targets; standalone repositories supply their Fast command in
`AGENTS.md`. Shared or unrecognized monorepo build changes validate all areas.
Library changes also validate consuming services. Documentation, `.cos` and
`AGENTS.md` changes skip test execution.

Success prints `SHIPPED <repo> <sha> <secs>`. A failure prints
`RED <repo> <area> <first failing test> <log path>`; detailed command output is
stored under `~/.local/state/ts-ship/`. Commit or stash working tree changes
before shipping. A rebase conflict stops shipping and restores the pre-rebase
branch so the conflict can be resolved deliberately.

Run `ts-main` for a single CI snapshot: one line per active repository with
main's result, result age and any failing job. Main CI must be green before a
production deploy. Slow suites remain independent of the Fast shipping gate.

Each area's Fast suite must finish in under 30 s wall time; each single test must
finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. Fast
covers only the happy path and critical paths, Slow keeps only heavy critical
tests, and everything else is deleted. Delete at least 80% of tests against the
2026-11-04 workspace `test-policy.md` baseline. New features add at most one
happy-path and one critical-failure test.

**Temporary enforcement until #1084 closes:** `ts-ship` reports its 30 s
per-area suite budget and 10 s per-test budget as warnings while the slow tests
are fixed. Once #1084 closes, both caps become hard: 10 s per test and 30 s per suite.
The temporary gate does not change the policy above.

Fast batches and `ts-heavy-any` jobs wait for dev host capacity instead of
overloading a host; see [Dev host capacity](dev-host-capacity.md).

For a missing GitHub push event, see [CI recovery](ci-recovery.md).
