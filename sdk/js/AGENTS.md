# ts-js

Read the root [AGENTS.md](../../AGENTS.md).

## Layout and checks

Run area commands from `sdk/js/` in the monorepo.

- Public package: `@terrascale/sdk`. Keep `package.json` at `0.0.X` and `package-lock.json` in sync until the owner lifts this restriction.
- Source is JavaScript ES modules with JSDoc in `src/`; `npm run build` generates TypeScript declarations in `types/`. Use native Node/npm tools; do not rewrite to TypeScript.
- Use Node 22 or 24 and the npm version in `packageManager`. Install with `npm ci`.
- Build: `npm run build`; static checks: `npm run typecheck` and `npm run check:v1-route-manifest`. From the repository root: `make sdk-js-check`.
- Current exports are root, `admin`, `management`, `ts-auth`, `react` and `svelte`. Keep credentials and server-only operations out of browser bundles. Do not add Dashboard-named or generic request entrypoints.

## Public API

- The C# TerraBase producer owns the project-scoped v1 database API. Preserve `/v1/projects/{project}/databases/{db}` scope, Bearer `tb_` keys and named producer scopes.
- Land the owning service's versioned REST, WebSocket or SSE contract first. Verify method/message, endpoint, operation identity, parameters, media types, field types/nullability, security/scope, success/error/partial outcomes, retry/idempotency, pagination/checkpoints and generated contract/route parity. A typed build alone proves no parity.
- Never invent routes, DTOs, headers, protocol frames or server behavior. Keep TerraScale data transport typed: no arbitrary paths, untyped JSON casts, generic requests, custom headers or consumer-specific escape hatches.
- Preserve supported public compatibility; breaking behavior needs a versioned contract and release plan. Backend cutovers do not authorize breaking `/api/v1` routes.
- Consumers use public operations with customer authorization: no private entrypoints, tenant bypass, direct database access or in-process transport.
- Do not extend retired Rust tenant/shard routes, `tbk1` keys, frozen Rust inputs, Loro payloads or the server-ack-only sync v2 corpus.

## Reactive, retries and evidence

- Durable mutations reach the server; no offline durable writes, mutation queue, local CRDT actors or deferred merge. Reversible optimistic presentation uses one stable idempotency identity.
- Subscribe to typed, bounded TQL queries with authorized snapshots and insert/update/patch/remove/move/reset operations. Checkpoints are opaque authorized-view positions, not Loro frontiers or replication vectors.
- WebSocket delivery is the fast path; checkpoint resume and snapshot bootstrap establish correctness. Cover permission changes, auth expiry, region movement, slow consumers, reconnect and expired history.
- Retry only documented transient failures safe to repeat. Mutations need stable idempotency. Respect cancellation, deadlines, `Retry-After` and overload; never auto-retry validation, authentication, authorization or deterministic conflicts.
- REST changes require OpenAPI/route-manifest drift checks; reactive changes require generated schema, byte vectors and the current C# producer/JS semantic corpus. Report unavailable live gates as skipped; never fabricate evidence.
- `README.md` records implementation and qualification gaps; do not claim unshipped behavior.
