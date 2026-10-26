# Changelog

## 0.0.16 — remove retired SDK runtimes

Delete `./local`, `./postgres`, `./schema`, `./sql`, `./tanstack` and the
`terrascale-local` CLI, including their runtime sources, legacy bindings and
unused peers. React/Svelte retain generic lifecycle utilities; their retired
`createDatabaseQuery` binding is removed. Current root v1 schema/SQL and document
codegen remain available. This is the authorized greenfield removal at 0.0.X.

## 0.0.15 — hosted request cancellation

Hosted identity and OIDC methods accept an optional final request-options argument
with `signal`. Existing list options retain their signal field. Cancellation stays
a transport error value, with `request_aborted` for caller cancellation and
`request_timeout` for the SDK deadline; ordinary network failures retain
`transport_error`. Cancellation details preserve a sanitized cause.

## 0.0.14 — remove the planned Better Auth stub

The package no longer exports `./better-auth` or ships its always-throwing stub.
The server-only database adapter is planned after the replication and public auth
primitive work; it is not an available SDK capability. Management and administration
operations remain available through their named entrypoints.

## 0.0.13 — current import contracts

Imports support the published NDJSON format and collection option. Job and preview
decoders match the current producer fields, with generated ULIDs for imported
documents; retired missing-primary-key and id-policy fields are removed. Existing
PostgreSQL/SQLite import operations and explicit preview confirmation are preserved.

## 0.0.12 — public management and administration

`./management` exposes project-scoped database, placement, backup/import, usage,
index and schema operations through the existing root client, plus a separate
platform sign-in client for organization, project, member, key, invitation,
ownership, SSO, MFA, audit and account operations. Receipt-based deletion status
and backend end-user assertions use separate credential boundaries.
`./admin` exposes role-protected platform exploration. Deprecated `require*`
aliases now construct clients with explicit credentials. Schema preview supports
bounded continuation and redacted diagnostics. The capability inventory tracks
remaining producer contracts; the server-only Better Auth adapter remains held
on public auth primitives and durable mutation receipts.

## 0.0.11 — read replica isolation and freshness

Persisted shape replicas bind to their service, project, database, collection and
filter. Authorization denial clears local documents and freshness; transient
disconnection marks the view stale while retrying the same checkpoint. Closing
a subscription settles even when a replica store never finishes loading or saving.

## 0.0.10 — production contract fixes

The named hosted-auth token operation accepts explicit project API-key grants
without a resource parameter. Public health probes accept omitted private build
metadata. Hosted-auth operations return authentication denials without replaying
requests with a changed credential. README qualification commands follow the
binary-free HTTP test policy and document the existing TypeScript model tooling.

## 0.0.9 — declared JSON Schema models

Node tooling at `@terrascale/sdk/codegen` generates document, create and
merge-patch body types from `collection.getSchema()` or a schema file. Install
the exact tooling peer `json-schema-to-typescript@16.0.0`; the `terrascale-models`
command is separate from browser/runtime entrypoints. Unsupported constructs
produce JSON Pointer diagnostics. Runtime validation remains authoritative.

## 0.0.7 — live shape reconnect, browser bootstrap and read replica

`subscribeShape` keeps its state and retries the same position after a lost
connection, follows the bootstrap `307` in browsers, and accepts
`replica: { load, save }` to restore its last resumable state across restarts
and offline periods. `indexedDbShapeReplica(key)` stores it in IndexedDB.
Writes are never queued locally. Also publishes the query aggregates and geo
filters below.

## Unreleased — query aggregates and geo filters

`query({ aggregate: { groupBy, sum, avg, min, max } })` returns typed
`groups` (`{ key, count, sum?, avg?, min?, max? }`) with empty `items`.
`v1Filter.withinRadius(path, { lat, lon, meters })` and
`v1Filter.withinBox(path, { south, west, north, east })` build `within`
filters, and `sort: [{ near: { path, lat, lon } }]` orders by meters, returned
as each item's `distance`.

## 0.0.6 — relational SQL

`db.execute(sql, args)`, `db.query(sql, args)` and `db.batch(statements)` call
TerraBase's `POST /v1/projects/{project}/databases/{database}/sql/execute`.
Each request is one transaction. Results are typed
`{ columns, rows, rowsAffected, lastInsertRowid }`; a refused statement throws
`SqlError` (a `TerraBaseV1Error`) with `statement` and `sqliteCode`. The
read-only `db.sql` route is unchanged.

## Unreleased — collection TTL and vector near sort

`collection.putTtl(path)`, `getTtl()` and `deleteTtl()` call
`PUT/GET/DELETE .../collections/{collection}/ttl` and return `{ path }`
(`null` when unset). `query({ sort: [{ near: { path, vector, metric } }] })`
sorts by vector distance and each item carries `distance`.

## Unreleased — database export

`db.export({ after, signal })` streams TerraBase's
`GET /v1/projects/{project}/databases/{database}/export` NDJSON as an async
iterable of `{ collection, id, document }`. `after: { collection, id }` resumes
after that document. Error answers raise the usual `TerraBaseV1Error`.

## 0.0.4

Publishes current main, including `db.sql`, `db.changes`, backups, indexes and
`allRegions` placement, plus every Unreleased entry below. Releases are now
tag-driven: pushing a `v0.0.N` tag that matches `package.json` runs
`.github/workflows/publish.yml`, which re-runs the checks and publishes to npm
with provenance.

## Unreleased — ts-auth platform token contract

`createTsAuthClient` sends every call to `apiBaseUrl`. The Atlas auth discovery
lookup, regional `<region>.auth.terrascale.tech` routing and the
`useReverseProxy` option are removed: ts-auth has one global issuer per tenant
(`https://auth.terrascale.tech/terrascale` for the platform). Contract tests
pin the issuer, the `https://api.terrascale.tech` audience, the four TerraBase
scopes and distinct 401/403 handling.

## Unreleased — TS-754

Entity IDs are plain uppercase Crockford ULIDs. `generateId()` uses the maintained
`ulid` library with monotonic generation; `isValidId()` and `requireId()` validate
the canonical 26-character, 128-bit form. UUIDs, prefixes, lowercase and legacy
keys fail validation. Project IDs, document requests/responses, snapshot keys,
hosted identity DTOs and explicit development storage use this contract.

The document key is the entity ID; document JSON and patches cannot duplicate it
in a top-level `id` field. The deterministic UUIDv5 `documentIdFor` helper is
removed. Store natural keys in indexed fields. Local Better Auth generates ULIDs
and stores the ID only in the record key, reconstructing its adapter view on read.

This is a greenfield cutover: there is no migration, ID rewrite, compatibility
shim or legacy fallback. Service owners wipe their verified affected stores and
recreate bootstrap state; this stateless SDK has no production data to delete.
Release qualification requires the matching immutable C# ULID producer release.

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
