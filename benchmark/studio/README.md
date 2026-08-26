# TerraBaseV2 operations studio (Vinext)

Vinext (React 19 RSC on the VitePlus `vp` toolchain) application per the
workspace stack policy (charter PD-2026-08-26-01 D2; ADR 0006). Consumes
a generated typed client against the C# API host; live views ride the
host's subscription endpoints fed by the change event backbone. V1
TerraBase.Studio is the parity reference and is not modified.

Tooling discipline (from Dashboard.Next conventions): `vp install
--frozen-lockfile`, `vp run vinext:dev|build|start`, `vp check`, `vp run
playwright:e2e`; no other package manager or lockfile, no direct
npm/npx/vite/next/playwright invocations.

Skeleton metadata only — implementation is tracked in
TerraScale/TerraBaseV2#15 (DB-01) and the DB-02..DB-08 tickets.
