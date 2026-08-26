---
status: accepted
---

# ADR 0006: Operations studio on Vinext

Parent record of charter PD-2026-08-26-01 D2 at repository level,
2026-08-26.

Owner: the parent fleet orchestrator, on the workspace owner's stack
policy (dashboards use Vinext) and the studio ticket wave's accepted
assumptions.

## Context

The workspace owner directed that dashboards use Vinext (React RSC on
the VitePlus `vp` toolchain), superseding an earlier Rust+Datastar
proposal for the V2 operations dashboard before any repository
execution. TerraBase V1 ships its Studio inside the database repository;
that self-contained precedent is kept for V2.

## Decisions

### D-1 — stack

The operations studio is a Vinext application: React 19 React Server
Components on the VitePlus `vp` toolchain, Tailwind v4 styling
conventions, Playwright e2e — following the tooling discipline
established by TerraScale.Dashboard.Next: `vp install --frozen-lockfile`,
`vp run vinext:dev|build|start`, `vp check`, no pnpm/yarn/bun/corepack,
no second lockfile, no direct npm/npx/vite/next/playwright invocations.

### D-2 — location and rejected alternatives

The studio lives in this repository under `studio/`, mirroring V1's
Studio-in-repo precedent: the database product ships self-contained
(Rust engine core, C# `host/` + `cli/`, Vinext `studio/`). Rejected:
hosting beside Dashboard.Next in TerraScale.Internals (splits the
database product across repositories and couples an operator tool to
customer-product infrastructure) and the superseded Rust+Datastar
direction (contradicts the owner's dashboard policy).

### D-3 — data access and live views

The studio consumes a generated typed client against the C# API host
contract only. No direct engine or storage access, no Rust C ABI, no
Platform/customer SDK dependency, no private routes. Live views ride the
API host's subscription endpoints (SSE/WebSocket) fed by the change
event backbone; backpressure (bounded queues, counted drops) is surfaced
in the UI honestly.

### D-4 — packaging (parked)

Whether the studio ships as host-served output (mirroring V1's
studio-zip convention) or a standalone Node process is decided in the
packaging ticket (DB-08) against the C# host artifact story and CI.

### D-5 — untouched by this decision

TerraBase V1 Studio (the parity reference), DevHost (fixed tailnet ports
18445/18446 and reserved loopback endpoints), and TerraScale.Dashboard.Next
(which already follows this policy).
