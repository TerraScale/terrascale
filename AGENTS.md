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

- Tests must exercise real TerraScale HTTP boundaries. Do not add mocked HTTP
  clients, fake servers, jsdom-only tests, or in-memory substitutes as product
  proof.
- Integration tests should run against the AppHost-emitted API/Auth URLs once
  the SDK extraction is wired into the Internals AppHost test harness.

