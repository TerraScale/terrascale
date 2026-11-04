# @terrascale/sdk

The JavaScript/JSDoc SDK for the project-scoped C#/.NET TerraBase server
(`TerraScale/terrascale`): SQL over HTTPS and WebSocket, live SQL table shapes and the database catalog.
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

## Quickstart: SQL

```js
import { createTerraBaseClient } from "@terrascale/sdk";

const db = createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT,
  token: process.env.TERRASCALE_TOKEN,
}).database("app");

await db.batch([
  "CREATE TABLE IF NOT EXISTS todos (id TEXT PRIMARY KEY, title TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0)",
  { sql: "INSERT INTO todos (id, title) VALUES (?, ?)", args: ["01JC0000000000000000000000", "Ship it"] },
]);

/** @type {{ id: string; title: string; done: number }[]} */
const todos = await db.query("SELECT id, title, done FROM todos WHERE done = :done", { done: 0 });
```

`query` returns rows as objects, `execute` returns `{ columns, rows, rowsAffected, lastInsertRowid }`, and `batch`
runs 1 to 256 statements as one transaction over HTTPS.

For many statements in a row, or a transaction that reads before it writes, open a WebSocket session:

```js
await using socket = await db.connect();
await socket.transaction(async tx => {
  const [todo] = await tx.query("SELECT done FROM todos WHERE id = ?", ["01JC0000000000000000000000"]);
  if (todo?.done === 0) await tx.execute("UPDATE todos SET done = 1 WHERE id = ?", ["01JC0000000000000000000000"]);
});
```

`connect()` works in Node 22+, Bun, Deno and browsers. Node, Bun and Deno send the token in the upgrade's
`Authorization` header; browsers, which cannot set WebSocket headers, send it as the first frame. It never goes in
the URL. In a browser use an end-user token, never a project key. A socket transaction lives at most 5 seconds,
commits when the function resolves and rolls back when it throws.

A refused statement throws `SqlError` with `code` (a `SqlErrorCode` such as `unique_violation`), `sqlstate`
(`23505`), `statement` (its zero-based index) and `retryable` (true for `serialization_conflict`, `tenant_busy` and
`unavailable`). A closed socket rejects pending requests with `SqlSocketClosedError` and its `closeCode`.
Values are `null`, numbers, strings and `Uint8Array` blobs; integers beyond 2^53 arrive as `ExactDecimal`
(`BigInt(String(value))`) and go out as `bigint`.

For live data, `db.shape("todos").subscribe(({ rows }) => …)` keeps every row of a table current (see
[Live SQL table shapes](#live-sql-table-shapes)).

## Databases for agents

On your server, pass `apiKey` instead of `token`. The key is exchanged at ts-auth for a project access token
on each request; the raw key goes only to `https://auth.terrascale.tech`. Keep keys out of browser code and logs.

```js
const client = createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT,
  apiKey: process.env.TERRASCALE_API_KEY,
});
const project = process.env.TERRASCALE_PROJECT;
const run = await client.createDatabase(project, "agent_run", { ttlSeconds: 3600 });
const fork = await client.forkDatabase(project, run.id, "agent_fork", { ttlSeconds: 600 });
const key = await client.createScopedKey({ project, database: fork.id, access: "read_write", ttlSeconds: 600 });
const agent = createTerraBaseClient({ baseUrl: "https://api.terrascale.tech", project, apiKey: key.key });
await agent.database(fork.id).execute("CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)");
await client.setDatabaseTtl(project, run.id, 7200);
await client.clearDatabaseTtl(project, run.id);
await client.deleteDatabase(project, run.id);
```

`forkDatabase` also accepts `{ pointInTime: "2026-11-04T12:00:00Z", ttlSeconds: 600 }` for a restore-window
snapshot. A fork does not inherit its source's TTL. Database results include `expiresAt` (RFC 3339 or null).
Database TTLs are whole seconds from 60 to 31,536,000; expiration deletes the database, backups and data keys.
Scoped keys require a database ULID, `read` or `read_write` access, and optionally 60 to 2,592,000 seconds
(default 3600, capped by the parent key's expiry). Organization keys require `project`; project keys may omit it.
The key is returned once. Revoking or rotating its parent revokes it. Database management requires `databases:write`;
scoped keys cannot manage databases or mint more keys. Mutations are sent once, and ts-auth refusals preserve
`TerraBaseV1Error` status, code, body and Retry-After.

The [agent route manifest](sdk-agent-route-manifest.json) and synthetic recorded contract fixtures cover encoding,
decoding and refusals against [the shared contract](../SQL-API.md#databases-for-agents). Live producer qualification
for these methods has not run; this evidence does not establish deployed TTL deletion or key isolation.

## Placement

```js
const db = await client.createDatabase(project, "orders", { homeRegion: "gru", replicaRegions: ["sjc"] });
await client.setDatabasePlacement(project, db.id, { allRegions: true });
await client.moveDatabaseHome(project, db.id, "sjc");
```

Databases carry `homeRegion`, `replicaRegions` and `allRegions`. `setDatabasePlacement` takes exactly one of
`replicaRegions` or `allRegions`. `moveDatabaseHome` waits for the move without a client timeout and resends only on
`503 move_unresolved` (at most 5 times, after `Retry-After` or 1 s). Encoding and decoding are covered by the
[recorded placement fixtures](../dotnet/contracts/terrabase/placement/); live producer qualification has not run.

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
Project catalog operations omit `database`; each project or database session
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

## SQL and catalog

The database catalog routes are in [sdk-v1-route-manifest.json](sdk-v1-route-manifest.json), checked against the
pinned producer handler sources; the full public route inventory is
[sdk-public-v1-route-manifest.json](sdk-public-v1-route-manifest.json). Current generated OpenAPI and immutable
release qualification remain open gates.

| Surface | Public methods |
| --- | --- |
| SQL over HTTPS | `db.execute/query/batch` |
| SQL over WebSocket | `db.connect()` |
| Live table shapes | `db.shape(table)` |
| Databases | `client.databases.create/list/get/rename/delete/branch` |
| Backups and usage | `db.backups`, `db.metrics`, `client.metrics`, `client.backupDestination` |
| Saved queries | `client.savedQueries` |
| File buckets | `client.storage` |

Current project routes authenticate ts-auth project access JWTs. Legacy C# `tb_` keys remain accepted for their
narrower data scopes; they cannot grant `databases:read` or `databases:write`. Credentials travel as
`Authorization: Bearer <token>` and are held privately. The SDK validates header-safe syntax; authentication and
authorization belong to the producer. SQL reads need the data read scope; writes and DDL also need the data write
scope, checked per statement. Keep service credentials private and out of logs and browser bundles.
`createTerraBaseClient` is an alias of `createTerraBaseV1Client`.

Each database is stored and served in one region. `client.databases.create({ name })` uses the called region;
`homeRegion` selects another serving region. Call `client.databases.delete(name)` in that region. A 204 response
records logical deletion and local cleanup; branches remain independent.

Project and database IDs are canonical uppercase 26-character ULIDs. `generateId()`, `isValidId()` and `requireId()`
retain that entity-ID contract; use `generateId()` for primary keys of tables that take local writes.

`execute` runs one statement, `query` returns its rows as objects keyed by column name, and `batch` sends 1 to 256
statements as one transaction: all commit or none do. `BEGIN`, `COMMIT` and `ROLLBACK` are refused over HTTPS; use a
WebSocket session for interactive transactions. Arguments are positional (`?`) or named (`:name`, `@name`, `$name`,
passed without the prefix). Each is `null`, a number, an int64 `bigint`, a boolean (stored as 1 or 0), a string or a
`Uint8Array` blob.

```js
await db.batch([
  "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
  { sql: "INSERT INTO people (id, name) VALUES (?, ?)", args: [1, "Ada"] },
  { sql: "INSERT INTO people (id, name) VALUES (:id, :name)", named: { id: 2, name: "Grace" } },
]);
const people = await db.query("SELECT id, name FROM people WHERE id > ?", [0]);
const { columns, rows, rowsAffected, lastInsertRowid } = await db.execute("DELETE FROM people WHERE id = :id", { id: 2 });
```

TerraBase sends integers as JSON numbers. A plain JavaScript number cannot hold an integer above 2^53 exactly, so the
SDK decodes such a value (a row value or `lastInsertRowid`) as an `ExactDecimal` that keeps every digit. Convert it
with `BigInt(String(value))`. Pass large integer arguments as `bigint`. `ExactDecimal` and bigint serialization
require Node.js 22+ or a current browser.

The C# producer rejects `Idempotency-Key`. Do not replay mutations after an uncertain result; this release does not
promise mutation deduplication. Validation, authentication and authorization failures are not automatically retried.

Failures throw `TerraBaseV1Error` with status, code, parsed `body`, raw `responseText`, response `headers`, and a
received `X-Request-ID` as `requestId`; a refused statement throws its `SqlError` subclass. The producer uses
`application/json` `{error:{code,message,details}}`. Cancellation preserves the supplied `AbortSignal` reason.

**Browser gate remains red:** Chrome and Edge native fetch resend a buffered mutation once after HTTP 421. Node uses
one-shot request streams to prevent that replay. Browser Fetch has no retry-control option; its one-wire assertions
remain failing in this draft. See the [HTTP 421 assessment](docs/fetch-421-assessment-20261013.md).

## Live SQL table shapes

```js
const todos = db.shape("todos").subscribe(({ rows, upToDate }) => {
  render([...rows.values()]);
});
todos.close();
```

`shape(table).subscribe()` loads every row of the table, then keeps it current with each committed change. The
listener runs after the snapshot and after every change, with `rows` keyed by row id (a decimal string) and
`upToDate` once the shape reached the table's head. Row values decode like SQL results (`Uint8Array` blobs).
A schema change of the table restarts the shape from a new snapshot; transient refusals and lost connections retry
with backoff; authentication, scope and unknown-table errors reject `done`. It needs the data read scope; end-user
tokens are not accepted yet.

```js
const open = db.shape("todos", { where: "done = 0", columns: ["id", "title"] }).subscribe(({ rows }) => render(rows));
```

`where` is a SQL condition and `columns` the columns each row carries; the SDK repeats both on every request. A row
that starts matching moves in and one that stops matching moves out, so `rows` always holds exactly the matching
rows.

## Ouroboros and qualification

Ouroboros means TerraScale services store their data in the same production
TerraBase that customers use. Qualification requires an authorized, isolated
real-service allocation, independently verified producer release provenance,
and current generated-contract and route-manifest parity. HTTP fakes, local
SQLite and typed builds cannot establish deployed
producer compatibility.

This SDK repository does not download or launch a TerraBase binary. Keep real
HTTP tests environment-gated; missing live configuration is skipped, never
successful integration evidence.

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

## Named entrypoints

`@terrascale/sdk/management` exports `createTerraScaleManagementClient` with the
root client's project credentials. It exposes `databases`, `database(name)`,
`metrics`, `backupDestination` and `savedQueries`; database handles include
SQL, backups and usage.

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
token })` with a named `databases` read.
The Database service requires `platform_role=admin`. The old `requireTerraScaleManagement`
and `requireTerraScaleAdministration` names are deprecated construction aliases
that now require the corresponding client configuration.
See the [operation map](docs/evidence/management-operation-map.md) for contract
pins and omitted producer operations. Durable schema plans, public billing REST
and administration mutations still need their owning public contracts.

`@terrascale/sdk/ts-auth` retains its separate hosted identity management and OIDC
client, with its own origin, instance and credentials. A server-only Better Auth
database adapter is planned; the package exports no `./better-auth` entrypoint.
The optional live catalog check needs `TB_V1_MANAGEMENT_TOKEN` with `databases:read`
in addition to `TB_V1_URL` and `TB_V1_PROJECT`; data-only keys are insufficient.
Webhook dead-letter replay uses the documented `endpointId:eventId` delivery
selector; both references must be canonical ULIDs. Entity pages reject null and
primitive records; recovery-code pages contain strings.

Hosted identity and OIDC network methods accept a final `{ signal }` argument;
list methods keep it in their existing options. Failures retain `kind: "transport"`:
`request_aborted` identifies caller cancellation, `request_timeout` the SDK deadline,
and `transport_error` an ordinary transport failure. Abort details carry a sanitized cause.

The retired `./local`, `./postgres`, `./schema`, `./sql`, `./tanstack` and `./codegen`
entrypoints and the `terrascale-local` and `terrascale-models` CLIs are gone. SQL runs on the root database
handle. React/Svelte exports retain generic observer, hook and store utilities. Their
retired `createDatabaseQuery` binding is removed; current bounded-query service
and framework integration remains planned, without a replacement adapter claim.

Hosted OAuth resource requests use one exact registered HTTPS `resource`. `oidc.authorizationUrl`, `oidc.deviceAuthorization`, and the code, device, refresh, and client-credentials token grants retain its bytes, including path and query. Omission on a browser code or refresh flow preserves the stored no-resource binding; it never repairs a rejected audience or falls back to browser login. Refresh requests can ask for narrower scopes. Client credentials require explicit resource and scope values. Token and originating-client introspection methods use `client_secret_post`; originating-client Basic token authentication is not currently exposed by this SDK. `oidc.refreshPublic(clientId, refreshToken)` refreshes a public client (like the CLI device client) sending `grant_type=refresh_token` with `client_id` and no client secret; ts-auth rotates the refresh token on every use, so persist the returned `refresh_token`. Confidential clients keep `oidc.token` with `client_secret_post`.

`oauthClients` and `oauthResources` provide the producer's snake_case policy records and named management operations. Resource PATCH requires `expected_auth_epoch`; client PATCH accepts it. JSON epochs outside JavaScript's exact integer range are rejected. Client PATCH returns a bare client view unless `rotate_secret` is true, when it returns the client and its one-time secret. Resource create/PATCH always return the resource and nullable one-time introspection secret.

Use `oidc.introspectResource` with independently configured `issuer`, exact `audience`, `introspection_client_id`, and `introspection_secret`. It uses resource Basic authentication only, with each credential component form encoded before Base64. An active result requires the exact configured issuer/audience, `Bearer`, integer timestamps valid at the local clock with no default skew, and the required user/service session shape. Inactive results contain only `active: false`. Resource results expose no internal epochs, refresh family, or token-use fields. The provider checks current durable token/session/resource state; an active principal or scope does not grant project ownership or management authority. Product authorization remains at the caller's existing authorization boundary. ts-auth has one global issuer per tenant, `{apiBaseUrl}/{slug}` (the platform issuer is `https://auth.terrascale.tech/terrascale`, with no regional host and no trailing slash); every call goes to `apiBaseUrl`. Platform tokens for TerraBase use audience `https://api.terrascale.tech` and only the scopes `databases:read`, `databases:write` and the two data scopes, sent as one space-separated `scope` string.

### Permanent API key errors

`401 key_revoked` and `401 key_expired` throw `V1KeyRevokedError` and
`V1KeyExpiredError` (subclasses of `V1InvalidKeyError`). Their `code` and message
are preserved. Replace the key: neither error retries or triggers token exchange.
The SDK has no exchanged-token cache; caller-owned providers must discard the
refused token and stop exchanging that key.

### Read deadline and retries

Replay-safe reads are GET operations. Each has one fixed 60-second deadline covering endpoint
resolution, the token provider, retries and the response body. Expiry rejects with a
`DOMException` named `TimeoutError`. A caller's `signal` can only shorten it and
keeps its own abort reason.

A replay-safe read is resent at most three times after a connection failure
before any response, `429 tenant_busy`/`tenant_memory`/`shape_busy`, `503
storage_busy`/`unavailable`/`auth_unavailable`/`catalog_stale`/
`remote_region_unavailable`/`confirmation_busy`, or a body-less 502, 503 or 504.
It waits at least `Retry-After`, plus exponential backoff with full jitter (up to
2 seconds), so clients refused together do not return in lockstep. A wait the
deadline cannot cover surfaces the refusal at once. Each attempt asks the token
provider again. The last refusal surfaces as the typed `TerraBaseV1Error`.
Validation, authentication, authorization, not-found, conflict, quota and other
503 refusals are never retried. Table shapes keep their own backoff.

Mutations are sent once and carry only the caller's `signal`: abandoning one
makes its outcome uncertain, and restores and branches can run long.

## Shipping from a TerraScale dev host

Commit your changes, then run `npm run ship` (or `ts-ship`) from the worktree.
It rebases onto current main, runs the Fast command from `AGENTS.md` on a dev
host, and pushes main after validation. Run `ts-main` for the latest CI results.
