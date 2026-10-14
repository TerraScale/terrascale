# @terrascale/sdk

The JavaScript/JSDoc SDK for the project-scoped C#/.NET TerraBase server
(`TerraScale/terrascale`). Current source compatibility is pinned at
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
is in [sdk-v1-route-manifest.json](sdk-v1-route-manifest.json), with 25 current
source-backed routes and one retained historical shape route. Current generated
OpenAPI and immutable release qualification remain open gates.

| Surface | Public methods |
| --- | --- |
| Documents | `collection.get/put/create/patch/delete/list/query/pages/items` |
| Collections and declared JSON Schema | `db.collections.create/get/list/drop`; `collection.getSchema/putSchema/deleteSchema` |
| Databases | `client.databases.create/list/get/update/delete/branch` |
| SQL | `db.sql/checkSql/explainSql` |
| Changes | `db.changes` (SSE async iterator) |

Project, collection catalog, and database catalog IDs are canonical uppercase
26-character ULIDs. `generateId()`, `isValidId()` and `requireId()` retain that
entity-ID contract. Document keys independently accept 1–256 UTF-16 code units,
without controls or lone surrogates; natural keys, slashes and Unicode travel as
one query parameter. A root payload `id` is ordinary data unless it equals the
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

`db.transaction()` builds one bounded atomic group across collections in that
database. It supports document operations, `assert`, `assertAbsent` and
`assertCount`; assertions examine the state before the group's writes.
The C# producer rejects `Idempotency-Key`. Do not enable automatic idempotency
or replay mutations after an uncertain result; this release does not promise
mutation deduplication. Validation, authentication, authorization and
precondition failures are not automatically retried.

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

The exported collection `shape()` remains compatible with the historical
`6cb9745…` release. Its route is absent from the audited current server source;
do not use it as current producer evidence. SSE changes are a separate database
feed, not authorized bounded-query snapshots or the planned reactive WebSocket API.

## Free shape subscriptions (protocol v1)

`collection.subscribeShape({ where? }, { onUpdate?, signal? })` follows a collection's
sealed snapshot, log chunks, and live long-polls. Its `state` is a map from document
id to `{ version, document }`; `upToDate` becomes true only after a fresh response
reports `up_to_date`. Each callback sees the complete applied response.

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

`close()` cancels a pending read. Terminal errors reject `done` using the existing
TerraBase error types; attach a handler when starting a subscription. Document
upserts replace the prior value and deletes of unknown ids are harmless. Repeated
events are applied: a sealed value can be newer than its event, including after
failover. There are no durable local writes or offline mutation queue.

The client preserves state for one proven cursor-resume attempt when a different
machine rejects its handle. It never resumes an incomplete snapshot. A failed
resume or another `409 must_refetch` clears state and starts a fresh snapshot.
Opaque resume tokens travel verbatim in the query, or `TB-Shape-Resume` when the
request target would exceed the protocol limit. Only documented transient
`shape_busy`, `unavailable`, and `shape_too_large` errors retry with backoff and
`Retry-After`; authorization and validation errors stop the subscription.

`collection.decryptShapeObject(bytes, { handle, offset, epoch, keyId }, { signal? })`
fetches the matching epoch key with `cache: "no-store"`, derives the per-object key
with WebCrypto HKDF, and verifies every AES-256-GCM TBS1 frame before returning a
page. Keys are fetched for each object, including historical epochs; they are not
persisted. A failed tag, binding, frame, or response validation applies nothing.
Native browser Fetch hides manual redirect metadata, so subscription bootstrap
currently requires server fetch. TBS1 decryption uses WebCrypto in either runtime.

This implements the [proposed protocol](contracts/shape-v1/shape-reads.md) at
`993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`, using `documents:read`. HTTP fakes and the
upstream [conformance vectors](test/fixtures/shape-vectors.json) verify client
behavior; they do not qualify a producer deployment. Current generated OpenAPI,
live endpoint availability, and automatic region discovery remain unqualified.
No TerraBase binary is downloaded or run by these tests. The existing historical
`shape()` method keeps its contract. Manifest/object delivery and 302 redirects
are deferred to FR-22; vector `presigned-manifest` is explicitly skipped until its
invalid JSON binding escape is corrected upstream.

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
TerraBase that customers use. SDK boundary tests must run the real C# TerraBase
release binary, pinned by immutable version and SHA-256, with an isolated
allocation. A fixture, mocked transport, SQLite SDK runtime, frozen Rust
contract archive or typed build cannot establish producer compatibility.

The v1 route manifest and producer contract evidence must agree with the C#
release under test. Keep live tests environment-gated; report missing local
configuration as skipped, never as successful integration evidence. Required
release qualification must supply the verified binary and real producer inputs.
Never commit keys, cookies, environment files or real tenant data.

The immutable release pin is
[`scripts/ci/terrabase-release.json`](scripts/ci/terrabase-release.json): C#
TerraBase 0.1.0, release `ci-6cb97456b9b52a92a61856601cc025c4f2833518-run-16729`,
with binary SHA-256
`cc2db35b18c385c6d58036c6c94e59460b0e12b0d5616da2222713efbe666700`.
This official prerelease contains the qualified TS-754 ULID producer, the renamed
`TerraBase` assembly and `src/TerraBase` sources. Its asset name remains `terrabase-linux-amd64`.
The SDK integration harness consumes its official asset read-only when the
version, source revision, size and SHA-256 are verified.
The acquisition script verifies the official release, revision, asset size and
digest. Private downloads require `FORGEJO_TOKEN`. Missing or mismatched official
assets block qualification rather than selecting older bytes.

```sh
nice -n 19 npm run provision:terrabase
export TERRABASE_BINARY=/absolute/path/printed/by/the/provisioner
nice -n 19 npm run test:integration
```

The integration runner rechecks the binary digest, starts an isolated TLS server,
verifies its version and runtime OpenAPI digest, and provisions a disposable
organization, project, database, collections and scoped keys. It supplies the
`TB_V1_*` environment and trusted local certificate to the real-server tests and
cleans up the process and allocation afterwards. No TLS validation is disabled.
Direct live test configuration uses `TB_V1_URL`, `TB_V1_PROJECT`,
`TB_V1_DATA_KEY` and optionally `TB_V1_DATABASE` (default `app`);
`TB_V1_REQUIRED=1` makes absent integration inputs fail rather than skip.

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

## Separate and deprecated entrypoints

`@terrascale/sdk/ts-auth` retains its separate hosted identity management and OIDC
client, with its own origin, instance and credentials. Management,
administration and production Better Auth capabilities remain subject to their
explicit admission errors; they are not a shortcut into the database transport.
Webhook dead-letter replay uses the documented `endpointId:eventId` delivery
selector; both references must be canonical ULIDs. Entity pages reject null and
primitive records; recovery-code pages contain strings.

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

Use `oidc.introspectResource` with independently configured `issuer`, exact `audience`, `introspection_client_id`, and `introspection_secret`. It uses resource Basic authentication only, with each credential component form encoded before Base64. An active result requires the exact configured issuer/audience, `Bearer`, integer timestamps valid at the local clock with no default skew, and the required user/service session shape. Inactive results contain only `active: false`. Resource results expose no internal epochs, refresh family, or token-use fields. The provider checks current durable token/session/resource state; an active principal or scope does not grant project ownership or management authority. Product authorization remains at the caller's existing authorization boundary. Behind the Atlas proxy `apiBaseUrl` `https://auth.terrascale.tech`, ts-auth's issuer is regional, `https://<region>.auth.terrascale.tech/<slug>` (for example `https://gru.auth.terrascale.tech/<slug>`); configure that issuer, not the proxy host. Resource introspection then calls the issuer's regional host directly, because Atlas could rank another region, which is another issuer; `useReverseProxy` keeps it on the proxy.
