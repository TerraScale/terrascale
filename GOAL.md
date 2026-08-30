# TerraScale.TypeScript production goal

Status: pre-production public SDK plan. Last reviewed: 2026-08-29.

The workspace [`../GOAL.md`](../GOAL.md) is the product authority. This file
defines the work required for `@terrascale/sdk`, its server-only Better Auth
adapter, public reactive client, and explicitly local SQLite runtime. A green
TypeScript build or local emulator does not qualify a customer release.

## Mission and boundary

Deliver a typed browser and Node SDK for the public Dashboard and
independently deployable `TerraScale.Database` service contracts. The
`TerraScale.Database` service source lives permanently in
`TerraScale.Internals`; this package consumes its external public API and does
not become its source repository. The package owns named customer and
administration operations, route and OpenAPI parity checks, the public
reactive result client, reversible online optimistic presentation, safe retry
and cancellation behavior, and the server-only Better Auth adapter.

The SDK never speaks TBEP, opens a TerraBase session, uses NATS, calls private
Dashboard routes, or supplies a generic request escape hatch. Dashboard uses
this package exactly as an external customer application does. The `local`
entrypoint is an explicit development runtime only.

## Current repository truth

The package publishes root and `admin` entrypoints, a Node-only
`better-auth` entrypoint, and an explicit `local` SQLite entrypoint. Route
capture, OpenAPI descriptor, forbidden-surface, generated manifest, contract,
MessagePack, and browser tests exist. The current source includes legacy
`terrascale.sync.v1` and a Loro-era v2 client/corpus. The generated MessagePack
module and result-view state machine are migration evidence until the public
Database API transport is published and qualified.

The local runtime is useful for fast Dashboard development and semantic tests.
It cannot prove multi-Region durability, authorization, backup, usage,
reactive cross-language behavior, external providers, or performance. Live
tests skip when configuration is unavailable and must not substitute mocks or
fabricated evidence.

## Production target

Every SDK operation maps to an owner-published versioned REST, WebSocket, or
SSE contract with exact method, path, operation identity, parameters, media
types, types, nullability, security scopes, limits, errors, idempotency,
retry, pagination, checkpoints, and cancellation semantics. The generated
route manifest and OpenAPI descriptor must agree with the server contract.

The current reactive target uses bounded typed TQL result views, authorized
snapshots and result operations, opaque view checkpoints, and a server-backed
mutation path. It handles reconnect, auth expiry, permission loss, Region
movement, history expiry, duplicate or reordered frames, slow consumers, and
backpressure. Offline durable writes, local CRDT actors, and mutation queues
remain forbidden.

## Non-goals

- A server, Dashboard business logic, identity authority, payment provider,
  TerraBase adapter, TBEP client, or NATS client.
- Generic HTTP requests, arbitrary paths, untyped JSON, arbitrary headers,
  private service routes, or tenant bypasses.
- SQLite in production, CI qualification, staging, or deployed environments.
- Offline durable writes, offline mutation queues, local CRDT replication, or
  later merge.
- Treating route fixtures, local SQLite, loopback services, or skipped live
  tests as production evidence.

## Gap register

| ID | Priority | Required outcome | Evidence |
| --- | --- | --- | --- |
| TS-P0-01 | P0 | Reconcile all captured routes with Dashboard and Database API OpenAPI and operation manifests. | Drift checks, schema digest, route matrix, and expected-failure tests. |
| TS-P0-02 | P0 | Finish generated public MessagePack and reactive transport adoption with bounded hostile decoding. | Shared Rust/.NET/TypeScript vectors, generated diff, malformed-input and live recovery receipts. |
| TS-P0-03 | P0 | Complete server-only Better Auth adapter against named Database API operations. | Exact-version adapter tests, browser-bundle exclusion, live concurrency and revocation evidence. |
| TS-P0-04 | P0 | Enforce typed errors, idempotency, safe retry, cancellation, limits, pagination, and response metadata. | Operation-level success/failure matrix and overload/fault tests. |
| TS-P0-05 | P0 | Qualify the exact npm artifact and supported Node/browser runtimes. | Clean install, tarball digest, source/tree provenance, Node 22/24, browser, license, and security receipt. |
| TS-P1-01 | P1 | Complete named public database, backup, usage, placement, query, and administration operations as server contracts land. | Contract-first operation matrix with auth and partial outcomes. |
| TS-P1-02 | P1 | Run real Dashboard and Database API integration on fixed DevHost routes and production-shaped endpoints. | Environment-gated live authorization, tenant isolation, reconnect, and Region evidence. |
| TS-P1-03 | P1 | Document deprecation and transition for legacy v1 and Loro-era v2 without a second current contract. | Versioned migration and rollback record. |
| TS-P1-04 | P1 | Keep local SQLite explicitly isolated from release and production composition. | Startup guards, package export inspection, and negative configuration tests. |
| TS-P2-01 | P2 | Publish stable XML-equivalent TypeScript docs, examples, compatibility window, and incident guidance. | Reviewed README, release notes, and support matrix. |

## Parallel workstreams

| Stream | Scope | Depends on | Deliverable |
| --- | --- | --- | --- |
| A: public contracts | Route capture, OpenAPI, operation identity, limits, errors, and compatibility. | Dashboard and Database API owners. | Versioned contract and drift receipt. |
| B: reactive protocol | Manifest generation, codecs, result-view engine, session transport, and recovery. | TerraBaseV2, Database API, .NET corpus. | Cross-language conformance receipt. |
| C: Better Auth | Node-only adapter, transactions, uniqueness, one-time consume, migrations, and revocation. | Database API auth contract. | Adapter compatibility receipt. |
| D: client quality | Retry, cancellation, backpressure, pagination, browser support, local runtime, and security scans. | A and stable models. | Unit, browser, and boundary evidence. |
| E: package release | Build, pack, archive inspection, provenance, registry promotion, and rollback. | A through D. | Immutable npm candidate and handoff. |
| F: live qualification | Fixed DevHost and production endpoints, identity, tenant isolation, Region movement, and incidents. | Owning services and safe fixtures. | Live release evidence. |

## Dependency waves

1. Freeze owner contracts, routes, limits, protocol versions, and runtime
   support. Keep unbacked methods omitted.
2. Generate and verify route/OpenAPI/MessagePack artifacts while adapter and
   client quality work proceeds on stable contracts.
3. Integrate the reactive replacement and Better Auth adapter, then run the
   shared semantic corpus and hostile-input tests.
4. Pack the exact artifact and verify it in a clean consumer on supported
   runtimes and browsers.
5. Run live authorization, tenant isolation, reconnect, Region, and recovery
   qualification and promote only the verified tarball.

## Decisions requiring owner input

- Final public API origin and Database API route, including compatibility and
  deprecation windows.
- The owner-published reactive route, MessagePack activation version,
  checkpoint retention, and legacy-client removal date.
- Better Auth core/plugin versions, adapter transaction semantics, revocation,
  and live authorization contract.
- npm package versioning, registry/promotion owner, signing or provenance
  format, Node/browser support window, and rollback policy.

## Acceptance criteria

- `npm ci`, `npm run typecheck`, `npm run build`, `npm test`, and
  `npm run test:browser` pass on the pinned toolchain, with unavailable live
  configuration reported as skipped.
- `npm run check:route-manifest`, OpenAPI drift, contract descriptor,
  public-fixture, and forbidden-surface checks pass, using the actual server
  schemas.
- Root, `admin`, and `better-auth` bundles contain no local SQLite activation,
  secret, credential, private route, or generic request API.
- Every operation has typed request/response/error/security/limit,
  idempotency, retry, pagination, and cancellation evidence.
- Generated MessagePack vectors pass round-trip, bounds, unknown-version,
  malformed, duplicate/reorder, reconnect, reset, and history-expiry tests.
- The Better Auth adapter runs only on Node, preserves maintained adapter
  semantics, and passes exact-version, migration, uniqueness, one-time consume,
  retry, and revocation tests.
- The exact npm tarball passes provenance, digest, contents, dependency,
  license, vulnerability, Node 22/24, and supported-browser checks.
- Live endpoints prove auth, scope, tenant isolation, permission loss, Region
  movement, reconnect, and typed failure behavior.

## Exit gates

1. Contract gate: server schemas, route manifest, OpenAPI descriptor, and
   generated codecs agree.
2. Boundary gate: no generic/private/direct-engine path and local SQLite is
   excluded from production graphs.
3. Behavior gate: typed operations, retry, cancellation, limits, adapter, and
   browser tests pass.
4. Reactive gate: cross-language vectors and live result-view recovery pass.
5. Artifact gate: exact npm package provenance, security, license, and clean
   consumer verification pass.
6. Live gate: identity, authorization, tenant, Region, and incident receipts
   pass on approved endpoints.
7. Workspace gate: service, infrastructure, soak, and GA evidence pass. The
   SDK cannot waive a missing owner-service receipt.

## References

- [`docs/adr/README.md`](docs/adr/README.md)
- [`docs/release.md`](docs/release.md)
- [`../GOAL.md`](../GOAL.md)
