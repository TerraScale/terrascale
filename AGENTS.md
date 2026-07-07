# TerraScale TypeScript Instructions

## Repository Role

- This repository owns the public TerraScale TypeScript SDK package,
  `@terrascale/sdk`.
- Keep it npm-only. Commit `package.json` and `package-lock.json`; do not add
  pnpm, yarn, bun, corepack, or alternate lockfiles.
- The SDK must consume TerraScale only through public REST APIs. Do not add
  MagicOnion, direct TerraBase access, Dashboard private contracts, or
  service-internal shortcuts.
- Public customer APIs live under the root entrypoint. Dashboard/admin-only
  helpers must be kept in an explicit internal entrypoint and must not be
  advertised as the public customer SDK surface.

## Testing

- TypeScript SDK tests follow the workspace no-mocks rule (root `AGENTS.md`).
- Integration tests run against AppHost-emitted `TERRASCALE_API_BASE_URL` and
  `TERRASCALE_AUTH_API_BASE_URL` (`src/integration/apphost-smoke.test.ts`) and
  skip when those env vars are unset.

