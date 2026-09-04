AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!
# TerraScale .NET SDK rules

Read the workspace `../GOAL.md` and root `../AGENTS.md` before changing a
public contract.

## Repository role

This repository owns the official .NET 10 customer SDK and the separate
`TerraScale.EntityFrameworkCore` package. It includes:

- named typed customer and administration operations;
- the public .NET reactive client and local result cache;
- reversible optimistic presentation for online mutations;
- the native `terrascale.sync.v2` implementation and conformance harness; and
- safe `HttpClient` and dependency-injection integration.

It does not own a server, Dashboard business logic, identity policy, payment
state, TerraBase adapter, TBEP client, NATS client, CLI, or control-plane
operation.

## Service boundaries

- Dashboard owns Better Auth, Organizations, Projects, Users, Payments, HTTP
  MCP, and those public product routes.
- The independent C#/.NET 10 `TerraScale.Database` ASP.NET Core service owns
  public database and reactive routes and is the sole QUIC TBEP gateway.
- Public clients may use one customer API origin while edge routing selects the
  owning runtime.
- This SDK treats both services as external versioned public APIs.

Do not reference Dashboard, Database API, Authentication, TerraBase, or other
server projects. Do not open TerraBase sessions or use private service
contracts.

`TerraScale.Database` may use named `TerraScale.DotNet` operations for
Dashboard authorization where standard maintained .NET OIDC or OAuth handlers
do not apply. That use does not make this SDK an internal client and does not
permit a raw request escape hatch.

## Public contract work

Land the owning public REST, WebSocket, or SSE contract before the SDK
operation. Do not invent a route, DTO, header, field, error, or server behavior.

Verify:

- method or protocol message;
- versioned path or endpoint;
- operation identity;
- parameters and requiredness;
- media types;
- field names, types, and nullability;
- security scheme and scope;
- success, error, and partial outcome;
- idempotency and retry;
- pagination or checkpoint semantics; and
- generated contract and route-manifest parity.

Use `HttpClient` and source-generated `System.Text.Json` metadata. No
`Newtonsoft.Json`, reflection workaround, generic request method, arbitrary
path, or arbitrary header.

Return typed immutable models. Preserve safe request IDs, retry guidance,
pagination, routing, rate limits, and response metadata.

## Reactive client

- Every durable mutation reaches the server.
- Do not add offline durable writes, an offline queue, local CRDT actors, or
  later merge.
- A connected client may apply reversible optimistic presentation with one
  stable idempotency identity.
- Clients subscribe to bounded typed TQL queries and receive authorized result
  snapshots, insert, update, patch, remove, move, or reset.
- Checkpoints are opaque authorized-view positions, not Loro payloads or Region
  replication vectors.
- Realtime WebSocket delivery is the fast path. Resume and snapshot bootstrap
  are the correctness path.
- Auth expiry, permission change, Region movement, slow consumer, reconnect,
  and history expiry are required state-machine cases.
- The old Loro-era server-ack-only v2 client and corpus are migration evidence,
  not the current target.

Rust, TypeScript, and .NET must pass one semantic corpus and generated
MessagePack vectors before current v2 conformance is claimed.

## Entity Framework Core

EF Core support stays in `src/TerraScale.EntityFrameworkCore`. It uses this
SDK and public Database API operations. It cannot reference TerraBase or server
projects.

Expose async, cancellation-aware `DbContext` integration. Map EF model
metadata to public database, environment, repository, key, schema, and query
contracts. Keep provider tests separate from base SDK tests.

## Async, retry, and resources

- APIs are asynchronous and cancellation-aware.
- Never block with `.Result`, `.Wait()`, or sync-over-async.
- SDK clients are thread-safe after construction.
- Do not serialize independent caller work without a correctness reason.
- Use bounded concurrency and backpressure for batches and pagination.
- Use `IAsyncEnumerable<T>` for streaming results.
- Never dispose a caller-owned `HttpClient`.
- Retry only documented transient failures that are safe to repeat.
- Mutations require stable idempotency before retry.
- Respect deadlines, cancellation, `Retry-After`, and overload.
- Do not retry validation, authentication, authorization, or deterministic
  conflict failures automatically.

## Developer experience

- Use discoverable TerraScale-specific names and immutable records.
- Validate options at construction with actionable errors.
- Keep timeouts, retry, concurrency, and buffer limits explicit.
- Provide XML documentation for public APIs.
- Enable nullable reference types, analyzers, and warnings as errors.
- Do not emit console output or use process-wide mutable state.
- Hidden background work is forbidden unless an explicitly owned reactive
  object starts and disposes it.

## Testing

- Use TUnit and await every assertion.
- Product integration and end-to-end tests target real public services. Do not
  replace them with mocks, fakes, direct server references, or in-memory
  substitutes.
- Keep unavailable live tests environment-gated and report them as skipped.
- Every public operation needs success and expected-failure coverage.
- Reactive work needs generated schema checks, semantic conformance, connection
  drop, duplicate, reorder, auth expiry, permission change, Region movement,
  slow consumer, and tenant isolation cases.
- Build with all CPU cores.
- Preserve secrets and real tenant data through environment configuration and
  redacted output.

The fixed DevHost URLs for Better Auth and Dashboard remain `18445` and
`18446`. The new Database API needs a fixed route before live SDK tests depend
on it.

## Git and collaboration

- Before the first write on `develop`, fetch and fast-forward from
  `origin/develop`.
- Claim exact paths, inspect status before every write and stage, and preserve
  unrelated work.
- Stage named paths only.
- Commit coherent changes often and push directly to `origin/develop`.
- When the human asks for a commit, commit and push exactly as asked — do not
  run tests, checks, or builds first.
- Fetch before push and stop if the remote moved.
- Do not create a feature branch, worktree, pull request, release, deployment,
  or live external mutation unless explicitly requested.
- Keep credentials, tokens, private endpoints, raw remote errors, customer
  payloads, and unredacted logs out of source, tests, output, commits, and
  reports.

## Agent skills

See `docs/agents/domain.md`.
