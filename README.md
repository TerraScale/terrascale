# terrascale

The JavaScript/JSDoc SDK for the project-scoped C#/.NET TerraBase server
(`terrabase`, `origin/main`). Version 0.0.1 replaces the retired Rust client.
The package targets Node.js 22 and 24 and current Chrome, Firefox, Safari, and Edge.
Plain ES modules ship as written; `nice -n 19 npm run build` generates the
TypeScript declarations in `types/`.

```js
import { createTerraBaseV1Client, documentIdFor, v1Filter } from "terrascale";

const client = createTerraBaseV1Client({
  project: process.env.TERRABASE_PROJECT,
  token: process.env.TERRABASE_API_KEY,
  baseUrl: process.env.TERRABASE_BASE_URL,
});
const db = client.database("app");
const todos = db.collection("todos");

const id = await documentIdFor("todos", "first-task");
await todos.put(id, { title: "Use TerraBase", done: false }, { ifNoneMatchAny: true });
const doc = await todos.get(id);
await todos.patch(id, { merge: { done: true } }, { ifMatch: doc.version });

for await (const page of todos.pages({ filter: v1Filter.eq("/done", true), limit: 50 })) {
  console.log(page.items);
}
```

Provision the project, database and collection before using their handles.
API keys begin with `tb_` and travel as `Authorization: Bearer <key>`.
Reads and queries require `documents:read`; writes and transactions require
`documents:write`. Transaction assertions additionally require `documents:read`.
Keep keys private and out of logs and browser bundles. `createTerraBaseClient`
is an alias of `createTerraBaseV1Client`; it no longer accepts tenant/shard options.

## Atlas and endpoint selection

Supply exactly one of `baseUrl` or `endpointResolver`. A resolver receives
`{ project, database }` and returns the service URL, synchronously or asynchronously.
The URL is resolved once per database session and reused by its operations.
Shared resolution expires after 30 seconds and supplies a cancellation signal
to the resolver; a later operation can retry an expired or failed resolution.
Cancelling one operation leaves other callers' shared resolution running.
HTTPS is required except for loopback HTTP in local development; no regional
host is hardcoded and no implicit production endpoint is selected.

```js
import { createTerraBaseV1Client, staticBaseUrlResolver } from "terrascale";

const client = createTerraBaseV1Client({
  project: process.env.TERRABASE_PROJECT,
  token: process.env.TERRABASE_API_KEY,
  endpointResolver: staticBaseUrlResolver(process.env.TERRABASE_BASE_URL),
});
const db = client.database("app");
```

Atlas (`ts-atlas`, a TypeScript Cloudflare Worker) discovers the closest eligible
region and the location of a project/database. The resolver seam allows that
choice to stay pinned for the session. Atlas discovery integration is deferred
to TS-507; this release requires an explicit URL or caller-supplied resolver.

## Documents, queries, transactions and shapes

The C# producer owns these routes:

| Operation | Method and path |
| --- | --- |
| Documents | `GET/PUT/PATCH/DELETE /v1/projects/{project}/databases/{db}/collections/{c}/documents/{id}`; `POST …/documents` |
| Query | `POST /v1/projects/{project}/databases/{db}/collections/{c}/query` |
| Transaction | `POST /v1/projects/{project}/databases/{db}/transactions` |
| Shape | `GET /v1/projects/{project}/databases/{db}/collections/{c}/shape` |

Collection methods are `get`, `put`, `create`, `patch`, `delete`, `query`,
`pages` and `items`. New document IDs are canonical UUIDs; `documentIdFor`
generates a stable UUIDv5 from a natural key. Documents return
`{ id, version, state: "value", document }`. Writes return the version and a
decimal-string commit `offset`; preserve those opaque positions and use
`ifMatch` or `ifNoneMatchAny` for conditional writes.

Queries support typed filters, sorting, projection and bounded cursor pages.
`query` returns one page; `pages` and `items` follow `nextCursor`.
`count.exact: false` means the count is a lower bound. The current C# producer
does not support `lower` paths or `explain`; query number comparisons use its
64-bit integer/double arithmetic, even when the SDK preserves exact JSON numbers.
`ExactDecimal` and bigint serialization require Node.js 22+ or a current browser.

`db.transaction()` builds one bounded atomic group across collections in that
database. It supports document operations, `assert`, `assertAbsent` and
`assertCount`; assertions examine the state before the group's writes.
The C# producer rejects `Idempotency-Key`. Do not enable automatic idempotency
or replay mutations after an uncertain result; this release does not promise
mutation deduplication. Validation, authentication, authorization and
precondition failures are not automatically retried.

Shapes require `shapes:read` and use the collection's GET route with a `where`
filter, handle, offset and live cursor. Snapshot bootstrap and checkpoint resume
provide correctness; `must_refetch` requires discarding local state and starting
a fresh snapshot. The C# producer has no shape gatekeeper, SSE route, edge shape
token or projection support. Origin responses require private cache revalidation. Fresh shape bootstrap is
server-only in this release: browsers hide the initial 307 redirect metadata.
Browser callers need a handle and snapshot offset obtained on their server.

The same public SDK supplies this metadata on the server. Pass the position to
the browser and repeat that immutable snapshot page with the identical filter
and collection:

```js
// Server: use the public client with the caller's shapes:read key.
const snapshot = await serverCollection.shape({ where });
const position = { handle: snapshot.handle, offset: snapshot.offset };
// Browser: resume using position supplied by your application server.
const page = await browserCollection.shape({ where, ...position });
```

Failures throw `TerraBaseV1Error` with the HTTP status and producer error code;
transaction failures may also identify the failing `op`. Request cancellation
uses `AbortSignal`.

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
TerraBase 0.1.0, release `ci-0248ad3ac6fb82ac900e38005840a619e5d0beac-run-13267`,
with binary SHA-256
`8abcedaa6b8487f08ecafe4485862f1f9309154e58ac1a74eda2278b78500968`.
This official release is currently a draft: the producer's fast checks failed
and publication was skipped (terrabase #110). The SDK integration harness may
consume its official asset read-only when the version, source revision, size and
SHA-256 are verified; publication is not required. Draft status does not establish
that the producer's source qualification passed.
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

`terrascale/ts-auth` retains its separate hosted identity management and OIDC
client, with its own origin, instance and credentials. Management,
administration and production Better Auth capabilities remain subject to their
explicit admission errors; they are not a shortcut into the database transport.

`terrascale/local` (including `terrascale-local`) and `terrascale/postgres` are
**deprecated legacy entrypoints**. SQLite stays opt-in for development only and
refuses CI, production, staging, qualification and deployment environments.
It is no longer the required Dashboard backend and does not qualify the C# API.
The PostgreSQL listener and retired tenant/shard client are outside the default
v1 API. Existing framework, schema and SQL entrypoints are separate legacy
surfaces; the root entrypoint exposes the C# v1 client API.

Hosted OAuth resource requests use one exact registered HTTPS `resource`. `oidc.authorizationUrl`, `oidc.deviceAuthorization`, and the code, device, refresh, and client-credentials token grants retain its bytes, including path and query. Omission on a browser code or refresh flow preserves the stored no-resource binding; it never repairs a rejected audience or falls back to browser login. Refresh requests can ask for narrower scopes. Client credentials require explicit resource and scope values. Token and originating-client introspection methods use `client_secret_post`; public-client and originating-client Basic token authentication are not currently exposed by this SDK.

`oauthClients` and `oauthResources` provide the producer's snake_case policy records and named management operations. Resource PATCH requires `expected_auth_epoch`; client PATCH accepts it. JSON epochs outside JavaScript's exact integer range are rejected. Client PATCH returns a bare client view unless `rotate_secret` is true, when it returns the client and its one-time secret. Resource create/PATCH always return the resource and nullable one-time introspection secret.

Use `oidc.introspectResource` with independently configured `issuer`, exact `audience`, `introspection_client_id`, and `introspection_secret`. It uses resource Basic authentication only, with each credential component form encoded before Base64. An active result requires the exact configured issuer/audience, `Bearer`, integer timestamps valid at the local clock with no default skew, and the required user/service session shape. Inactive results contain only `active: false`. Resource results expose no internal epochs, refresh family, or token-use fields. The provider checks current durable token/session/resource state; an active principal or scope does not grant project ownership or management authority. Product authorization remains at the caller's existing authorization boundary.
