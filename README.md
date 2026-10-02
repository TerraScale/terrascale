# terrascale

The JavaScript SDK for native TerraBase documents and hosted TerraScale identity.
The package targets Node.js 22 and 24 and current Chrome, Firefox, Safari, and Edge.
Release qualification installs the exact packed artifact into a separate consumer.

The source is plain ES modules typed with JSDoc and published as written; there
is no transpile step. `npm run build` generates the `.d.ts` declarations in
`types/`, so TypeScript and editor consumers get the same types the source is
checked against.

```js
import { createTerraBaseClient } from "terrascale";

const database = createTerraBaseClient({
  tenant: process.env.TERRABASE_TENANT,
  database: process.env.TERRABASE_DATABASE,
  shard: BigInt(process.env.TERRABASE_SHARD),
  apiKey: process.env.TERRABASE_API_KEY,
});

const result = await database.read(collectionUuid, documentUuid);
if (!result.ok) throw new Error(result.error.code);
console.log(result.value.state);
```

Entity IDs are canonical UUIDs. Shard, generation, sequence, and signed integer
values remain bigint; bytes remain Uint8Array. Preserve every conflict sibling
and the complete causal context. Missing fields are omitted; `{ type: "null" }`
is an explicit null. Use the actual provisioned collection's numeric field IDs,
schema generation, and digest. Schema helpers describe local declarations and
SQL builders produce parameterized descriptors; neither provisions a catalog.

Native methods are `read`, `query`, `subscription`, `prepareCommit`, `commit`,
and `transactionStatus`. Queries use bounded typed predicates and opaque cursors.
Subscriptions first consume a complete paginated snapshot, then finite poll
groups. SQL execution requires the separate PostgreSQL listener contract.

`createDatabaseQuery(client, options)` delivers complete native snapshot and poll
cuts to `useQuery` from `terrascale/react` or `createQuery` from
`terrascale/svelte`. TanStack DB 0.9.2 integration is available through
`terrascaleCollectionOptions` from `terrascale/tanstack`; map each complete
point explicitly and preserve conflicts. Publication is acknowledged before
advancing delivery, and cleanup cancels owned work. Mutations prepare and commit
through the same client; a presentation rollback does not establish a server abort.

Omitting `endpoint` uses authenticated, scope-bound routing at
`https://atlas.terrascale.tech` with the native `tbk1` database key. The client
selects an eligible canonical regional origin and checks its bounded lease again
before dispatch. `discoveryTimeoutMs` defaults to 3000 and accepts 1–30000 ms.
Each operation uses the receiver's highest-priority endpoint with the required
capabilities. Discovery denial, unavailability, invalid or expired leases, and
missing capabilities return a typed error before any native data request.
Set `endpoint` to use an independently provisioned direct HTTPS origin. HTTP is
allowed only on loopback for development, and the public test host is rejected.
Database keys and hosted identity management keys are separate credentials.

The admitted Atlas receiver advertises conditional mutations, atomic groups,
and security state as unavailable. Discovered clients refuse those operations
before native dispatch. Direct local native conditional semantics do not grant
those production capabilities.

For authentication/security conditional writes, use the provisioned single-writer authority, including
its generation and writer identity. An absent condition requires absence; a
version condition compares the complete point's state token. Copy the point's
full context into `observed` for replacement or deletion. Causal observation
alone does not establish compare-and-swap. Direct leaderless conditional commits
prove only the admitted local native exclusion semantics; they do not establish
distributed writer fencing. Leaderless storage is unsuitable for
security state and authentication invariants.

`prepareCommit` snapshots the request and computes its canonical SHA-256 effect
fingerprint. Reuse that prepared transaction on the same client for an exact
retry; a changed effect requires a new transaction identity. Mutations are not
automatically replayed. Resolve an uncertain mutation with transaction status;
unknown, pending, and expired status do not establish an abort. Persist the
transaction identity, authority, and fingerprint when recovery spans restarts.
`local_durable` and `object_durable` are distinct acknowledgement levels.

`terrascale/ts-auth` contains the separate hosted identity management and
OIDC client. Its private transport preserves form-encoded token operations,
bounded responses, deadlines, and a single changed-token refresh. Configure
its own origin, instance, and identity credentials.

`terrascale/local` is an explicitly enabled Node development runtime:

```js
import { createTerraScaleSqliteRuntime } from "terrascale/local";

const local = createTerraScaleSqliteRuntime({
  sqlite: { enabled: true, databasePath: "/absolute/path/development.sqlite" },
});
await local.bindCollection({
  collection: collectionUuid,
  schema_generation: 1n,
  schema_digest,
});
const client = local.createClient();
// Use local.authority for local conditional commits.
await local.close();
```

The local runtime refuses CI, production, qualification, staging, and deployment
environments. Its official Better Auth 1.7.2 adapter uses development SQLite
transactions and maintained adapter suites. These checks do not qualify
production security, regional replication, or the deployed customer application.

The management, administration, and production Better Auth entrypoints fail
with explicit capability errors until management/catalog provisioning and
credential lifecycle (C06/C07) and production Better Auth security persistence
are admitted.

Every push to `main` that passes CI publishes the `package.json` version to npm
when that version is not yet published. Bump the version to release.

```sh
npm ci
npm run typecheck
npm run build
npm test
npm run check:frameworks
npm run check:current-contract-inputs
npm run check:current-contract
npm run test:browser
```

Source parity requires absolute `TERRASCALE_CURRENT_OPENAPI` and
`TERRASCALE_ATOMIC_VECTORS` paths matching `sdk-current-contract.json` exactly.
Run `npm run qualify:current-producer` with a supplied immutable tarball and a
real isolated allocation; see [qualification inputs](scripts/qualification/README.md).
Missing real-producer inputs fail the required gate. See [release requirements](docs/release.md).

Hosted OAuth resource requests use one exact registered HTTPS `resource`. `oidc.authorizationUrl`, `oidc.deviceAuthorization`, and the code, device, refresh, and client-credentials token grants retain its bytes, including path and query. Omission on a browser code or refresh flow preserves the stored no-resource binding; it never repairs a rejected audience or falls back to browser login. Refresh requests can ask for narrower scopes. Client credentials require explicit resource and scope values. Token and originating-client introspection methods use `client_secret_post`; public-client and originating-client Basic token authentication are not currently exposed by this SDK.

`oauthClients` and `oauthResources` provide the producer's snake_case policy records and named management operations. Resource PATCH requires `expected_auth_epoch`; client PATCH accepts it. JSON epochs outside JavaScript's exact integer range are rejected. Client PATCH returns a bare client view unless `rotate_secret` is true, when it returns the client and its one-time secret. Resource create/PATCH always return the resource and nullable one-time introspection secret.

Use `oidc.introspectResource` with independently configured `issuer`, exact `audience`, `introspection_client_id`, and `introspection_secret`. It uses resource Basic authentication only, with each credential component form encoded before Base64. An active result requires the exact configured issuer/audience, `Bearer`, integer timestamps valid at the local clock with no default skew, and the required user/service session shape. Inactive results contain only `active: false`. Resource results expose no internal epochs, refresh family, or token-use fields. The provider checks current durable token/session/resource state; an active principal or scope does not grant project ownership or management authority. Product authorization remains at the caller's existing authorization boundary.

Regional native database origins use `https://<region>.api.terrascale.tech`,
for example `https://gru.api.terrascale.tech`. The global API host and retired
root regional hosts cannot serve as native origins. Auth uses its separate
`.auth.terrascale.tech` namespace. Direct independent origins must use ASCII
hostnames; percent-encoded and Unicode hostname aliases are rejected.

## TerraBase v1 documents, queries and shapes

`createTerraBaseV1Client` targets the name-based TerraBase v1 Documents API
(terrabase `docs/api-v1.md`, `docs/query-v1.md`, `docs/shapes-v1.md` on
`feat/v1-contracts` at `7bc74f9d`). It sits
beside the UUID/shard native client above, which stays until those routes are
removed. It needs a `tbk1` data key; the key is held privately and is sent only
as `Authorization` to `baseUrl` (HTTPS, or HTTP on loopback).

```js
import { createTerraBaseV1Client, documentIdFor, newIdempotencyKey, v1Filter } from "terrascale";

const tb = createTerraBaseV1Client({ baseUrl: "https://gru.terrabase.example", token: process.env.TERRABASE_KEY });
const db = tb.database("main");
const users = db.collection("users");

const id = await documentIdFor("users", "ana@example.com"); // UUIDv5 natural key
const written = await users.put(id, { email: "ana@example.com", seats: 1 }, { ifNoneMatchAny: true });
const doc = await users.get(id); // { id, version, state: "value", document }
await users.patch(id, { ops: [{ op: "inc", path: "/seats", by: 1 }] }, { ifMatch: doc.version });

await db.transaction()
  .create("orgs", { name: "Acme" })
  .assertAbsent("invites", id)
  .assertCount("access_tokens", v1Filter.eq("/user_id", id), { lte: 24 })
  .commit({ idempotencyKey: newIdempotencyKey() });

for await (const page of users.pages({ filter: v1Filter.prefix(v1Filter.lower("/email"), "ana"), count: true }))
  console.log(page.count, page.items.length);

const shape = await db.subscribe({ collection: "users", filter: v1Filter.exists("/email") });
for await (const event of shape) {
  if (event.type === "reset") { /* discard local state; a snapshot follows */ }
  for (const change of event.changes) for (const op of change.ops) console.log(op.op, op.id);
}
await shape.waitFor(written.offset); // read-your-writes, from another task
```

- Documents: `get`, `put`, `create`, `patch` (`{ merge }` or `{ ops }` with
  `set`, `unset`, `inc`, `append`) and `delete`, with `ifMatch`,
  `ifNoneMatchAny` and `idempotencyKey`. Writes return `{ id, version, offset,
  durability, created, location }`; `offset` is a decimal u64 string.
- Transactions: `create`, `put`, `patch`, `delete`, `assert`, `assertAbsent`
  and `assertCount` (a filter and `gte` and/or `lte` are required), with
  per-op preconditions (`patch` and `delete` take only `ifMatch`).
- Errors: every failure is a `TerraBaseV1Error` with `status`, `code`, `op`
  and `constraint` (also `handle`, `retryAfter`, `etag` and the raw
  `details`). The common cases are subclasses: `V1PreconditionFailedError`
  (412), `V1UniqueViolationError` (409, with `constraint`),
  `V1InvalidKeyError` (401 `invalid_key`), `V1ForbiddenScopeError` (403) and
  `V1NotFoundError` (404). `V1ErrorCode` lists every contract code and
  `isTerraBaseV1Error(error, code)` narrows one.
- Queries: `v1Filter` builds `and`, `or`, `not`, `eq`, `ne`, `lt`, `lte`,
  `gt`, `gte`, `in`, `nin`, `exists`, `prefix`, `contains`, `lower` and the
  `v1Filter.id` (`"$id"`) path. `lower` sends `{ "lower": path }`; the server
  applies Unicode default case folding to the stored values, and the client
  never folds anything itself. `query` returns one page; `pages` and `items`
  follow `nextCursor`, sending `count` and `explain` with the first page only.
- Shapes: `createShape` calls the gatekeeper. `subscribe` long-polls by default
  (`offset=-1` → 307 → snapshot pages → log → live, echoing
  `TB-Shape-Cursor`); `{ mode: "sse" }` reads the origin's event stream over
  fetch and reconnects with `Last-Event-ID`; `{ edge: true }` reads through the
  edge URL with the shape token and refreshes it before `expiresAt`.
  `must_refetch` yields a `reset` event and restarts from the snapshot;
  `position` drops to `"0"` until the replacement snapshot has been handled.
  When iteration ends for any reason (close, the abort signal, a terminal
  error, or the consumer stopping), the subscription closes and pending
  `waitFor` calls reject, with the terminal error when there is one.
- Exact numbers: responses are parsed with `JSON.parse` source text access. A
  number is a JavaScript `number` only when that number denotes the same
  decimal value as the wire text; otherwise it is an `ExactDecimal` holding the
  exact text (`toString`, `toBigInt`, `equals`, `toNumber`). `ExactDecimal`
  and `bigint` values are written back exactly with `JSON.rawJSON`. This needs
  Node.js 22+ or a current browser.
- Retries follow the contract: `429` after `Retry-After` (a `Retry-After`
  above 30 seconds is not shortened: the error is returned with `retryAfter`);
  a response body cut off in transit is retried like a transport failure for
  replay-safe requests; `capacity`,
  `storage_unavailable` and `dependencies_unavailable` only for reads,
  queries, the shape gatekeeper, shape reads and writes with an
  `idempotencyKey`; `commit_uncertain` (503) and `internal_error` (500) only
  with an `idempotencyKey`. Shape reads also retry the edge's `502
  upstream_unavailable` and a 504 within the subscription's bounded
  consecutive-failure budget (`maxRetries`). Nothing else is retried. `createTerraBaseV1Client({ ..., autoIdempotencyKey: true })` gives
  every unkeyed mutation a fresh key so those outcomes are retried safely.

`sdk-v1-route-manifest.json` is generated from the client's route table and
pins the contract documents by SHA-256 (`npm run check:v1-route-manifest`;
set `TERRABASE_REPO` to a terrabase checkout, or `TERRABASE_V1_CONTRACT_DIR`
to its `docs` directory, to verify the digests, the route declarations and the
error-code table). Set `TERRABASE_V1_OPENAPI` to the producer's v1 OpenAPI
file to also require every route there; terrabase has not published a v1
OpenAPI yet.

The v1 integration tests (`test/v1-*.test.js`) run against an in-test HTTP
loopback contract fixture, which is not evidence of producer behavior.
`test/v1-conformance.test.js` also runs against a real tb-server when
`TB_V1_URL` and `TB_V1_DATA_KEY` are set (and `NODE_EXTRA_CA_CERTS` for a
private CA); optional `TB_V1_DATABASE` (default `app`), `TB_V1_COLLECTION`
(default `todos`, with a unique constraint on `/email`),
`TB_V1_UNIQUE_CONSTRAINT` (default `email`, `""` for none) and
`TB_V1_REVOKED_KEY`. Without them the live suite is reported as skipped.
