---
status: accepted
---

# ADR 0015: Single checkout, no Git worktrees, named path ownership

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

### D-2 — concurrent named path ownership

Multiple filesystem writers may work in the normal checkout when each writer
claims named non-overlapping paths. A writer preserves unrelated changes,
coordinates before touching an overlapping path, stages named paths only,
reviews staged and unstaged diffs, fetches before push, and revalidates after
concurrent commits. Recovery and benchmark runs exclude other writers through
their existing exclusive lanes.

### D-3 — history

Dated ledger entries that mention old worktrees or the former one-writer rule
remain factual history. They grant no current permission. This decision
continues to supersede every active worktree instruction elsewhere in the
charter.

## Consequences

The P0-P8 phase plan keeps its dependency order, but independent work inside
the current phase may proceed concurrently on disjoint paths. Concurrency with
other live sessions is handled through named ownership,
fetch-often/verify-before-push discipline, and explicit overlap coordination,
not isolation worktrees. Shared manifests, lockfiles, generated registries,
and integration seams require an agreed owner before modification.
