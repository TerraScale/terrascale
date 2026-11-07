# Shipping from a worktree

On the TerraScale development hosts, commit the change and run
`scripts/dev/ts-ship` from its repository worktree. The command fetches and
rebases onto `origin/main`, runs only the affected Fast suites on the
least-loaded dev host, and pushes main. If main advances, it rebases and
validates the new candidate again.

Run that wrapper, not `ts-ship | tail` (or another pager or filter). A stopped
shell pipeline cannot reap: OpenCode session `ses_e548d2e3dffeiaQEQtC7ilEDQt`
left `ts-ship 2>&1 | tail` SIGSTOPped for about 12 hours, and its exited
children stayed zombies until the process was continued. `scripts/dev/ts-ship`
keeps a waiter in the shell's process group and runs the ship in a new session.
The ship ignores terminal stop signals (`SIGTSTP`, `SIGTTIN`, `SIGTTOU`) and is
a child subreaper, so it waits on descendants whose parents have exited.
SIGSTOP cannot be caught; leaving the stopped group is what keeps the ship
running. Killing the waiter delivers SIGTERM to the ship. When stdout or stderr
is a pipe, the wrapper writes one line naming the log under
`~/.local/state/ts-ship/` and the ship appends there instead of blocking on
the pipe. A pipeline's exit status is still the last stage.

The host binary is the same program. Move `~/.local/bin/ts-ship` to
`~/.local/bin/ts-ship.impl`, then `install scripts/dev/ts-ship ~/.local/bin/ts-ship`.
Do that on delta, beta and mariogk-dev so a direct `ts-ship` is covered too.

During live proofs, the COS can run `scripts/dev/ts-ship freeze terrascale 5 "live proof"`; validation continues, but main pushes wait until expiry or `scripts/dev/ts-ship unfreeze terrascale`.

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

**Temporary enforcement until #1084 closes:** `ts-ship` keeps its current 120 s
per-area suite cap and warn-only 10 s per-test check while the slow tests are
fixed. Once #1084 closes, both caps become hard: 10 s per test and 30 s per suite.
The temporary gate does not change the policy above.

Fast batches and `ts-heavy-any` jobs wait for dev host capacity instead of
overloading a host; see [Dev host capacity](dev-host-capacity.md).

For a missing GitHub push event, see [CI recovery](ci-recovery.md).
