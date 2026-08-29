---
status: accepted
---

# ADR 0015: Single checkout, no Git worktrees, one filesystem writer

Parent record of charter PD-2026-08-28-04, 2026-08-28, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
that TerraBaseV2 development must not use worktrees.

## Context

The W3-W6 waves were built in `.worktrees/*` fleet worktrees with
parallel builder packets and integrator merges. That model produced
real integration hazards (documented in the charter ledger: `.gitignore`
sweeps of worktree gitlinks, merges committed inside worktrees,
double-dispatched packets). The owner replaced it with a strict
single-checkout discipline for all future execution.

## Decisions

### D-1 — single checkout

Every future write occurs in the repository's normal `develop` checkout.
No agent, script, lane, or reviewer creates, adopts, or reuses a Git
worktree for TerraBaseV2 development. Read-only review may overlap a
writer.

### D-2 — one writer

One filesystem writer holds the checkout lease. It starts from a clean
tree, stages named paths, reviews staged and unstaged diffs, commits,
and returns a clean tree before another writer begins. Recovery and
benchmark runs also exclude other writers (exclusive lanes).

### D-3 — history

Dated ledger entries that mention old worktrees remain factual history.
They grant no current permission. This decision supersedes every active
worktree or parallel-writer instruction elsewhere in the charter.

## Consequences

Parallelism moves to read-only fan-out (analysis, review, verification)
while writes stay serial. The P0-P7 phase plan is serial by phase in the
normal checkout. Concurrency with other live sessions on this machine is
handled by fetch-often/verify-before-push discipline, not by isolation
worktrees.
