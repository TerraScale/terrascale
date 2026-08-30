# TerraScale.DotNet production goal

Status: pre-production public SDK plan. Last reviewed: 2026-08-29.

The workspace [`../GOAL.md`](../GOAL.md) defines the product contract. This
file scopes the official .NET 10 SDK and `TerraScale.EntityFrameworkCore`
package, records current implementation limits, and defines the evidence
needed before a package may be promoted. A package build is not a production
qualification.

## Mission and boundary

Deliver a typed, cancellation-aware, thread-safe customer SDK for the public
Dashboard and Database API contracts. The SDK owns named customer and
administration operations, the public reactive result client, reversible
online optimistic presentation, native `terrascale.sync.v2` bindings, safe
`HttpClient` and dependency-injection integration, and the separate EF Core
package.

Dashboard and `TerraScale.Database` are external public services from this
repository's point of view. The SDK never references server projects, opens a
TerraBase session, speaks TBEP, uses NATS, calls private Dashboard routes, or
offers a generic request escape hatch.

## Current repository truth

The source contains named REST clients for the existing product and database
operations, route and model validation, idempotency and retry helpers, EF Core
integration, reactive/session state, and a legacy v1 plus Loro-era v2 sync
implementation. The v2 result engine and transport are intentionally separate
and the checked-in corpus is migration evidence until the owner-published
replacement contract is live.

The package workflow verifies an exact archive, source revision, tree, digest,
and clean consumer. Repository tests use fixtures and environment-gated live
tests. They do not prove live Database API compatibility, authorization,
tenant isolation, cross-language reactive parity, or production publication.

## Production target

Each public method maps to an owner-published versioned REST, WebSocket, or SSE
operation with exact method, path, operation ID, parameters, media types,
types, nullability, security scopes, limits, errors, idempotency, retry,
pagination, checkpoint, and cancellation semantics. Generated contract and
route-manifest parity is mandatory.

The reactive client subscribes to bounded typed TQL result views and processes
authorized snapshots and result operations with opaque view checkpoints.
Durable writes always reach the server. Reconnect, auth expiry, permission
loss, Region movement, history expiry, duplicate/reordered frames, cancellation,
and slow consumers have explicit state-machine behavior.

## Non-goals

- A server, Dashboard runtime, identity provider, billing state machine,
  TerraBase adapter, TBEP client, or NATS client.
- Direct engine or private service access from any package entrypoint.
- A generic request method, arbitrary route, header, untyped JSON, or private
  Dashboard escape hatch.
- Offline durable writes, local CRDT actors, offline mutation queues, or later
  merge. SQLite is not a .NET production contract.
- Treating fixtures, loopback services, or skipped live tests as production
  evidence.

## Gap register

| ID | Priority | Required outcome | Evidence |
| --- | --- | --- | --- |
| DOT-P0-01 | P0 | Reconcile every named SDK operation with current Dashboard and Database API schemas and route manifests. | Contract matrix, generated parity, positive and expected-failure tests. |
| DOT-P0-02 | P0 | Complete the public reactive replacement and remove current-client ambiguity without preserving old payloads as a second production protocol. | Owner schema, generated binding, shared Rust/TypeScript corpus, reconnect and history receipts. |
| DOT-P0-03 | P0 | Enforce limits, cancellation, retry, `Retry-After`, idempotency, resource disposal, and typed error projection for every operation. | Failure matrix, overload tests, and no-sync-over-async review. |
| DOT-P0-04 | P0 | Qualify package provenance and dependency closure for the exact artifact and supported .NET 10 runtime. | Clean-consumer receipt, package digest, source/tree identity, license and vulnerability evidence. |
| DOT-P1-01 | P1 | Complete administration, database, backup, usage, placement, and branch operations as owning public contracts land. | Operation coverage matrix with authorization and partial outcomes. |
| DOT-P1-02 | P1 | Run real public-service integration across GRU/SFO/FRA with tenant isolation, auth expiry, Region movement, reconnect, and failure recovery. | Environment-gated live receipts; missing config remains skipped and visible. |
| DOT-P1-03 | P1 | Keep EF Core mapping and async behavior aligned with the public database, repository, schema, and query contracts. | Dedicated EF tests and package-consumer evidence. |
| DOT-P2-01 | P2 | Publish support, deprecation, compatibility, XML docs, samples, and incident guidance without claiming unsupported behavior. | Reviewed release docs and compatibility record. |

## Parallel workstreams

| Stream | Scope | Depends on | Deliverable |
| --- | --- | --- | --- |
| A: contract parity | Route/model manifests, OpenAPI comparison, errors, limits, and compatibility. | Dashboard and Database API owners. | Versioned operation matrix. |
| B: reactive client | Generated MessagePack binding, result-view engine, transport, checkpoints, and recovery. | A, TerraBaseV2, and TypeScript corpus. | Cross-language conformance receipt. |
| C: SDK quality | Async/cancellation, retries, bounded concurrency, disposal, EF Core, and API docs. | A and stable models. | Focused test and analyzer results. |
| D: package release | Pack, archive inspection, provenance, consumer install, runtime matrix, license and security. | A through C. | Immutable package handoff. |
| E: live qualification | Public endpoints, identity, tenant isolation, Region movement, incident and rollback. | Owning services and fixed routes. | Environment-gated release evidence. |

## Dependency waves

1. Freeze public operation ownership, versions, limits, and error/retry rules.
2. Generate and check route/schema bindings while independent client-quality
   work proceeds.
3. Land the reactive replacement against stable public contracts and run the
   shared semantic corpus.
4. Verify the exact package in a clean consumer and perform supported-runtime,
   security, license, and browser-independent SDK checks.
5. Run live service and Region qualification, then promote the exact artifact
   through the protected workflow.

## Decisions requiring contract owners

The SDK does not choose service behavior. The Database API and Dashboard owners
must publish the final route manifests, operation identities, security scopes,
limits, errors, and compatibility window before this package binds them.
The reactive service owners must publish the result-view schema, transport,
checkpoint retention, and legacy-client transition date before the replacement
client is qualified. Release owners must record the supported .NET runtime
window, package version policy, registry, and rollback authority.

## Acceptance criteria

- `dotnet format TerraScale.DotNet.slnx --verify-no-changes` passes.
- `dotnet build TerraScale.DotNet.slnx --configuration Release` and
  `dotnet test TerraScale.DotNet.slnx --configuration Release --no-build`
  pass with warnings as errors and no unreported live-test substitution.
- Every public operation has a matching owner schema, generated/manifest
  entry, typed model, error, security, retry, idempotency, and pagination rule.
- No source or published package contains a generic request escape hatch,
  direct private service path, secret, token, or customer payload.
- The reactive client passes shared vectors for snapshots, result changes,
  checkpoints, reset, duplicate/reorder, auth expiry, permission loss, Region
  movement, history expiry, cancellation, and slow consumers.
- EF Core tests pass separately and use only public SDK operations.
- The exact packed archive passes provenance, digest, clean-consumer, package
  contents, dependency, license, and runtime checks.
- Live qualification proves authorization, tenant isolation, reconnect,
  Region movement, and failure behavior on the approved endpoints.

## Exit gates

1. Contract gate: route/schema/model parity and compatibility review.
2. Behavior gate: operation, error, retry, cancellation, EF, and disposal tests.
3. Reactive gate: generated cross-language corpus and live recovery evidence.
4. Boundary gate: no private server or generic transport surface.
5. Artifact gate: exact package provenance, security, license, and clean
   consumer verification.
6. Live gate: public-service authorization, isolation, Region, and incident
   receipts.
7. Workspace gate: production services, soak, and GA review pass. This SDK
   cannot waive a missing server or infrastructure receipt.

## References

- [`docs/adr/README.md`](docs/adr/README.md)
- [`README.md`](README.md)
- [`../GOAL.md`](../GOAL.md)
