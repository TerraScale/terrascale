# TerraScale TypeScript SDK

`@terrascale/sdk` is the public TypeScript client for TerraScale.

The workspace `../GOAL.md` defines target architecture. An accepted target is
not a released SDK capability.

## Service boundary

The SDK uses public versioned TerraScale HTTPS and reactive contracts:

- Dashboard owns Better Auth, Organizations, Projects, Users, Payments, HTTP
  MCP, and those public product routes.
- The independent C#/.NET 10 `TerraScale.Database` service owns database,
  query, placement, backup, usage, and reactive routes.
- TerraBaseV2 is private. The SDK never speaks TBEP, opens a TerraBase
  connection, consumes native replication data, or uses NATS.

Dashboard uses this package exactly as an external customer application does.
It receives no private entrypoint, header, tenant bypass, or direct database
path.

## Entrypoints

| Entrypoint | Contract |
| --- | --- |
| `@terrascale/sdk` | Ordinary typed customer operations |
| `@terrascale/sdk/admin` | Role-protected public administration |
| `@terrascale/sdk/local` | Explicit development-only local runtime |
| `@terrascale/sdk/better-auth` | Public server-only Better Auth adapter |

The Better Auth adapter accepts the generated Database API operation port and
maps Better Auth's adapter contract to named unique-insert, compare-and-swap,
one-time-consume, pagination, transaction, and migration operations. It is
server-only and is excluded from browser bundles. The live Database API
implementation and adapter conformance evidence are separate qualification
gates.

There is no Dashboard-named or generic request entrypoint.

## Honest sync status

This tree still contains:

- the legacy `terrascale.sync.v1` client; and
- a Loro-era `terrascale.sync.v2` client, codec, and vendored corpus.

Neither implements the accepted replacement reactive contract. The old v2
uses collection subscriptions, opaque Loro deltas and snapshots, and
server-ack-only presentation. It is migration evidence and must not receive new
product features.

The replacement `terrascale.sync.v2` will:

- subscribe to bounded typed TQL queries;
- receive authorized result snapshots, insert, update, patch, remove, move, or
  reset operations;
- use opaque view-scoped checkpoints;
- support read-your-writes and Region movement through sparse causal tokens;
- apply reversible optimistic presentation for connected online writes;
- reject durable writes while disconnected;
- reauthorize on reconnect and grant refresh;
- remove data after permission loss;
- bound every queue, subscription, result, and reconnect path; and
- pass one semantic corpus shared with Rust and .NET.

The new generated corpus must not preserve old Loro bytes. Until the
replacement passes conformance, the current sync story is decided but
unqualified.

## Better Auth adapter target

The planned server-only adapter implements the pinned Better Auth database
adapter contract through named operations for:

- unique insert;
- compare-and-swap;
- one-time consume;
- bounded transaction batches;
- indexed reads and pagination;
- idempotent retry; and
- schema migration.

It cannot use raw `fetch`, arbitrary paths, generic SDK requests, direct
TerraBase, TBEP, or Dashboard private routes.

Qualification uses Better Auth's maintained adapter tests where available,
exact-version compatibility, concurrent Region cases, migration, revocation,
and fault injection.

## Idempotent mutations

Public clients opt in with `idempotencyKeyFactory` on the client or
`idempotencyKey` on an individual request. Set the request value to `false`
only when the operation's public contract permits no generated identity.

Keys are nonblank and at most 128 characters. A retry reuses the same key for
one logical attempt. Reusing a key with different input is a typed conflict.
Replay retention and response headers follow the owning public contract.

Mutations retry only when the operation is documented as idempotent. Validation,
authentication, authorization, and deterministic conflict failures are not
automatic retry candidates.

## Region and session behavior

The default public API origin is `https://api.terrascale.tech` in the current
SDK. The final single-origin edge routing decision is awaiting owner
confirmation.

A database selects one through twenty active Regions, initially GRU, SFO, and
FRA. A preferred Region is a failover-capable hint. Strict affinity fails
rather than silently serving from another Region.

Automatic session consistency retains the latest opaque causal token inside
one client instance and sends it on later eligible reads. A caller can request
an explicitly stale read where the public contract allows it.

`RegionalDurable` avoids WAN acknowledgement and requires both active local
Members. `GlobalDurable` pays WAN latency and requires every selected active
Region. Backup is an independent opt-in.

## Local development runtime

The explicit SQLite local runtime supports bounded public-contract development:

~~~ts
import { createTerraScaleSqliteRuntime } from "@terrascale/sdk/local";

const local = createTerraScaleSqliteRuntime({
  sqlite: { enabled: true, databasePath: "/absolute/path/to/local.sqlite" },
});

const seed = await local.seed();
const client = local.createClient({ projectId: seed.projectId });

await local.start();
await local.stop();
await local.reset({ seed: true });
await local.close();
~~~

Importing `local` is the opt-in. Root, `admin`, and `better-auth`
entrypoints never enable SQLite automatically.

The local runtime does not prove TerraBaseV2 replication, RF=2 durability,
failover, reactive conformance, authorization, usage, billing, external
providers, or performance. Unsupported operations fail explicitly.

## Public contract discipline

The SDK implements only named operations published by the owning service.
Dashboard-owned contracts and Database API contracts may share one edge origin
and OpenAPI document, but their runtime ownership remains distinct.

Each operation is gated against:

- method and versioned path;
- operation identity;
- parameters and requiredness;
- request and response media types;
- field types and nullability;
- security scheme and scopes;
- success, error, and partial outcome shapes;
- idempotency and retry;
- pagination, checkpoint, and streaming semantics; and
- route-manifest parity.

Unbacked operations remain omitted. Do not invent types or use a generic
request to hide a contract gap.

## Development

~~~bash
npm install
npm run typecheck
npm run build
npm test
npm run test:browser
~~~

Node.js 22 and 24 are the supported server runtimes. Browser qualification
covers current stable Chrome, Firefox, Safari, and Edge through the repository
release gates.

Generate and verify route contracts with:

~~~bash
npm run generate:route-manifest
npm run check:openapi-drift
npm run generate:openapi-contract-descriptor
npm run check:openapi-contract-descriptor
~~~

Integration tests target the fixed DevHost public routes. When the required
base URLs or credentials are absent, they skip instead of using mocked HTTP.
The new Database API needs a fixed DevHost route before its live tests can be
current.

## Releases

The Orchestrator-owned `develop` to `main` promotion path builds and
publishes the immutable package only after contract, conformance, security, and
compatibility gates pass. See `docs/release.md`.
