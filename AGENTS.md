# TerraScale TypeScript Instructions

## Repository Role

- This repository owns the public TerraScale TypeScript SDK package,
  `@terrascale/sdk`.
- Keep it npm-only. Commit `package.json` and `package-lock.json`; do not add
  pnpm, yarn, bun, corepack, or alternate lockfiles.
- The SDK must consume TerraScale only through public REST APIs. Do not add
  MagicOnion, direct TerraBase access, Dashboard private contracts, or
  service-internal shortcuts.
- Public customer APIs live under the root entrypoint. Role-protected platform
  administration is exported through an explicit public `admin` entrypoint;
  private Dashboard business contracts are prohibited.

## Testing

- TypeScript SDK tests follow the workspace no-mocks rule (root `AGENTS.md`).
- Integration tests run against DevHost-emitted `TERRASCALE_API_BASE_URL` and
  `TERRASCALE_AUTH_API_BASE_URL` (`src/integration/devhost-smoke.test.ts`) and
  skip when those env vars are unset.

## Branch and Release Policy

- Feature/fix/chore pull requests target `develop` and run no Forgejo automation. Direct pushes to `develop` and `main` are prohibited.
- Only a `develop` to `main` pull request may build and publish the immutable major-version release candidate.
