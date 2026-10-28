AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!
# TerraScale TypeScript SDK rules

## Repository role and versioning

- This repository owns the public `terrascale` package.
- Per the owner's 2026-10-05 decision, `package.json` must match `^0\.0\.[0-9]+$` until the owner lifts this. Keep `package-lock.json` in sync.

## C# TerraBase, Atlas and Ouroboros

- `terrabase` origin/main (C#/.NET) is the sole database producer. Use project-scoped `/v1/projects/{project}/databases/{db}` documents, query, transaction and collection shape routes with Bearer `tb_` keys and the producer's named scopes.
- The root API is v1 only. Preserve explicit project scope and authorized tokens.
- Automatic eligible-region discovery is the operating contract. The current SDK still requires `baseUrl` or `endpointResolver` until discovery ships. Document that gap without making manual region pinning a first-party setup step. Static origins belong to reviewed isolated tests, never an admission bypass.
- Ouroboros: TerraScale services use the same production TerraBase as customers. Boundary tests run a real immutable C# release binary pinned by version and SHA-256. Fake servers and local SDK SQLite are not producer qualification.

## Legacy surfaces

- Legacy: retired Rust tenant/shard routes, `tbk1` keys, frozen Rust contract inputs, Loro payloads, and the server-ack-only sync v2 corpus.
- The named `local` SQLite and `postgres` entrypoints are deprecated legacy support. The existing exported runtime stays inventoried for removal.
- Do not extend legacy surfaces, document the activation of the exported runtime, or cite legacy material as current conformance or producer evidence. Deprecation does not change the real producer qualification gates.

## Local CPU priority

- Locally, run every build, test, lint, and benchmark command at low priority with `nice -n 19`, for example `nice -n 19 npm test`. Local dev servers and watchers count too.
- In CI (`CI=true`), use full CPU priority. Never add `nice` or `renice` to CI workflows, Makefiles, scripts, or Containerfiles; whoever runs the command locally applies the low priority.

## Service ownership

- Dashboard is one full-stack Vinext TypeScript app. It owns Better Auth, Organizations, Projects, Users, Payments, HTTP MCP, and public product routes.
- `TerraScale.Database` is a separate C#/.NET 10 ASP.NET Core service. It owns public database, query, placement, backup, usage, and reactive routes and is the sole TBEP gateway.
- Dashboard uses this SDK exactly as an external customer does: no private entrypoint, header, identity, tenant bypass, direct database access, or in-process transport.
- The SDK may present one customer-facing base URL; edge routing sends typed paths to the owning service. One public origin does not merge runtime ownership.

## Entrypoints

- `root`: only the project-scoped C# TerraBase v1 API.
- `admin`: typed, role-protected public administration.
- `better-auth`: named and server-only. It contains the Better Auth database adapter and must be excluded from browser bundles.
- Do not add Dashboard-named or generic request entrypoints.

## Better Auth adapter

The public, supported adapter implements the pinned Better Auth adapter contract through named, typed Database API operations only. It must:

- run only on the server and keep credentials out of browser output;
- use named unique insert, compare-and-swap, one-time consume, pagination, bounded transaction, index, and migration operations;
- preserve Better Auth transaction and error semantics;
- use explicit idempotency for retryable mutations;
- pass maintained Better Auth adapter tests when available;
- pin supported core and plugin versions; and
- publish compatibility and license evidence.

It cannot use raw `fetch`, a generic SDK request, direct TerraBase, TBEP, Dashboard private routes, or an in-process shortcut.

## Reactive client

- Every durable mutation reaches the server. No offline durable writes, offline mutation queue, local CRDT actors, or later merge.
- A connected client may apply a reversible optimistic presentation tied to one stable idempotency identity.
- Clients subscribe to typed, bounded TQL queries and receive authorized result snapshots plus insert, update, patch, remove, move, or reset operations.
- Public checkpoints are opaque authorized-view positions, not Loro frontiers or Region replication vectors.
- Realtime WebSocket delivery is the fast path. Checkpoint resume and snapshot bootstrap are the correctness path.
- Required state-machine cases: permission change, auth expiry, Region movement, slow consumer, reconnect, and expired history.

## Public contracts

- Contract-first: the owning service publishes the versioned REST, WebSocket, or SSE schema before the SDK operation lands.
- For each operation, verify: method or protocol message identity; versioned path or endpoint; parameter name, location, and requiredness; request and response media type; field names, types, and nullability; security scheme and scope; success, error, partial outcome, and retry semantics; idempotency; pagination or checkpoint behavior; and route-manifest and generated-contract parity.
- Do not invent an SDK-only route, DTO, header, protocol frame, or server behavior. A typed build without contract parity is not evidence.
- Transport stays typed: no raw `fetch`, arbitrary paths, untyped JSON casts, generic request helpers, custom headers, or consumer-specific escape hatches for TerraScale data.

## Retry and compatibility

- Retry only documented transient failures that are safe to repeat. Mutations need a stable idempotency contract before retry.
- Respect cancellation, deadlines, `Retry-After`, and server overload.
- Never retry validation, authentication, authorization, or deterministic conflict failures automatically.
- Preserve supported public compatibility. Breaking behavior needs a versioned contract and release plan. A direct backend cutover does not authorize breaking all `/api/v1` routes.

## Testing

- Use the repository's native TypeScript tools. No mocks for TerraScale product boundaries.
- Fake and local SDK stores, demo stores, and skip-auth exist only in test code and test binaries. Never ship them as a development or deployed runtime mode.
- Keep real API tests environment-gated. Never commit credentials, tokens, cookies, `.env` files, or real tenant data. Never publish private topology or service connection details.
- REST changes require OpenAPI and route-manifest drift checks.
- Reactive changes require generated schema, byte vectors, and the current C# producer/TypeScript semantic corpus.
- Better Auth changes require exact-version adapter tests, Region concurrency, migration, retry, unique, one-time consume, revocation, and denial cases.
- Dashboard integration evidence must use authorized, isolated real-service allocations and published contracts before live tests depend on a route.
- When live configuration is unavailable, report the gate as skipped. Do not substitute a fake service or fabricated evidence.

## Git and collaboration

- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]`, and no similar generated-by notices.
- When the human asks for a commit, commit and push exactly as asked. Do not run tests, checks, or builds first.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles, or kill switches in code, config, or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird membership admits a node automatically; it discovers every region, infers its own region from where it runs, and serves without a per-region enable step.
- OCI Vault creates, delivers, and configures every first-party production secret. Vendor keys (such as Stripe and Cloudflare) are stored once in Vault and picked up automatically. Nobody copies secrets by hand.
- Values have one correct setting in code. Expose no tuning knobs.
- CI rejects new switch-style configuration. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

Customer credentials authorize access; service secrets belong in OCI Vault and must never ship in a client bundle.

This section is the operating contract, not evidence that every runtime flow has shipped. See [README.md](README.md) for current SDK/qualification gaps. Existing production switches are inventoried for their owning implementation work; documentation changes must not invent APIs or bypass authorization, tenant scope, registered callbacks, payment consent, or safety guards.
