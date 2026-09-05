> Review-time snapshot of the workspace root file. The live workspace file is authoritative.

# TerraScale workspace review and consolidation

## Objective

Complete the workspace review, cleanup, integration assessment, v1 naming
reset, architecture documentation, and repository goal alignment requested by
the owner. The active goal-tool objective tracks this work. Product architecture
decisions belong in `GOAL.md`; this file tracks the review task.

## Owner requests

1. Review all repositories, including TerraBase, the dashboard, SDKs, edge,
   infrastructure, deployment, CLI, and documentation. Explain how they work
   together and identify what prevents complete end-to-end operation.
2. Explain the SQLite engine, sync engine, replication engine, and surrounding
   infrastructure with Markdown and Mermaid.
3. Propose many concrete features, improvements, and ways to simplify the
   project. Rank the work and separate proposals from accepted product scope.
4. Clean up old projects. Retired Forgejo projects belong in the read-only
   `TerraScale-Old` organization. Remove obsolete active-workspace copies only
   after preserving useful work.
5. Recover good work from all worktrees into their repositories, commit and
   push it, then remove the recovered worktrees.
6. Remove obsolete TerraScale second-version naming. This is the first product
   release. Update the connected producers, consumers, protocols, generated
   artifacts, fixtures, names, and documentation together.
7. Read the latest `ts-orchestrator/GOAL.md`, which the owner identifies as more
   current than the workspace goal. Reconcile the workspace goal with it, then
   update or create the goal of every active repository.
8. Use a rendering tool to create three architecture pictures: 3 regions,
   10 regions, and 20 regions. Trace incoming requests through the infrastructure
   and show the roles of the engine, replication, edge, and control plane.
   Supply editable Mermaid sources and inspect the rendered output.
9. Make `ts-proxy` the REST load balancer in the architecture: route around
   failed, draining, and overloaded member servers without violating database
   placement, sessions, idempotency, or durability. Review and document the
   implementation gaps and the exact producer/consumer acceptance requirements.
10. Start with two servers per Region, then scale as measured demand requires.
    Reflect the same-host orchestrators and fixed global R3 in the goals and all
    three diagrams. Do not confuse additional hosts with extra SQLite writers.
11. Consolidate these instructions into a written goal and use the goal tool to
   persist until all authorized work is handled.

## Constraints

- Forgejo operations use `fj`. Repositories use `develop`; no pull requests.
- Never edit human-owned `AGENTS.md` or `CLAUDE.md`; never add co-author trailers.
- Follow the owner's explicit commit rule: commit and push requested changes
  without running tests, checks, or builds first. Run qualification afterward
  and report results accurately.
- Rust commands run at lowered CPU priority.
- Preserve unrelated main-checkout edits and concurrent work. Recover source
  before deleting worktrees or old copies; keep recovery material private.
- Real external versions, such as NATS dependencies, Buf configuration and Linux
  cgroups, retain their correct names. Negative-version and revision tests must
  retain their original test semantics.
- Go owns backend projects except TerraBase in Rust and the explicitly assigned
  TypeScript dashboard, SDK, and proxy. Customer SDK languages remain their own.
- OCI and Cloudflare are the supported hosting platforms. Charge for writes and
  storage; keep Workers out of the reactive delivery hot path.
- Do not deploy services, mutate unknown cloud resources, or delete unknown data
  as part of this review. Repository commits and pushes are authorized.

## Completion evidence

- An inventory and integration report covers all eleven active repositories.
- Every recovered worktree has evidence of source preservation and pushed
  ancestry; registered secondary worktrees and retired local copies are gone.
- Retired Forgejo repositories are archived in the old organization.
- Coordinated v1 commits are pushed, with no hidden inclusion of unrelated work.
- The workspace goal and eleven repository goals agree on the current topology
  and service ownership. Findings and proposed features live outside normative
  goals unless they clarify already accepted requirements.
- The review contains concrete source references, actual verification results,
  blockers, simplification recommendations, and a substantial feature backlog.
- Three rendered architecture diagrams show 3, 10, and 20 regions, distinguish
  existing implementation from target design, and include editable sources.
- Requested commits are pushed and verification is reported with its limits.
  A unit-suite pass never substitutes for an unexecuted live or Firecracker gate.
- The final response links the deliverables and identifies any remaining
  external blockers without claiming complete production readiness.

## Progress record

- Recovered and pushed dashboard visual baselines and already committed
  TerraBase work; removed six merged worktrees and four obsolete snapshot copies.
- Verified nine retired Forgejo repositories were already archived/read-only in
  `TerraScale-Old`; eleven active repositories remain.
- Preserved a private local recovery archive outside the active workspace.
- Captured initial per-repository verification and integration findings.
- Coordinated v1 producers, consumers, fixtures and documentation committed and pushed.
- Workspace and all eleven repository goals updated, including two initial hosts
  per Region and the required member-aware load-balancing acceptance criteria.
- Review delivered in `ts-docs/reviews/workspace-2026-09-04.md`: thirteen integration
  findings, fifteen simplifications and seventy prioritized proposals.
- Three rendered and inspected overview pictures plus editable Mermaid detail
  sources delivered in `ts-docs/reviews/architecture/`.
- Follow-up SDK, control-plane and deployment verification recorded in the review.
  Production integration blockers and unexecuted live qualification remain explicit.
