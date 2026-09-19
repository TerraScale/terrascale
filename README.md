# TerraScale TypeScript SDK

`@terrascale/sdk` is the public TypeScript client for TerraScale.

The workspace `../GOAL.md` is the sole architecture and delivery authority.
This repository's [GOAL.md](GOAL.md) points to it and lists the local
verification commands. An accepted target is not a released SDK capability.

## Service boundary

Dashboard is the full-stack Vinext application and owns Better Auth,
organizations, projects, users, payments, MCP, and those public product routes.
The separate TerraScale.Database service owns the public database, SQL,
placement, backup, usage, and reactive routes and is the sole TBEP gateway.
The SDK uses the owning services' versioned public contracts. Dashboard uses
this SDK with the same public authorization boundary as customer applications;
it receives no private entrypoint, header, identity bypass, or direct database
access.

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

| Import | Purpose |
| --- | --- |
| `@terrascale/sdk` | Client factory, public operations, schema/SQL types, reactive client |
| `@terrascale/sdk/admin` | Role-protected administration |
| `@terrascale/sdk/schema` | Schema builders |
| `@terrascale/sdk/sql` | Parameterized SQL builders |
| `@terrascale/sdk/management` | Database and collection management |
| `@terrascale/sdk/react` | React integration |
| `@terrascale/sdk/svelte` | Svelte integration |
| `@terrascale/sdk/tanstack` | TanStack integration |
| `@terrascale/sdk/better-auth` | Server-only Better Auth adapter |
| `@terrascale/sdk/local` | Explicit local development runtime |

The Better Auth adapter accepts a typed Database API operation port and maps
Better Auth's adapter contract to named unique-insert, compare-and-swap,
one-time-consume, pagination, transaction, and migration operations. It is
server-only and is excluded from browser bundles. The generated Database API
implementation, pinned Better Auth compatibility and license checks, and
adapter conformance are separate qualification gates.

```ts
import { AuthPersistenceClient, createTerraScaleAuthAdapter } from "@terrascale/sdk/better-auth";

## Database documents

`databaseDocuments` follows the Go Database API contract directly. The
currently published operations read, create, merge-patch, and delete one
document at
`/api/v1/databases/{database}/collections/{collection}/documents/{documentId}`.
They are separate from the older repository-scoped `documents` client, which
remains available for the Dashboard-owned contract.

~~~ts
import { createTerraScaleClient, toUlid } from "@terrascale/sdk";

const client = createTerraScaleClient({
  accessToken: process.env.TERRASCALE_ACCESS_TOKEN,
});
const scope = { database: "app", collection: "users" };
const idempotencyKey = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FB0");

await client.databaseDocuments.create(
  scope,
  "user-1",
  { body: { name: "Ada" }, durability: "global" },
  { idempotencyKey },
);
~~~

Database, collection, and document identifiers must contain 1 to 64 UTF-8
bytes and no control characters. Each mutation requires a canonical uppercase
ULID, reused unchanged across retries. Create and patch put durability in the
JSON body. Delete sends it as the published query parameter.
The current gateway has a configurable request-body ceiling. It does not yet
publish the launch 1 MiB document limit or an attachment route, so this SDK
does not promise a large-value upload operation.

The Database API publishes document `version`, journal `sequence`, and nullable
journal `position` as canonical unsigned 64-bit decimal strings. A value is
`"0"` or a nonzero decimal string without leading zeroes, has at most 20
digits, and is no greater than `"18446744073709551615"`; `position` may also
be `null`. The SDK rejects numeric JSON values, malformed decimal text, and
overflow as `invalid_response` without converting the metadata to JavaScript
numbers.

Database API failures expose the standard `TERRASCALE-####` code, category,
detail, suggested action, documentation URL, trace ID, and field errors through
the typed result. The response's `X-Request-Id` remains a separate request ID.

## Reactive protocol

The current reactive client is `TerraScaleSyncV1Client`, exposed as
`client.reactive` and `TerraScaleSyncV1Client`. It decodes the generated
`terrascale.sync.v1.msgpack` frames and applies authorized snapshots and
result operations to bounded result views. The checked-in JSON manifest owns
every tag, field position, direction, and bound. The generated TypeScript
module consumes that file. INT-3's Go codec must check the same manifest
instead of copying a second frame definition.

MessagePack encodes the envelope, subscription identities, revisions,
checkpoints, and result-operation structure. Document bodies inside those
frames remain raw TerraScale-canonical JSON bytes. The gateway does not
transcode them into MessagePack objects. The production client must decode the
envelope, then use one native `JSON.parse` for each document body. It must not
double-decode or parse a MessagePack-encoded document.

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

Delivery uses the pinned producer transports: authenticated HTTP attach plus
cacheable snapshot reads and bounded long-poll log reads, with optional direct
push upgrades to the attached member. HTTP is the default; pass
`transport: "websocket"` (and optionally `webSocketFactory` in environments
without a global `WebSocket`) or `transport: "server-sent-events"` to upgrade.
See [ADR 0025](docs/adr/0025-public-messagepack-manifest.md).
There is no JSON WebSocket variant; binary MessagePack envelopes are the only
wire schema.

The reactive hooks bind the same observer to both frameworks:
`useQuery`/`useMutation` from `@terrascale/sdk/react` and
`createQuery`/`createMutation` stores from `@terrascale/sdk/svelte` (any object
implementing the Svelte store contract, so `$query.data` auto-subscribes
without a runtime dependency).

REST discovers eligible regional endpoints once per routing scope and then
connects directly (ADR 0050); the global origin remains the fallback.
Placement repair must preserve authorization, session frontiers, and
idempotency. `preferredRegion` is a failover-capable hint;
`regionAffinity: "strict"` requires a preferred Region and refuses silent
regional fallback.

Use `idempotencyKeyFactory` or an operation's `idempotencyKey` for retryable
mutations. Keys are nonblank and at most 128 characters; retries retain one
identity for one logical attempt. Validation, authentication, authorization,
and deterministic conflicts are not automatic retry candidates.

`RegionalDurable` requires both local members. `GlobalDurable` requires every
selected Region and is unavailable for a single-Region database. These are
server guarantees that require live qualification, not client-side promises.

The public API `/api/v1` is versioned. The SDK runs on Node.js 22 and 24.
Release qualification must cover the
exact packed tarball and the current stable major versions of Chrome, Firefox, Safari, and Edge.

## Development and contracts

## Idempotent mutations

Public clients opt in with `idempotencyKeyFactory` on the client or
`idempotencyKey` on an individual request. Set the request value to `false`
only when the operation's public contract permits no generated identity.

Keys are nonblank and at most 128 characters. A retry reuses the same key for
one logical attempt. Reusing a key with different input is a typed conflict.
Replay retention and response headers follow the owning public contract.
Database document mutations use the narrower gateway contract: a canonical
uppercase ULID is mandatory.

Mutations retry only when the operation is documented as idempotent. Validation,
authentication, authorization, and deterministic conflict failures are not
automatic retry candidates.

## Region and session behavior

The discovery origin is `https://atlas.terrascale.tech` (the metadata-only
`ts-atlas` Worker); `https://api.terrascale.tech` is the dashboard-owned
product API origin. Per
[ADR 0050](../terrabase/docs/adr/0050-sdk-discovery-and-direct-regional-routing.md),
the SDK's first database-scoped request performs one metadata
`GET /api/v1/discovery` call against the discovery origin and then connects
**directly** to the ranked regional endpoint —
`https://gru.api.terrascale.tech`, `https://sfo.api.terrascale.tech`, or
`https://fra.api.terrascale.tech` — for every subsequent query, mutation, and
reactive connection. Discovery never runs before every query: the routing
document is cached per database/branch/project/tenant scope for its refresh
policy, concurrent lookups share one request, and a healthy connection is
kept across refreshes.

There is no proxy fallback: the per-request proxy is retired. If discovery
fails or times out (bounded at 3 s), the SDK fetches the region catalog
(`GET /api/v1/regions` on the discovery origin, cached five minutes) and
sends the request directly to the first available regional endpoint.
Database-less member routes (database create/list, tenants) use the same
catalog selection, while dashboard-owned product routes
(`/api/v1/organizations`, `/api/v1/projects`, `/api/v1/regions/{code}`)
target the product origin `api.terrascale.tech`.

Routing documents carry a routing revision, a refresh policy, and eligible
endpoints with per-region capabilities; a draining replica keeps reads but
loses writes, and mutations skip it automatically. A member `421` still
repairs placement once (ADR 0043 §4) and schedules a corrective
rediscovery. Never-failover rules are absolute: the SDK only fails over
within the document's eligible endpoints for the same database.

At launch, a database selects one to three active Regions from GRU, SFO, and
FRA. A preferred Region is a failover-capable hint honoured only when it is
eligible in the routing document. Strict affinity (`regionAffinity: "strict"`
with `preferredRegion`) pins at construction and never uses discovery.
Custom origins keep ADR 0043 behavior unless `routing.baseUrl` or
`routing.enabled` opts in. `requestTimeoutMs` (default 30 s) bounds every
request including retries; retry and reconnect backoff is exponential with
jitter. A `401` with a function `accessToken` provider re-resolves the token
once and replays only when the token changed.

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

The local runtime does not qualify TerraBase replication, RF=2 durability,
failover, reactive conformance, authorization, usage, billing, external
providers, or performance. Unsupported operations fail explicitly.

## Public contract discipline

The SDK implements only named operations published by the owning service.
Dashboard-owned contracts and Database API contracts share one edge origin,
but their runtime ownership and OpenAPI documents remain distinct. The public API `/api/v1`
remains versioned. Releases qualify the exact packed tarball so consumers receive the same
server and browser API that passed the repository gates.

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
the in-repository [`GOAL.md`](GOAL.md), which points workspace contributors to
the cross-repository authority and production exit gates.

## Development

~~~bash
npm install
npm run typecheck
npm run build
npm test
```

This checks committed contract metadata, installs dependencies, typechecks,
builds, and runs Vitest. Browser qualification is a separate lane:

```bash
npm run test:browser
```

Use the explicit generation scripts to regenerate route and OpenAPI metadata
from the pinned producer bundle:

```bash
npm run generate:route-manifest
npm run generate:openapi-contract-descriptor
```

Review generated changes against the owning service contract before updating a
pin. Product routes remain typed clients in the root entrypoint and are not
part of the generated database/reactive route manifest.

~~~bash
node scripts/generate-msgpack-protocol.mjs --check
node --test test/msgpack-manifest-drift.test.mjs
node --test test/msgpack-hostile-decode.test.mjs
node --test test/msgpack-corpus-roundtrip.test.mjs
~~~

Integration tests target the fixed DevHost public routes. When the required
base URLs or credentials are absent, they skip instead of using mocked HTTP.
Database API live tests also need fixed DevHost routes and credentials. The
default OpenAPI gate checks the owner-published Dashboard snapshot and the
checked-out `ts-internals` Database API document, including a complete
contract fingerprint for each covered Database operation. Auth-storage routes stay
listed as explicit SDK-3/INT-4 deferrals until their durable contract lands. A
local emulator cannot qualify a live contract.

## Releases

The protected `develop` to `main` promotion builds and publishes one immutable
package only after contract, conformance, security, and compatibility gates
pass. The current automation is not authorized to publish until it uses the
current delivery toolchain described in `docs/release.md`.
