AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!
# TerraScale TypeScript SDK rules

## TS-498: C# TerraBase, Atlas and Ouroboros

- `terrabase` origin/main (C#/.NET) is the sole database producer. Use project-scoped
  `/v1/projects/{project}/databases/{db}` documents, query, transactions and collection
  shape routes with Bearer `tb_` keys and the producer's named scopes. The retired
  Rust tenant/shard routes, `tbk1` keys and frozen Rust contract inputs are legacy.
- The root API is v1 only. Preserve explicit project scope and authorized tokens.
  Automatic eligible-region discovery is the operating contract; the current
  SDK still requires `baseUrl` or `endpointResolver` pending TS-507. Document
  that gap without making manual region pinning a first-party setup step.
  Static origins belong to reviewed isolated tests, never an admission bypass.
- Ouroboros: TerraScale services use the same production TerraBase as customers.
  Boundary tests run a real immutable C# release binary pinned by version and
  SHA-256; fake servers and local SDK SQLite are not producer qualification.

## Deprecated SDK SQLite

The named `local` SQLite and `postgres` entrypoints are deprecated legacy support.
Fake/local SDK stores belong only in test code and test binaries, not a shipped
development or deployed runtime mode. The existing exported runtime remains
inventoried for removal; do not document its activation or treat it as producer
qualification. This rule does not change the real producer qualification gates.

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Repository role

This repository owns the public `terrascale` package

## Version convention

Per the owner's 2026-10-05 decision (TS-528), the `package.json` version must
match `^0\.0\.[0-9]+$` until the owner explicitly lifts this policy. Keep
`package-lock.json` versions in sync with `package.json`.

## Service ownership

- Dashboard is one full-stack Vinext TypeScript app. It owns Better Auth,
  Organizations, Projects, Users, Payments, HTTP MCP, and those public product
  routes.
- `TerraScale.Database` is a separate C#/.NET 10 ASP.NET Core service. It owns
  public database, query, placement, backup, usage, and reactive routes and is
  the sole TBEP gateway.
- Dashboard uses this SDK exactly as an external customer does. It receives no
  private entrypoint, header, identity, tenant bypass, direct database access,
  or in-process transport.

The SDK may present one customer-facing base URL while edge routing sends typed
paths to their owning service. One public origin does not merge runtime
ownership.

## Entrypoints

- The root entrypoint contains only the project-scoped C# TerraBase v1 API.
- The `admin` entrypoint contains typed role-protected public administration.
- A named server-only `better-auth` entrypoint contains the Better Auth
  database adapter and must be excluded from browser bundles.
- Fake/local stores must be confined to test code and test binaries; the
  currently exported legacy runtime remains an implementation removal gap.
- Do not add Dashboard-named or generic request entrypoints.

## Better Auth adapter

The adapter is public and supported for Dashboard and customer applications.
It implements the pinned Better Auth adapter contract through named typed
Database API operations only.

It must:

- run only on the server;
- prevent credentials from entering browser output;
- use named unique insert, compare-and-swap, one-time consume, pagination,
  bounded transaction, index, and migration operations;
- preserve Better Auth transaction and error semantics;
- use explicit idempotency for retryable mutations;
- pass maintained Better Auth adapter tests when available;
- pin supported core and plugin versions; and
- publish compatibility and license evidence.

It cannot use raw `fetch`, a generic SDK request, direct TerraBase, TBEP,
Dashboard private routes, or an in-process shortcut.

## Reactive client

- Every durable mutation reaches the server. Do not add offline durable writes,
  an offline mutation queue, local CRDT actors, or later merge.
- A connected client may apply a reversible optimistic presentation associated
  with one stable idempotency identity.
- Clients subscribe to typed bounded TQL queries and receive authorized result
  snapshots, insert, update, patch, remove, move, or reset operations.
- Public checkpoints are opaque authorized-view positions. They are not Loro
  frontiers or Region replication vectors.
- Realtime WebSocket delivery is the fast path. Checkpoint resume and snapshot
  bootstrap are the correctness path.
- Permission change, auth expiry, Region movement, slow consumer, reconnect,
  and expired history are required state-machine cases.
- Loro payloads and the old server-ack-only sync v2 corpus are legacy and must
  not be extended as current conformance.

## Public contracts

Implement contract-first. The owning service publishes the versioned REST,
WebSocket, or SSE schema before the SDK operation lands.

For each operation verify:

- method or protocol message identity;
- versioned path or endpoint;
- parameter name, location, and requiredness;
- request and response media type;
- field names, types, and nullability;
- security scheme and scope;
- success, error, partial outcome, and retry semantics;
- idempotency behavior;
- pagination or checkpoint behavior; and
- route-manifest and generated-contract parity.

Do not invent an SDK-only route, DTO, header, protocol frame, or server
behavior. A typed build without contract parity is not evidence.

Transport stays typed. Do not add raw `fetch`, arbitrary paths, untyped JSON
casts, generic request helpers, custom headers, or consumer-specific escape
hatches for TerraScale data.

## Retry and compatibility

- Retry only documented transient failures that are safe to repeat.
- Mutations require a stable idempotency contract before retry.
- Respect cancellation, deadlines, `Retry-After`, and server overload.
- Never retry validation, authentication, authorization, or deterministic
  conflict failures automatically.
- Preserve supported public compatibility. Breaking behavior needs a versioned
  contract and release plan.
- A direct backend cutover does not authorize breaking all `/api/v1` routes.

## Testing

- Use the repository's native TypeScript tools and no mocks for TerraScale
  product boundaries.
- Keep real API tests environment-gated. Never commit credentials, tokens,
  cookies, `.env` files, or real tenant data.
- REST changes require OpenAPI and route-manifest drift checks.
- Reactive changes require generated schema, byte vectors, and the current C#
  producer/TypeScript semantic corpus; frozen Rust inputs are legacy evidence.
- Better Auth changes require exact-version adapter tests, Region concurrency,
  migration, retry, unique, one-time consume, revocation, and denial cases.
- Dashboard integration evidence must use authorized isolated real-service
  allocations and published contracts before live tests depend on a route.
  Never publish private topology or service connection details.

When live configuration is unavailable, report the gate as skipped. Do not
substitute a fake service or fabricated evidence.

## Git and collaboration

- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` or similar generated-by notices.
- When the human asks for a commit, commit and push exactly as asked — do not
  run tests, checks, or builds first.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles or kill switches in code, config or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird network membership admits a node automatically; it discovers every region, infers its own region from where it runs and starts serving without a per-region enable step.
- OCI Vault automatically creates, delivers and configures every first-party production secret. Vendor-issued keys (such as Stripe and Cloudflare) are stored once in Vault and picked up automatically; nobody copies secrets by hand.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration; ts-reviewer requests changes. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

The operating contract requires automatic public regional endpoint discovery. Customer credentials authorize access; service secrets belong in OCI Vault and must never ship in a client bundle.

This section is the TS-795 operating contract, not evidence that every runtime
flow has shipped. See [README.md](README.md) for current SDK/qualification gaps.
Existing production switches are inventoried for their owning implementation
threads; documentation changes must not invent APIs or bypass authorization,
tenant scope, registered callbacks, payment consent, or safety guards.
