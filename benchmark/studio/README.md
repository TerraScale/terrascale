# TerraBaseV2 operations studio (Vinext)

Vinext (React 19 RSC on the VitePlus `vp` toolchain) application per the
workspace stack policy (charter PD-2026-08-26-01 D2; ADR 0006). Consumes a
generated typed client against the C# API host; live views ride the host
subscription endpoints fed by the change event backbone. V1 TerraBase.Studio
is the parity reference and is not modified.

Tooling discipline (from Dashboard.Next conventions): `vp install
--frozen-lockfile`, `vp run vinext:dev|build|start`, `vp check`, `vp run
playwright:e2e`; no other package manager or lockfile, no direct
npm/npx/vite/next/playwright invocations. The lockfile is `pnpm-lock.yaml`
(`vp` 0.3.0 manages dependencies through pnpm; the previous npm-style
`overrides` alias is not honored under it, so `vite` is pinned as a direct
devDependency).

## Layout

- `app/` — App Router: shell layout + navigation, home (engine status through
  typed reads, with real loading and error routes), live view (the trivial SSE
  tick Live View). Routes are `force-dynamic`: the API base URL and the
  operator session resolve per request.
- `contract/` — `terrabase.api.v2`, the stub-first contract for the SE-08 C#
  API host surface: envelope discipline from the SE-04 frame vocabulary, the
  engine `PublicOperation` set (`src/operations.rs`), the closed family kind
  set (`src/families/state.rs`), and the host-enforced operator session
  (`session.unauthenticated` + `loginUrl`; the default local surface is
  ungated per charter PD-2026-08-26-11 — the fixture simulates the gate).
- `src/api/generated/client.ts` — the typed client, generated from the
  contract (`vp run generate:client`; freshness guarded by unit test).
- `scripts/fixture-server.mjs` — the dev fixture server implementing the
  contract (sessions tick server-side so reconnects replay real missed
  deltas; controlled behaviors: status-slow/fail, session-gate, frame
  poison, connection drops).
- `e2e/` + `playwright.config.ts` — the Playwright harness; all servers boot
  on randomized loopback ports.
- `tests/` — node:test unit tests (fixture envelope behaviors, contract
  consistency, client freshness).

## Commands

```bash
vp install --frozen-lockfile     # install (lockfile committed)
vp run fixture:serve             # fixture API host on 127.0.0.1:3032
vp run vinext:dev                # dev server on 127.0.0.1:3031
vp run vinext:build              # standalone build (dist/standalone)
vp run vinext:start              # serve the standalone build
vp run vinext:check              # vinext compatibility scan
vp check                         # format + lint
vp run unit:test                 # node:test unit tests
vp run conformance:vectors       # contract conformance vectors vs the fixture
vp run playwright:e2e            # e2e (boots fixtures + built studio)
```

`STUDIO_API_BASE_URL` points the studio at the API host (default
`http://127.0.0.1:3032`, the fixture). Task runs use `cache: false` throughout
— see the note in `vite.config.ts`.

Implementation is tracked in the DB-02..DB-08 tickets
(seeded by TerraScale/TerraBaseV2#16).
