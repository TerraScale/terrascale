# Changelog

## 0.0.1 — TS-498

Per the owner's 2026-10-05 decision (TS-528), 0.0.1 is the latest npm release,
1.0.0 is withdrawn from npm, and 0.3.0 is deprecated. `terrascale` stays on
0.0.x until the owner explicitly lifts this policy.

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
5. `terrascale/local`, `terrascale-local` and `terrascale/postgres` are deprecated
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
