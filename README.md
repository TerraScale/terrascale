# @terrascale/sdk

The JavaScript/JSDoc SDK for the project-scoped C#/.NET TerraBase server
(`TerraScale/terrascale`). The historical source compatibility audit records
`e30ffdc287530551b161b0130220be53d3c0475e`; see the
[compatibility audit](docs/api-compatibility-20261013.md) for exact evidence and open gates.
The package targets Node.js 22 and 24 and current Chrome, Firefox, Safari, and Edge.
Plain ES modules ship as written; `nice -n 19 npm run build` generates the
TypeScript declarations in `types/`.
The latest published standard is [ECMAScript 2026](https://ecma-international.org/publications-and-standards/standards/ecma-262/).
The compiler uses [`ESNext`](https://www.typescriptlang.org/tsconfig/target.html),
its latest supported ECMAScript target. Source stays
in `.js` files; JSDoc supplies types through `allowJs` and `checkJs`. JSDoc typing
is a [tooling feature](https://www.typescriptlang.org/docs/handbook/jsdoc-supported-types.html),
independent of the ECMAScript edition. Published runtime
support remains Node.js 22/24 and the browser targets listed above.

```sh
npm install @terrascale/sdk
```

The following shows the current API with a trusted `discoveredOrigin` supplied
by your authorized integration. Built-in Atlas integration remains pending; see
[endpoint selection](#atlas-and-endpoint-selection).

```js
import { createTerraBaseV1Client, generateId, v1Filter } from "@terrascale/sdk";

const client = createTerraBaseV1Client({
  project: process.env.TERRABASE_PROJECT,
  token: process.env.TERRABASE_ACCESS_TOKEN,
  baseUrl: discoveredOrigin,
});
const db = client.database("app");
const todos = db.collection("todos");

const id = generateId();
await todos.put(id, { title: "Use TerraBase", done: false }, { ifNoneMatchAny: true });
const doc = await todos.get(id);
await todos.patch(id, { merge: { done: true } }, { ifMatch: doc.version });

for await (const page of todos.pages({ filter: v1Filter.eq("/done", true), limit: 50 })) {
  console.log(page.items);
}
```

Provision the project, database and collection before using their handles.
Current project routes authenticate ts-auth project access JWTs. Legacy C# `tb_`
keys remain accepted for their narrower data scopes; they cannot grant
`databases:read` or `databases:write`. The historical immutable release harness
still uses `tb_` keys and does not qualify the current JWT implementation.
Credentials travel as
`Authorization: Bearer <token>` and are held privately. The SDK validates
header-safe syntax; authentication and authorization belong to the producer.
Reads and queries require `documents:read`; writes and transactions require
`documents:write`. Transaction assertions additionally require `documents:read`.
Keep service credentials private and out of logs and browser bundles. `createTerraBaseClient`
is an alias of `createTerraBaseV1Client`; it no longer accepts tenant/shard options.

## Atlas and endpoint selection

The [zero-configuration operating contract](AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert)
requires first-party services to discover every eligible serving region and
receive production credentials automatically, without regional activation or
manual endpoint/secret wiring. Customer project/database scope and authorized
access tokens remain required. This release has not implemented automatic Atlas
integration (TS-507); the endpoint examples show the current API boundary and
are not a first-party production setup recipe. No new resolver API is implied.

The current implementation requires exactly one of `baseUrl` or
`endpointResolver`. In these examples, `discoveredOrigin` is a trusted HTTPS
origin supplied by an authorized integration, not an SDK-created value.
A resolver receives
`{ project, database?, signal }` and returns the service URL, synchronously or asynchronously.
Project catalog operations omit `database`; each project/catalog or database session
resolves once and reuses its URL.
Shared resolution expires after 30 seconds and supplies a cancellation signal
to the resolver; a later operation can retry an expired or failed resolution.
Cancelling one operation leaves other callers' shared resolution running.
HTTPS is required except for loopback HTTP in isolated tests; no regional
host is hardcoded and no implicit production endpoint is selected.

```js
import { createTerraBaseV1Client, staticBaseUrlResolver } from "@terrascale/sdk";

const client = createTerraBaseV1Client({
  project: process.env.TERRABASE_PROJECT,
  token: process.env.TERRABASE_ACCESS_TOKEN,
  endpointResolver: staticBaseUrlResolver(discoveredOrigin),
});
const db = client.database("app");
```

Atlas discovers the closest eligible
region and the location of a project/database. The resolver seam allows that
choice to stay pinned for the session. Atlas discovery integration is deferred
to TS-507; this release requires an explicit URL or caller-supplied resolver.

## Documents, SQL, change streams and catalog

Current handler source implements project-scoped database names and point
addresses as `/documents/by-key?id=…`. The complete method/path/scope inventory
is in [sdk-v1-route-manifest.json](sdk-v1-route-manifest.json), including the
current public inventory and a retained historical shape route. Current generated
OpenAPI and immutable release qualification remain open gates.

| Surface | Public methods |
| --- | --- |
| Documents | `collection.get/put/create/patch/delete/list/query/pages/items` |
| Collections and declared JSON Schema | `db.collections.create/get/list/drop`; `collection.getSchema/putSchema/deleteSchema` |
| Collection TTL | `collection.putTtl/getTtl/deleteTtl` |
| Databases | `client.databases.create/list/get/update/delete/branch` |
| Relational SQL | `db.execute/query/batch` |
| Read-only SQL (older) | `db.sql/checkSql/explainSql` |
| Changes | `db.changes` (SSE async iterator) |
| Export | `db.export` (NDJSON async iterator) |

A writable database requires at least two regions. `client.databases.create({ name })`
uses the called region as its management home and every other current serving region
as replicas. Setting `homeRegion` selects where the database is managed; it does
not request single-region placement. An explicit `replicaRegions` list must include
at least one region other than the home; an empty list on create returns
400 `placement_needs_two_regions`.
`allRegions: true` instead follows every serving region, including regions that
join later, and cannot be combined with `replicaRegions`. `client.databases.update`
with an explicit replica list turns all-regions placement off and cannot change
the home region. An empty `replicaRegions` in a response with `allRegions: true`
represents all-regions placement.

Call `client.databases.delete(name)` directly in the database's home region,
without a preceding placement update. Permanent purge authority removes every
replica, including all-regions placement; branches remain independent. A 204
response records logical deletion and local cleanup. Remote erasure is retried
durably, so 204 does not guarantee synchronous remote erasure. An empty replica-list
update can temporarily make document writes return 409 `placement_needs_two_regions`;
it is not required for deletion and does not provide supported single-region placement.

Project, collection catalog, and database catalog IDs are canonical uppercase
26-character ULIDs. `generateId()`, `isValidId()` and `requireId()` retain that
entity-ID contract. Document point operations and transaction writes require canonical
ULIDs; the producer rejects other forms with `invalid_document_id`. SQL-backed
collections can still return their stored primary keys in query, stream and export
results. A root payload `id` is ordinary data unless it equals the
key; the producer then strips that redundant member.
Documents return
`{ id, version, state: "value", document }`; a causal read can answer
`state: "conflict"` with `siblings`, and unknown states pass through. Versions
(`tbv1-…`, `tbv2-…`) are opaque tokens. Writes return the version and a
decimal-string commit `offset`; PATCH also returns the stored `document`.
Ops PATCH supports top-level `inc`, `add` and `remove` alongside `set`, `unset`
and `append`. `inc` requires a nonzero signed Int64 integer; set elements are
JSON scalars of at most 1 KiB under the producer serializer.
Preserve those opaque positions and use
`ifMatch` or `ifNoneMatchAny` for conditional writes.

Queries support typed filters, sorting, projection and bounded cursor pages.
`query` returns one page; `pages` and `items` follow `nextCursor`.
`count.exact: false` means the count is a lower bound. The current C# producer
does not support `lower` paths or query-body `explain`; query number comparisons use its
64-bit integer/double arithmetic, even when the SDK preserves exact JSON numbers.
`ExactDecimal` and bigint serialization require Node.js 22+ or a current browser.

A `near` sort orders by vector distance, nearest first, and adds `distance` to
each item. It is the only sort key, `metric` is `cosine` (default) or `l2`,
`limit` is at most 100 and there is one page (no cursor):

```js
await users.putTtl("/expiresAt"); // getTtl() -> { path: "/expiresAt" }; deleteTtl() clears
const { items } = await docs.query({
  sort: [{ near: { path: "/embedding", vector: [0.1, 0.2], metric: "cosine" } }], limit: 10,
});
```

Geo points are `{ lat, lon }` objects (WGS 84 degrees). `v1Filter.withinRadius`
matches points within `meters` (haversine), `v1Filter.withinBox` points in a box
(`west > east` crosses the antimeridian); both nest under `and`, `or` and `not`
but shape filters reject them. A geo `near` sort orders by meters from a point,
with the same one-page, at-most-100 rule as the vector `near`:

```js
const nearby = await places.query({
  filter: v1Filter.withinRadius("/loc", { lat: -23.55, lon: -46.63, meters: 1000 }),
  sort: [{ near: { path: "/loc", lat: -23.55, lon: -46.63 } }], limit: 10,
}); // each item carries distance in meters
```

`aggregate` answers `groups` instead of items. `groupBy` takes 0-4 paths;
`sum`, `avg`, `min` and `max` take at most 16 JSON Pointers in total and
consider numbers only (`sum` is 0 and the others null without any). `count`
is a decimal string. It takes no `sort`, `project`, `cursor` or `count`, and
`limit` bounds the groups (more answer 400 `invalid_query`):

```js
const { groups } = await orders.query({
  filter: v1Filter.gte("/created", 1761264000000),
  aggregate: { groupBy: ["/status"], sum: ["/amount"], avg: ["/amount"] }, limit: 100,
}); // [{ key: ["paid"], count: "250", sum: { "/amount": 12500.5 }, avg: { "/amount": 50.002 } }]
```

A collection TTL deletes documents whose member holds a past RFC 3339 UTC time.
An hourly home-region sweep does the delete, up to about an hour late; until
then expired documents stay readable.

`db.transaction()` builds one bounded atomic group across collections in that
database. It supports document operations, `assert`, `assertAbsent` and
`assertCount`; assertions examine the state before the group's writes.
The C# producer rejects `Idempotency-Key`. Do not enable automatic idempotency
or replay mutations after an uncertain result; this release does not promise
mutation deduplication. Validation, authentication, authorization and
precondition failures are not automatically retried.

Relational SQL runs statements on tables created with `CREATE TABLE` (rows
are documents). `execute` runs one statement, `query` returns its rows as
objects keyed by column name, and `batch` sends 1 to 256 statements as one
transaction: all commit or none do. `BEGIN`, `COMMIT` and `ROLLBACK` are
refused. Arguments are positional (`?`) or named (`:name`, `@name`, `$name`,
passed without the prefix). Each is `null`, a number, an int64 `bigint`, a
boolean (stored as 1 or 0) or a string. Reads need `documents:read`; writes and
DDL also need `documents:write`.

```js
await db.batch([
  "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
  { sql: "INSERT INTO people (id, name) VALUES (?, ?)", args: [1, "Ada"] },
  { sql: "INSERT INTO people (id, name) VALUES (:id, :name)", named: { id: 2, name: "Grace" } },
]);
const people = await db.query("SELECT id, name FROM people WHERE id > ?", [0]);
const { columns, rows, rowsAffected, lastInsertRowid } = await db.execute("DELETE FROM people WHERE id = :id", { id: 2 });
```

A refused statement throws `SqlError`, a `TerraBaseV1Error` that adds
`statement` (zero-based index) and `sqliteCode` (such as
`SQLITE_CONSTRAINT_PRIMARYKEY`). Its `code` is `sql_error` (400),
`forbidden_scope` (403), `sql_conflict` (409) or `sql_too_large` (413).
Authentication, quota and availability errors keep their usual error classes.

TerraBase sends integers as JSON numbers. A plain JavaScript number cannot hold
an integer above 2^53 exactly, so the SDK decodes such a value (a row value or
`lastInsertRowid`) as an `ExactDecimal` that keeps every digit. Convert it with
`BigInt(String(value))`. Pass large integer arguments as `bigint`.

The older `db.sql` route below is read-only.
SQL is the producer's bounded read-only subset: `{ matched, truncated, rows }`,
with row `{ id, value }` and no pagination cursor. `checkSql` returns `ok: false`
for invalid SQL; `explainSql` returns an HTTP error for invalid SQL. All three
require `databases:read`, granted by a project access JWT.

```js
const result = await db.sql("SELECT * FROM todos LIMIT 10");
const controller = new AbortController();
for await (const event of db.changes({ cursor: "now", collections: ["todos"] }, {
  signal: controller.signal,
})) {
  console.log(event);
  // Stop with controller.abort(), or break to close this iterator.
}
```

SSE emits `cursor` and `change` events. Cursors are opaque and must retain the
same collection filter. `lastEventId` takes precedence over `cursor`; normal
valid EOF reconnects with the latest issued cursor. Delivery is at least once;
deduplicate by origin, version, collection and id. `document` is its current
serving value, including null. Network failures, malformed streams, terminal
errors, denials, expired history (410) and overload (429) propagate. The SDK
never silently resets to `now` or broadens the view. Iterator `return()` also
interrupts an already pending read.

`db.export()` streams every live document as `{ collection, id, document }`,
in collection then id order, from one read snapshot (`documents:read`). Lines
are parsed as they arrive. A stream that ends inside a line throws; resume with
the last whole document received, which reads a new snapshot:

```js
let last;
for await (const row of db.export({ after: last, signal: controller.signal })) {
  last = { collection: row.collection, id: row.id };
}
```

The exported collection `shape()` reads the current free shape protocol using
`documents:read`. A retained historical `shapes:read` route descriptor does not
qualify that current protocol. SSE changes are a separate database
feed, not authorized bounded-query snapshots or the planned reactive WebSocket API.

## Free shape subscriptions (protocol v1)

`collection.subscribeShape({ where? }, { onUpdate?, signal?, replica? })` follows a collection's
sealed snapshot, log chunks, and live long-polls. Its `state` is a map from document
id to `{ version, document }`; `upToDate` becomes true only after a fresh response
reports `up_to_date`. Each callback sees the complete applied response or a
freshness change; freshness is cleared while a changed-lineage resume is pending.

```js
const shape = todos.subscribeShape({ where: v1Filter.eq("/done", false) }, {
  onUpdate(subscription) {
    console.log(subscription.state, subscription.upToDate);
  },
});
try {
  await shape.done; // Runs until close(), cancellation, or a terminal error.
} finally {
  shape.close();
}
```

`close()` cancels a pending read. Server errors reject `done` using the existing
TerraBase error types; malformed responses also stop it. A lost connection
(a fetch rejection or a body failing in transit) keeps state and retries the same
position with backoff, because shape reads are replay-safe `GET`s.
Observe `done` to detect terminal errors. A throwing `onUpdate` stops the
subscription and rejects `done` with the callback's error; already applied state
remains available. Calling `close()` or aborting takes precedence over a later
callback exception and resolves `done`. Document
upserts replace the prior value and deletes of unknown ids are harmless. Repeated
events are applied: a sealed value can be newer than its event, including after
failover. There are no durable local writes or offline mutation queue: every
write goes to the server, and a live subscription delivers it back.

Pass a token provider (`token: () => getAccessToken()`) to keep a subscription
alive across token renewal: each long-poll asks it for the current token. A
`401` or `403` still stops the subscription.

### Read replica

`replica: { load, save }` keeps the last resumable state as exact JSON text. On
start the subscription restores those documents with `upToDate` false, calls
`onUpdate`, and catches up from the saved handle and offset; while offline it
keeps retrying and the restored state stays readable. A `409 must_refetch`
clears the replica with the state. A replica saved for another database,
collection or filter is ignored. `indexedDbShapeReplica(key)` stores it in
browser IndexedDB; use one key per signed-in subject and shape, and clear it on
sign-out. A failing store rejects `done`.

```js
import { indexedDbShapeReplica } from "@terrascale/sdk";
const open = todos.subscribeShape({ where: v1Filter.eq("/done", false) }, {
  replica: indexedDbShapeReplica(`${userId}/todos/open`),
  onUpdate: s => render(s.state, s.upToDate),
});
```

The client preserves state for one proven cursor-resume attempt when a different
machine rejects its handle. It never resumes an incomplete snapshot. A failed
resume or another `409 must_refetch` clears state and starts a fresh snapshot.
Opaque resume tokens travel verbatim in the query, or `TB-Shape-Resume` when the
request target would exceed the protocol limit. Only documented transient
`shape_busy`, `unavailable`, and `shape_too_large` errors and lost connections
retry with backoff and `Retry-After` until success or cancellation;
authorization and validation errors stop the subscription. The backoff delay is capped at 30 seconds
unless `Retry-After` requires longer. The maximum signed-64-bit live bucket is
accepted as a response, as §4 allows, but cannot be sent as the next input;
`done` rejects with a `TypeError` if that protocol limit is reached.

Origin JSON with a leading UTF-8 BOM is rejected by the v1 transport, including
historical routes; shape v1 §6 requires bytes without a BOM.
Subscriptions use unconditional reads; they do not send `If-None-Match` or use
the optional `304` cached-body path.

`collection.decryptShapeObject(bytes, { handle, offset, epoch, keyId }, { signal? })`
fetches the matching epoch key with `cache: "no-store"`, derives the per-object key
with WebCrypto HKDF, and verifies every AES-256-GCM TBS1 frame before returning a
page. Keys are cached only in process memory by `(handle, epoch, keyId)`, scoped
to the credential, database and collection. A key fetch bypasses HTTP caches;
keys are never persisted. Past epochs are evicted on access as time or the
latest served epoch advances; historical objects fetch their keys again without
retaining them. Any `409 must_refetch` on the shape or key route clears that
collection's key cache. A failed tag, binding, frame, or response validation
applies nothing. Key ids follow §12's 26 ASCII bytes without assuming a ULID.
Native browser Fetch hides a manual `307` behind an opaque redirect, so in a
browser the bootstrap is sent again with `redirect: "follow"` and the final URL
is held to the same same-route `Location` rules. TBS1 decryption uses WebCrypto
in either runtime.

This implements the [proposed protocol](contracts/shape-v1/shape-reads.md) at
`993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`, using `documents:read`. HTTP fakes and the
upstream [conformance vectors](test/fixtures/shape-vectors.json) verify client
behavior; they do not qualify a producer deployment. Isolated production checks
on 2026-10-26 verified shape bootstrap, continuation, manifest and epoch-key reads,
live subscription updates, and checkpoint restore. Full current generated
OpenAPI and semantic-corpus parity, sealed-object delivery/decryption, and
automatic region discovery remain open qualification gates. These collection
reads do not implement the planned bounded-query WebSocket result protocol.
No TerraBase binary is downloaded or run by SDK tests.

Failures throw `TerraBaseV1Error` with status, code, optional transaction `op`,
parsed `body`, raw `responseText`, response `headers`, and a received
`X-Request-ID` as `requestId`. The actual producer uses `application/json`
`{error:{code,message,op,details}}`, not RFC 9457. The SDK preserves RFC 9457
bodies when received but does not invent producer support or request IDs.
Cancellation preserves the supplied `AbortSignal` reason.

**Browser gate remains red:** Chrome and Edge native fetch resend a buffered
mutation once after HTTP 421. Node uses one-shot request streams to prevent that
replay. Browser Fetch has no retry-control option; its one-wire assertions remain
failing in this draft. See the audit for exact results and producer dependencies.

## Ouroboros and qualification

Ouroboros means TerraScale services store their data in the same production
TerraBase that customers use. Qualification requires an authorized, isolated
real-service allocation, independently verified producer release provenance,
and current generated-contract and route-manifest parity. HTTP fakes, local
SQLite, historical contract inputs and typed builds cannot establish deployed
producer compatibility.

This SDK repository does not download or launch a TerraBase binary. The
historical [release pin](scripts/ci/terrabase-release.json) does not qualify the
current producer or JWT authorization. Keep real HTTP tests environment-gated;
missing live configuration is skipped, never successful integration evidence.

Existing live tests consume `TB_V1_URL`, `TB_V1_PROJECT`, `TB_V1_DATA_KEY` and
optionally `TB_V1_DATABASE` (default `app`) from an independently provisioned
allocation. Denial checks use `TB_V1_READ_KEY` and `TB_V1_WRITE_KEY`. Supply
credentials privately; never commit keys, cookies, environment files or real
tenant data. Local checks below do not substitute for this live gate.

Local checks run at low CPU priority:

```sh
nice -n 19 npm ci
nice -n 19 npm run typecheck
nice -n 19 npm run lint
nice -n 19 npm run build
nice -n 19 npm test
```

`lint` reuses the strict TypeScript/JSDoc check, including unused-code checks,
without adding a linter dependency. CI runs these commands at normal priority.
See [CHANGELOG.md](CHANGELOG.md) for the 0.0.1 migration and breaking changes.

## TypeScript document models

Node-only `@terrascale/sdk/codegen` generates body aliases from declared JSON
Schema, using `generateDocumentModels(schema, "Order")` or the authorized
`generateCollectionModels(collection, "Order", { signal })` operation.
Install its exact tooling peer separately:

```sh
npm install --save-dev json-schema-to-typescript@16.0.0
npx terrascale-models schema.json Order models.ts
```

The input is the schema itself, without the API response wrapper. Output has
`OrderDocument`, `OrderCreate` and `OrderPatch`; keep codegen out of browser
bundles. Generated aliases do not validate or automatically narrow SDK reads.
See [TypeScript document models](https://docs.terrascale.tech/guides/schema-validation#generate-typescript-document-models)
for the supported subset, merge-patch null semantics and strict compiler setup.

## Separate and deprecated entrypoints

`@terrascale/sdk/management` exports `createTerraScaleManagementClient` with the
root client's project credentials. It exposes `databases`, `database(name)`,
`metrics`, `backupDestination` and `savedQueries`; database handles include
backups, imports, indexes and schema. Collection `previewSchema(schema, { cursor })`
observes a bounded page of existing documents without reserving a schema change.
Repeat identical schema bytes with `nextCursor`; a stale preview must restart.

The same entrypoint exports `createTerraScaleIdentityManagementClient({ apiBaseUrl,
signInToken })` for named organization, project, member and API-key operations.
Use the Authentication origin and the platform sign-in JWT, separately from
project access tokens. Results use `{ ok, value, status }` or `{ ok: false, error,
status }`; pages accept `limit/after` and return `items/next`. The service enforces
roles. Mutations are never replayed automatically; deleteProject's 202 means
pending closure. Store create/rotate key secrets securely when returned once.
Identity operations also cover invitations, ownership transfer, SSO, organization
MFA policy, audit and account lifecycle. `createTerraScaleAccountDeletionClient({
apiBaseUrl })` checks deletion status/history using an explicit receipt capability
after sign-in stops working. Logical deletion does not certify physical erasure.
`createTerraScaleProjectIdentityClient({ apiBaseUrl, project, projectApiKey })`
mints end-user tokens through the public assertion contract; keep the project key
on your backend and give browsers only the issued short-lived end-user token.

`@terrascale/sdk/admin` exports `createTerraScaleAdministrationClient({ baseUrl,
token })` with named `databases`, `collections`, `documents` and `document` reads.
The Database service requires `platform_role=admin`. The old `requireTerraScaleManagement`
and `requireTerraScaleAdministration` names are deprecated construction aliases
that now require the corresponding client configuration.
See the [operation map](docs/evidence/management-operation-map.md) for contract
pins and omitted producer operations. Durable schema plans, public billing REST
and administration mutations still need their owning public contracts.

`@terrascale/sdk/ts-auth` retains its separate hosted identity management and OIDC
client, with its own origin, instance and credentials. A server-only Better Auth
database adapter is planned; the package exports no `./better-auth` entrypoint.
The [public auth primitives and durable receipts](docs/evidence/better-auth-producer-audit.md)
require replication design before adapter work resumes. Existing adapter tooling
pins Better Auth 1.7.2; this does not establish a supported production adapter.
The optional live catalog check needs `TB_V1_MANAGEMENT_TOKEN` with `databases:read`
in addition to `TB_V1_URL` and `TB_V1_PROJECT`; document-only keys are insufficient.
Webhook dead-letter replay uses the documented `endpointId:eventId` delivery
selector; both references must be canonical ULIDs. Entity pages reject null and
primitive records; recovery-code pages contain strings.

Hosted identity and OIDC network methods accept a final `{ signal }` argument;
list methods keep it in their existing options. Failures retain `kind: "transport"`:
`request_aborted` identifies caller cancellation, `request_timeout` the SDK deadline,
and `transport_error` an ordinary transport failure. Abort details carry a sanitized cause.

`@terrascale/sdk/local` (including `terrascale-local`) and `@terrascale/sdk/postgres` are
**deprecated legacy entrypoints**. The operating contract confines fake/local
stores to test code and test binaries. Their current exported runtime paths
remain inventoried for removal; this docs change does not remove them.
Do not use them for application onboarding, production, staging or producer
qualification. A local adapter test does not qualify the real C# API.
The PostgreSQL listener and retired tenant/shard client are outside the default
v1 API. Existing framework, schema and SQL entrypoints are separate legacy
surfaces; the root entrypoint exposes the C# v1 client API.

Hosted OAuth resource requests use one exact registered HTTPS `resource`. `oidc.authorizationUrl`, `oidc.deviceAuthorization`, and the code, device, refresh, and client-credentials token grants retain its bytes, including path and query. Omission on a browser code or refresh flow preserves the stored no-resource binding; it never repairs a rejected audience or falls back to browser login. Refresh requests can ask for narrower scopes. Client credentials require explicit resource and scope values. Token and originating-client introspection methods use `client_secret_post`; public-client and originating-client Basic token authentication are not currently exposed by this SDK.

`oauthClients` and `oauthResources` provide the producer's snake_case policy records and named management operations. Resource PATCH requires `expected_auth_epoch`; client PATCH accepts it. JSON epochs outside JavaScript's exact integer range are rejected. Client PATCH returns a bare client view unless `rotate_secret` is true, when it returns the client and its one-time secret. Resource create/PATCH always return the resource and nullable one-time introspection secret.

Use `oidc.introspectResource` with independently configured `issuer`, exact `audience`, `introspection_client_id`, and `introspection_secret`. It uses resource Basic authentication only, with each credential component form encoded before Base64. An active result requires the exact configured issuer/audience, `Bearer`, integer timestamps valid at the local clock with no default skew, and the required user/service session shape. Inactive results contain only `active: false`. Resource results expose no internal epochs, refresh family, or token-use fields. The provider checks current durable token/session/resource state; an active principal or scope does not grant project ownership or management authority. Product authorization remains at the caller's existing authorization boundary. ts-auth has one global issuer per tenant, `{apiBaseUrl}/{slug}` (the platform issuer is `https://auth.terrascale.tech/terrascale`, with no regional host and no trailing slash); every call goes to `apiBaseUrl`. Platform tokens for TerraBase use audience `https://api.terrascale.tech` and only the scopes `databases:read`, `databases:write`, `documents:read` and `documents:write`, sent as one space-separated `scope` string.
