AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!
# TerraScale TypeScript SDK rules

This is the SDK to interact with TerraScale and TerraBase apis

## Repository role

This repository owns the public `@terrascale/sdk` package

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
- Reactive changes require generated schema, byte vectors, and the shared Rust,
  TypeScript, and .NET semantic corpus.
- Better Auth changes require exact-version adapter tests, Region concurrency,
  migration, retry, unique, one-time consume, revocation, and denial cases.
- Dashboard integration evidence uses the fixed DevHost routes. Authentication
  remains on `18445` and Dashboard on `18446`.
- The new Database API route must be fixed in the workspace and DevHost
  contracts before live tests depend on it.

When live configuration is unavailable, report the gate as skipped. Do not
substitute a fake service or fabricated evidence.

## Git and collaboration

- When the human asks for a commit, commit and push exactly as asked — do not
  run tests, checks, or builds first.
