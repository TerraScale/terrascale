# TerraScale TypeScript SDK

`@terrascale/sdk` is the public TypeScript client for TerraScale.

The workspace `../GOAL.md` defines the target architecture. This repository's
current status and production gaps are tracked in [GOAL.md](GOAL.md). An
accepted target is not a released SDK capability.

## Service boundary

The SDK uses public versioned TerraScale HTTPS and reactive contracts:

- Dashboard owns Better Auth, Organizations, Projects, Users, Payments, HTTP
  MCP, and those public product routes.
- The Go Database API in `ts-internals` owns database, query, placement,
  backup, usage, and reactive routes.
- TerraBase is private. The SDK never speaks TBEP, opens a TerraBase
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

The Better Auth adapter accepts a typed Database API operation port and maps
Better Auth's adapter contract to named unique-insert, compare-and-swap,
one-time-consume, pagination, transaction, and migration operations. It is
server-only and is excluded from browser bundles. The generated Database API
implementation, pinned Better Auth compatibility and license evidence, and
adapter conformance are separate qualification gates.

There is no Dashboard-named or generic request entrypoint.

## Reactive protocol

The SDK has one reactive implementation: `TerraScaleSyncV2Client`. It decodes
the generated `terrascale.sync.v2.msgpack` frames and applies authorized
snapshots and result operations to bounded result views. The checked-in JSON
manifest owns every tag, field position, direction, and bound. The generated
TypeScript module consumes that file. INT-3's Go codec must check the same
manifest instead of copying a second frame definition.

MessagePack owns the envelope and result-operation structure only. Projected
document values stay as opaque TerraScale-canonical JSON byte strings inside
the frame. The SDK enforces the 1 MiB document ceiling across the aggregate
projected bytes in an item, does not transcode them into nested MessagePack,
and leaves the single native `JSON.parse` per delivered value to SDK-2's
browser boundary.

Protocol 2.1 is the first publishable wire contract. The pre-launch 2.0
artifacts reassigned tags during development and are withdrawn. The decoder
rejects 2.0 before reading a message body, and 2.0 decoders reject 2.1. Tags
in 2.1 are stable and future compatible changes must append new tags.

The current client:

- builds subscribe, unsubscribe, heartbeat, and checkpoint frames for bounded
  typed TQL queries;
- receives authorized result snapshots, insert, update, patch, remove, move, or
  reset operations;
- keeps checkpoints opaque and view-scoped;
- applies reversible optimistic presentation only while the transport is
  online;
- installs permission-change replacements before exposing the next view;
- stops client traffic on backpressure and marks Region movement offline; and
- passes the hostile-decode, semantic corpus, manifest-drift, unit, and browser
  gates.

SDK-2 supplies the production WebSocket transport and HTTP + MessagePack
fallback. Until those transports pass browser and live gateway tests, this
state machine alone is not a production transport. See
[ADR 0025](docs/adr/0025-public-messagepack-manifest.md).

## Better Auth adapter target

The server-only adapter implements the pinned Better Auth database
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

The default public API origin is `https://api.terrascale.tech`. Cloudflare
routes that global host to the nearest healthy Region. Region-pinned access
uses `https://gru.api.terrascale.tech`, `https://sfo.api.terrascale.tech`, or
`https://fra.api.terrascale.tech`.

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

The local runtime does not prove TerraBase replication, RF=2 durability,
failover, reactive conformance, authorization, usage, billing, external
providers, or performance. Unsupported operations fail explicitly.

## Public contract discipline

The SDK implements only named operations published by the owning service.
Dashboard-owned contracts and Database API contracts may share one edge origin
and OpenAPI document, but their runtime ownership remains distinct.
The public API `/api/v1` remains versioned; releases qualify the exact packed tarball
so consumers receive the same server and browser surface that passed the repository gates.

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

The repository decision records are under [`docs/adr/`](docs/adr/). Start with
[GOAL.md](GOAL.md) for the current implementation, cross-repository gaps,
parallel work waves, and production exit gates.

## Development

~~~bash
npm install
npm run typecheck
npm run build
npm test
npm run test:browser
~~~

Node.js 22 and 24 are the supported server runtimes. Browser qualification
covers the current stable major versions of Chrome, Firefox, Safari, and Edge through the repository
release gates.

Generate and verify route contracts with:

~~~bash
npm run generate:route-manifest
npm run check:openapi-drift
npm run generate:openapi-contract-descriptor
npm run check:openapi-contract-descriptor
~~~

The MessagePack generator and its qualification suites are separate from
`npm test`:

~~~bash
node scripts/generate-msgpack-protocol.mjs --check
node --test test/msgpack-manifest-drift.test.mjs
node --test test/msgpack-hostile-decode.test.mjs
node --test test/msgpack-corpus-roundtrip.test.mjs
~~~

Integration tests target the fixed DevHost public routes. When the required
base URLs or credentials are absent, they skip instead of using mocked HTTP.
The new Database API needs fixed DevHost routes before its live tests can be
current. OpenAPI drift and descriptor checks must use the owner-published
Dashboard snapshot. A local emulator cannot substitute for that evidence.

## Releases

The Orchestrator-owned `develop` to `main` promotion path builds and
publishes the immutable package only after contract, conformance, security, and
compatibility gates pass. See `docs/release.md`.
