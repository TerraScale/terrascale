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
- Concurrent writers use explicit file ownership and explicit `git add` paths;
  the integration owner alone resolves shared export or generated-file conflicts.
- DevHost integration work requires an exclusive fixture lease covering its
  tenant, project, database, and credentials; record only redacted fixture
  identifiers in coordination and handoff notes.
- Implement public capabilities contract-first. For REST, land the Platform
  public REST contract, route manifest, and OpenAPI surface first; for public
  realtime APIs, land the versioned WebSocket or SSE protocol and schema
  contract first. Then add the matching typed SDK DTOs and operation, followed
  by consumers, docs, and adapters. Never invent an SDK-only route, protocol,
  or contract to unblock a consumer.
- Every typed public SDK operation needs proof at the public boundary. For REST,
  verify its OpenAPI and route-manifest mapping have not drifted; for WebSocket
  or SSE, verify the versioned protocol, event/message schemas, handshake or
  subscription semantics, and compatibility behavior have not drifted. Show
  DevHost live success and expected-failure evidence for its
  authorization/validation path when the required environment is available.
- Treat the SDK operation and its public contract as one unit. For REST, before
  merge gate exact method, path, parameter location/name/requiredness, request
  and response media types, status/error shapes, operation identity, and DTO
  field/nullability parity against OpenAPI and the route manifest. For
  WebSocket/SSE, gate the versioned endpoint and protocol/schema identity,
  connection/authentication and subscription semantics, event/message and
  error shapes, reconnect/resume behavior where specified, and DTO
  field/nullability parity. Do not represent realtime APIs as REST or accept a
  passing TypeScript build as evidence that either public surface agrees.
- Transport stays typed end-to-end: each public operation must use the SDK's
  typed operation/client path and typed request/response models. Do not add
  raw `fetch`, generic request helpers, stringly route construction, untyped
  JSON casts, arbitrary headers, or per-consumer transport escape hatches.
- MCP adapters may compose and expose only typed SDK operations. Do not add a
  generic request method, arbitrary path/body forwarding, or another escape
  hatch that bypasses the public typed contract.

## Retry, Idempotency, and Compatibility

- Classify each operation before adding automatic retry. Retry only documented
  transient failures for operations that are safe to repeat; respect server
  retry guidance and cancellation/deadline signals. Never retry validation,
  authentication, authorization, or other deterministic client failures.
- Mutations are not implicitly retryable. A retryable create or command needs
  a documented public idempotency contract, a caller-supplied or explicitly
  generated idempotency key with stable reuse for the logical attempt, and
  evidence that duplicate delivery preserves the documented result.
- Preserve compatibility for supported SDK consumers: make additive changes by
  default, keep serialized field names and optionality stable, and avoid
  changing exported behavior, error semantics, or defaults without a versioned
  public API and release plan. Coordinate any removal, rename, or narrowing
  with the Platform contract owner before implementation.

## Testing

- TypeScript SDK tests follow the workspace no-mocks rule (root `AGENTS.md`).
- Integration tests run against DevHost-emitted `TERRASCALE_API_BASE_URL` and
  `TERRASCALE_AUTH_API_BASE_URL` (`src/integration/devhost-smoke.test.ts`) and
  skip when those env vars are unset.
- Keep live tests explicitly environment-gated. Do not commit credentials,
  tokens, `.env` files, captured authorization headers, or real tenant data;
  supply secrets only through the local/CI environment and redact them from
  logs, fixtures, test output, and commits.
- Real-API evidence is conditional, never simulated: when both DevHost base
  URLs and authorized test credentials are supplied by the environment, run
  the affected live success and expected-failure paths and report the command
  and redacted result. When any required variable is absent, keep the tests
  skipped, state that live evidence was unavailable, and do not substitute
  mocks, fixed secrets, local `.env` loading, or fabricated proof.
- Contract-drift checks are release gates for changed public operations. Run
  the repository's applicable generation/verification and type/test commands;
  inspect the REST OpenAPI/route-manifest or realtime versioned
  protocol/schema contract, as applicable, and the exported SDK diff. A
  mismatch, unexplained generated-file change, or missing public-boundary
  evidence blocks handoff until resolved or explicitly accepted by the
  contract owner.

## Branch and Git Workflow

Workspace root `/workspaces/TerraScale/AGENTS.md` Git policy applies in full:

- **Before work:** `git fetch` + `git pull --ff-only origin develop`.
- **Default:** implement on `develop`, **commit often** on substantial coherent changes, and **push directly to `origin/develop`**. Do not open pull requests for day-to-day work.
- **Do not** create feature branches or worktrees unless the user explicitly asks; when used, branch and worktree are always paired, and finished work lands back on `develop`.
- Pushes to `develop` intentionally run no Forgejo automation.
- `main` is for release/promotion only. Only the `develop` → `main` promotion path may build and publish the immutable major-version release candidate.
- Before handoff, inspect the exact owned-path diff and `git status`; run the
  relevant checks, then `git fetch origin`. If `origin/develop` moved, stop and
  coordinate before any push. Report the commit, validation, owned paths, and
  only redacted fixture/credential details.
- Stage named owned files only (for example, `git add -- AGENTS.md`); never
  use broad staging commands. Before requesting review or root validation,
  hand off the owned files, exact diff/commit, OpenAPI and route-manifest
  parity result for REST or versioned realtime protocol/schema compatibility
  result for WebSocket/SSE, compatibility/retry decision, commands and
  results, live evidence status, and known gaps. The reviewer/root validator
  must be able to confirm the public contract without secrets or access to
  unredacted tenant data.
