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
- The SDK exports no Dashboard-named entrypoint, method, DTO, or route. Dashboard
  is a full-stack consumer that composes typed, Dashboard-agnostic public product
  operations locally; the SDK is its sole TerraScale data-access boundary and
  gives it no special identity, header, authorization, or tenant bypass beyond
  what any external user receives.

## Parallel Delivery and Public Contracts

- Claim clear ownership before editing: each agent owns a bounded file/module
  slice. Inspect the worktree first, coordinate any shared contract, export, or
  generated-file change, and do not overwrite or reformat another agent's work.
- Implement public capabilities contract-first: land the Platform public REST
  contract, route manifest, and OpenAPI surface first; then add the matching
  typed SDK DTOs and operation; finally update consumers, docs, and adapters.
  Never invent an SDK-only route or contract to unblock a consumer.
- Every typed public SDK operation needs proof at the public boundary: verify
  its OpenAPI and route-manifest mapping have not drifted, and show DevHost live
  success and expected-failure evidence for its authorization/validation path
  when the required environment is available.
- MCP adapters may compose and expose only typed SDK operations. Do not add a
  generic request method, arbitrary path/body forwarding, or another escape
  hatch that bypasses the public typed contract.

## Testing

- TypeScript SDK tests follow the workspace no-mocks rule (root `AGENTS.md`).
- Integration tests run against DevHost-emitted `TERRASCALE_API_BASE_URL` and
  `TERRASCALE_AUTH_API_BASE_URL` (`src/integration/devhost-smoke.test.ts`) and
  skip when those env vars are unset.
- Keep live tests explicitly environment-gated. Do not commit credentials,
  tokens, `.env` files, captured authorization headers, or real tenant data;
  supply secrets only through the local/CI environment and redact them from
  logs, fixtures, test output, and commits.

## Branch and Git Workflow

Workspace root `/workspaces/TerraScale/AGENTS.md` Git policy applies in full:

- **Before work:** `git fetch` + `git pull --ff-only origin develop`.
- **Default:** implement on `develop`, **commit often** on substantial coherent changes, and **push directly to `origin/develop`**. Do not open pull requests for day-to-day work.
- **Do not** create feature branches or worktrees unless the user explicitly asks; when used, branch and worktree are always paired, and finished work lands back on `develop`.
- Pushes to `develop` intentionally run no Forgejo automation.
- `main` is for release/promotion only. Only the `develop` → `main` promotion path may build and publish the immutable major-version release candidate.
