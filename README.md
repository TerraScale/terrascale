# TerraScale TypeScript SDK

`@terrascale/sdk` provides typed public TerraScale operations for browsers,
Node.js 22 and 24, and Cloudflare Workers. The [workspace goal](../GOAL.md#ts-typescript) defines
required capabilities; [release qualification](docs/release.md) distinguishes
implemented checks from production evidence still required.

## Service boundary

The Rust TerraBase member serves the public database, SQL, placement, backup,
usage, and reactive APIs. Dashboard owns identity, organizations, projects,
users, payments, and MCP. The SDK uses their versioned public contracts.
Private TBEP, native replication, NATS, and direct storage access are internal.
Dashboard uses the same SDK and authorization as customer applications.

Create clients with `createTerraScaleClient(config)`. Authentication uses
`accessToken`, which accepts a token or token provider. The default API origin
is `https://api.terrascale.tech`. Keep server credentials out of browser bundles.

## Entrypoints

| Import | Purpose |
| --- | --- |
| `@terrascale/sdk` | Client factory, public operations, schema/SQL types, reactive client |
| `@terrascale/sdk/admin` | Role-protected administration |
| `@terrascale/sdk/schema` | Schema builders |
| `@terrascale/sdk/sql` | Parameterized SQL builders |
| `@terrascale/sdk/management` | Database and collection management |
| `@terrascale/sdk/tenants` | Tenant operations |
| `@terrascale/sdk/react` | React integration |
| `@terrascale/sdk/tanstack` | TanStack integration |
| `@terrascale/sdk/better-auth` | Server-only Better Auth adapter |
| `@terrascale/sdk/local` | Explicit local development runtime |

The export map in [package.json](package.json) is authoritative. Public clients
expose named operations, not arbitrary request paths or a generic transport.
The server-only Better Auth factory uses the named public auth operations:

```ts
import { AuthPersistenceClient, createTerraScaleAuthAdapter } from "@terrascale/sdk/better-auth";

const database = createTerraScaleAuthAdapter(
  new AuthPersistenceClient({ apiBaseUrl, accessToken: apiKey }),
  authDatabaseId,
);
// Supply `database` as the database option to Better Auth.
```

Install the pinned optional peer `@better-auth/core@1.7.2` when using this
entrypoint with Better Auth 1.7.2. It supports the official schema transforms,
explicit joins, atomic token consumption and serialized transaction callbacks.
Producer deployment, schema migration and real multi-Region qualification are
still required; this integration does not establish production readiness.

The temporary local SQLite runtime is separate from TerraBase's production
SQLite engine. It cannot qualify production behavior and is forbidden in
production, staging, deployment, and CI qualification. See
[ADR 0004](docs/adr/0004-sqlite-local-development-only.md).

## Queries and reactive delivery

Queries use read-only SQL; durable writes use document operations.
Document reads and SQL row values return integer literals outside JavaScript's
safe range as `bigint` through the `DocumentValue` and `SqlValue` types. Strings,
floating-point literals, and safe integers retain their JSON representations.
These reads require JSON reviver source support for large integers and return
`invalid_response` if the runtime cannot preserve them. Write inputs remain
`JsonValue`; `bigint` is not a supported request value.
`TerraScaleSyncV1Client` implements the reactive state machine with CDN-backed
HTTP delivery by default and explicit WebSocket opt-in. It applies bounded
snapshots, ordered result changes, checkpoints, and typed resets. Online
optimism is reversible presentation; there is no durable offline write queue.

The generated `terrascale.sync.v1.msgpack` codec defines frame layout and
bounds. Projected values remain canonical JSON bytes until the delivery
boundary. Unknown wire versions and hostile sizes fail closed. Permission
changes must remove revoked results before exposing replacement views.
Transport implementation alone does not prove cross-language conformance or
live authorization, reconnect, and Region-movement behavior.

## Routing and retries

REST starts at the global origin and can pin to a permitted Region. Placement
repair must preserve authorization, session frontiers, and idempotency.
`preferredRegion` is a failover-capable hint; `regionAffinity: "strict"`
requires a preferred Region and refuses silent regional fallback.

Use `idempotencyKeyFactory` or an operation's `idempotencyKey` for retryable
mutations. Keys are nonblank and at most 128 characters; retries retain one
identity for one logical attempt. Validation, authentication, authorization,
and deterministic conflicts are not automatic retry candidates.

`RegionalDurable` requires both local members. `GlobalDurable` requires every
selected Region and is unavailable for a single-Region database. These are
server guarantees that require live qualification, not client-side promises.

The public API `/api/v1` is versioned. Release qualification must cover the
exact packed tarball and the current stable major versions of Chrome, Firefox, Safari, and Edge.

## Development and contracts

```bash
make check
```

This checks committed contract metadata, installs dependencies, typechecks,
builds, and runs Vitest. Browser qualification is a separate lane:

```bash
npm run test:browser
```

`make contracts` regenerates route and OpenAPI metadata from the pinned
TerraBase bundle. Supply explicit development `CONTRACT_SOURCE`, or immutable
`CONTRACT_URL` and `CONTRACT_ARCHIVE_SHA256`; there is no Dashboard fallback.
Review generated changes against the producer contract before updating a pin.

Unavailable live test configuration is reported as skipped. Unit, browser,
package, and real-service evidence are separate release requirements. See the
[ADR index](docs/adr/README.md) and [release guide](docs/release.md).
