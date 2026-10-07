# Changelog

## Unreleased — TS-592

The project-scoped data client accepts a ts-auth access JWT as `token`, while
retaining legacy C# `tb_` keys for the existing producer release. Header injection
and malformed credentials fail before I/O. TS-589 keeps data paths unchanged;
Management and Admin remain closed and make no retired route calls.
JWT producer qualification awaits the server implementation of `docs/api.md`.

## 0.0.2 — TS-650

TerraBase version tokens are opaque: `tbv1-<32 hex>` and the coming `tbv2-…`
format are both accepted (any 1-256 visible ASCII characters without quotes, so
they stay safe in `If-Match`). Responses with new members no longer throw:
`durability` passes through any label, and documents pass through any `state`,
including a causal read's `"conflict"` with its `siblings` (the LWW winner stays
in `document`). Conditional writes and error handling are unchanged.

## 0.0.1 — TS-498

The package moves from the unscoped `terrascale` npm name, which the owner has
deleted, to `@terrascale/sdk` (TS-537). Install `@terrascale/sdk` and import
from `@terrascale/sdk` and its subpaths, for example `@terrascale/sdk/admin`.
Per the owner's 2026-10-05 decision (TS-528), 0.0.1 is the first `latest`
release and `@terrascale/sdk` stays on 0.0.x until the owner lifts this policy.

### Breaking changes

1. The root entrypoint exposes only the C# TerraBase v1 client API.
   `createTerraBaseClient` now aliases `createTerraBaseV1Client`; the retired
   tenant/shard client, Rust discovery, schema/SQL and reactive-view
   exports are removed from the root. Named legacy entrypoints remain separate.
2. Client configuration requires `project`, a Bearer `tb_` token and exactly one
   of `baseUrl` or `endpointResolver`. Tenant, shard, `apiKey`, `endpoint` and
   `tbk1` routing assumptions no longer apply. Database operations use
   `/v1/projects/{project}/databases/{db}` paths.
3. Mutations follow the C# producer: `Idempotency-Key` is unsupported and writes
   cannot be automatically replayed. Queries do not support `lower` or `explain`;
   server numeric comparison and bounded-count semantics replace Rust assumptions.
4. Shapes use `GET …/collections/{collection}/shape` with `shapes:read`.
   The retired shape gatekeeper, SSE, edge tokens and projection are unavailable.
5. `@terrascale/sdk/local`, `terrascale-local` and `@terrascale/sdk/postgres` are deprecated
   legacy surfaces. SDK SQLite is no longer the required Dashboard backend;
   real C# release binaries replace frozen Rust/fake-server qualification.

### Migration

Configure a provisioned project and `tb_` API key with `documents:read` and/or
`documents:write`, then obtain `client.database(name).collection(name)` handles.
Use `get`, `put`, `create`, `patch`, `delete` and bounded `query`/`pages`/`items`
operations; conditional writes carry `ifMatch` or `ifNoneMatchAny`.
Do not replay uncertain mutations without a producer deduplication contract.

`staticBaseUrlResolver(baseUrl)` supplies an explicit override; a custom resolver
receives `{ project, database }` and is resolved once per database session.
Atlas discovery is deferred to TS-507; no regional hostname is hardcoded.
Ouroboros qualification uses the real immutable C# TerraBase release binary,
pinned by version and SHA-256, rather than an SDK-local substitute.
`provision:terrabase` acquires the verified binary; `test:integration` runs it
with isolated TLS, real tenant provisioning and runtime OpenAPI parity.
The pinned official producer release is a draft after failed fast checks
(terrabase #110). Its verified release asset can run SDK integration without
publication; those results do not establish that producer source checks passed.

The package and lockfile must use version 0.0.1. `lint` reuses the
existing strict TypeScript/JSDoc check; dependencies are unchanged.

Fresh shape bootstrap requires a server runtime; browser manual redirects hide
the bootstrap handle and offset. Browser continuation needs those server-issued positions.
