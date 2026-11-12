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

Export `TERRASCALE_PROJECT_ID`, exchange `TERRASCALE_API_KEY` for a short-lived access token as shown in the [JavaScript getting-started guide](https://docs.terrascale.tech/getting-started/javascript), and export it as `TERRASCALE_ACCESS_TOKEN`.

```js
import { createTerraBaseClient } from "@terrascale/sdk";

const db = createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  token: process.env.TERRASCALE_ACCESS_TOKEN,
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
on each request; the raw key goes only to `https://auth.terrascale.tech`, or to the `authBaseUrl` you name, which an
`apiKey` needs with any other `baseUrl` or with an `endpointResolver`. Keep keys out of browser code and logs.

```js
const client = createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  apiKey: process.env.TERRASCALE_API_KEY,
});
const project = process.env.TERRASCALE_PROJECT_ID;
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
const db = await client.createDatabase(project, "orders", { regions: ["gru", "sjc"] });
await client.setDatabasePlacement(project, db.id, { regions: ["fra", "gru", "sjc"] });
```

Every region that holds a database is equal. Databases carry `regions` (every region that holds it, sorted by region
id; the order has no meaning) and `allRegions`. `createDatabase` and `setDatabasePlacement` take `regions` (at least
one, no duplicates) or `allRegions`, and not both. TerraBase picks which region writes and moves that on its own when
the set changes. `db.regions` reads each region's pull progress; an entry's `role` is `writer` for the region that
commits writes. Encoding and decoding are covered by the
[recorded placement fixtures](../dotnet/contracts/terrabase/placement/); live producer qualification has not run.

All-regions placement (`allRegions`) holds the database in every serving region, including regions added later.
These ship from the next published release; the current npm release predates them.

## Atlas and endpoint selection

The [zero-configuration operating contract](https://docs.terrascale.tech/guides/zero-configuration)
requires first-party services to discover every eligible serving region and
receive production credentials automatically, without regional activation or
manual endpoint/secret wiring. Customer project/database scope and authorized
access tokens remain required. This release has not implemented automatic Atlas
integration (TS-507); the endpoint examples show the current API boundary and
are not a first-party production setup recipe. No new resolver API is implied.

Without `baseUrl` or `endpointResolver` the client sends to
`https://api.terrascale.tech`; passing both is refused. In these examples, `discoveredOrigin` is a trusted HTTPS
origin supplied by an authorized integration, not an SDK-created value.
A resolver receives
`{ project, database?, signal }` and returns the service URL, synchronously or asynchronously.
Project catalog operations omit `database`; each project or database session
resolves once and reuses its URL.
Shared resolution expires after 30 seconds and supplies a cancellation signal
to the resolver; a later operation can retry an expired or failed resolution.
Cancelling one operation leaves other callers' shared resolution running.
HTTPS is required except for loopback HTTP in isolated tests; no regional
host is hardcoded. With an `apiKey`, a `baseUrl` other than
`https://api.terrascale.tech` or an `endpointResolver` needs `authBaseUrl`, the
bare ts-auth origin that exchanges the key; without it the client throws before
any request instead of sending the key to `https://auth.terrascale.tech`.

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
to TS-507; this release sends to `https://api.terrascale.tech` unless a URL or
resolver is supplied.

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
| Databases | `client.databases.create/list/get/rename/delete/branch`, plus `deleteIfId`/`branchIfId` guarded by the database's immutable ID |
| Backups, usage and regions | `db.backups`, `db.metrics`, `db.regions`, `client.metrics`, `client.backupDestination` |
| Functions | `db.functions.deploy/list/get/invoke/logs/delete` |
| Request logs | `db.listDatabaseLogs(status?)` |
| Project metrics | `client.metrics.metrics()` |
| File buckets | `client.storage` |

`client.metrics.usage()` includes the current month’s `readUnits`, `writeUnits` and billed `units` in `month`. Billed `units` equals `writeUnits`; SQL reads count against fair use. Database usage reports stored bytes and operation counts.

`db.listDatabaseLogs(status?)` reads the newest 100 recorded requests within 24 hours; `status` is `2xx`, `3xx`, `4xx` or `5xx`. Recording is best effort, without a cursor or export guarantee. `client.metrics.metrics()` returns the project's Prometheus 0.0.4 text exposition.

Current project routes authenticate ts-auth project access JWTs. Legacy C# `tb_` keys remain accepted for their
narrower data scopes; they cannot grant `databases:read` or `databases:write`. Credentials travel as
`Authorization: Bearer <token>` and are held privately. The SDK validates header-safe syntax; authentication and
authorization belong to the producer. SQL reads need the data read scope; writes and DDL also need the data write
scope, checked per statement. Keep service credentials private and out of logs and browser bundles.
`createTerraBaseClient` is an alias of `createTerraBaseV1Client`. A region sends `X-TerraScale-Region-Pin`, and a missing, empty, or whitespace region omits it.
On a server, `createTerraBaseClientFromEnvironment(overrides?)` reads `TERRASCALE_PROJECT_ID`, `TERRASCALE_API_KEY` and
`TERRASCALE_BASE_URL`. Without that variable, a `baseUrl` or an `endpointResolver` it sends to
`https://api.terrascale.tech`; passing both `baseUrl` and `endpointResolver` is refused. With a
`TERRASCALE_BASE_URL` other than `https://api.terrascale.tech`, pass `authBaseUrl` in the overrides.

`client.databases.create({ name })` uses the called region; `regions` names the regions that hold the database.

Every region that holds a database is equal.
A region can accept a local SQL REST write; TerraBase applies it once. The server forwards a timed-out local publish with TerraBase-Publish-Id set to the same transaction id and applies that write once. A push stream that returns NATS error 10074 is not retried, because the stream wants more copies than the cluster has. The default
`TerraScale-Durability` is `local`. REST reads can use a ready local region. Copies are eventually
consistent. REST, psql and SQL WebSocket can read a ready local copy. Postgres and WebSocket sessions
preserve read-your-writes; use the writer for REST reads that must see a previous write. All-regions
placement (`-all-regions` on `ts db create` and `ts db placement`) holds the database in every
serving region; otherwise set an explicit region list.

The [published pricing catalog](https://terrascale.tech/pricing.json) meters database writes and storage; selecting additional regions does not multiply storage billing. Deleting a database destroys its backup data
keys, making retained encrypted backups unreadable; physical cleanup is separate. Forks remain independent.

Project and database IDs are canonical uppercase 26-character ULIDs. `generateId()`, `isValidId()` and `requireId()`
retain that entity-ID contract; use `generateId()` for primary keys of tables that take local writes.

`execute` runs one statement, given as SQL text with its arguments or as one `{ sql, args }` or `{ sql, named }`
statement value (on the database, a session and inside `transaction`; any other value is refused before sending),
`query` returns its rows as objects keyed by column name, and `batch` sends 1 to 256
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
In a replica region, writes made in that region show at once; `provisional` holds the keys of rows whose values the
database's home has not confirmed yet (such inserts have keys starting with `~`). A write the home rejects disappears
from `rows`.
A schema change of the table restarts the shape from a new snapshot; transient refusals and lost connections retry
with backoff; authentication, scope and unknown-table errors reject `done`. It needs the data read scope, or an end-user token with a read grant for the selected table.
Table grants authorize access; shape filters do not replace authorization.
Snapshots and sealed catch-up chunks use the manifest’s CDN URLs. The client checks the object size, SHA-256, TBS1 header, AES-GCM frames and handle/offset binding before applying data. Epoch keys stay in the subscription and CDN requests carry no credentials. The live tail uses the authenticated origin. An unavailable or invalid cached object falls back to that offset at the origin. Browsers that hide the bootstrap redirect must fetch the first snapshot page from the origin.


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
and current generated-contract and route-manifest parity. Local SQLite and
typed builds cannot establish deployed producer compatibility.

The package downloads and launches no TerraBase binary. The journey in the
monorepo's `e2e/` drives this SDK against the TerraBase, ts-auth and dashboard
built from the same commit.

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
```

`lint` reuses the strict TypeScript/JSDoc check, including unused-code checks,
without adding a linter dependency. CI runs these commands at normal priority.
See [CHANGELOG.md](CHANGELOG.md) for the 0.0.1 migration and breaking changes.

## Named entrypoints

`@terrascale/sdk/management` exports `createTerraScaleManagementClient` with the
root client's project credentials. It exposes `databases`, `database(name)`,
`metrics` and `backupDestination`; database handles include
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

`@terrascale/sdk/ts-auth` exports the ts-auth OIDC and project client, with its
own origin and instance. A server-only Better Auth
database adapter is planned; the package exports no `./better-auth` entrypoint.
`@terrascale/sdk/ts-auth/webhooks` exports the server-only `verifyWebhook`, which checks
a Standard Webhooks `v1` delivery and returns the parsed event or throws `TsAuthWebhookError`.
The optional live catalog check needs `TB_V1_MANAGEMENT_TOKEN` with `databases:read`
in addition to `TB_V1_URL` and `TB_V1_PROJECT`; data-only keys are insufficient.
Entity pages reject null and primitive records.

`tsAuth.projects` administers one project's hosted sign-in tenant over `/v1/{project}`
with the caller's TerraScale sign-in token or project secret key: `getSettings`, `updateSettings`,
`listUsers`, `getUser`, `disableUser`, `enableUser`, `deleteUser`, `exportUsers`,
`setProvider`, `deleteProvider` (providers are listed in the settings),
`listSessions`, `revokeSession`, `listClients`, `createClient`, `updateClient`,
`rotateClientSecret` and `deleteClient`; the client write operations carry a
step-up proof.

OIDC and project network methods accept a final `{ signal }` argument. Failures retain `kind: "transport"`:
`request_aborted` identifies caller cancellation, `request_timeout` the SDK deadline,
and `transport_error` an ordinary transport failure. Abort details carry a sanitized cause.

`@terrascale/sdk/drizzle` exports `drizzleProxy(database)`, the two callbacks
Drizzle's `sqlite-proxy` driver expects, built from a root database handle. The
SDK takes no `drizzle-orm` dependency; each query is one `execute` call and a
Drizzle batch is one atomic `batch` request. `drizzle-kit push` and schema
introspection are not supported.

`@terrascale/sdk/prisma` exports `PrismaTerraScale`, a Prisma driver-adapter
factory over a root database handle. It ships in the next SDK release; until
then install it from source (this monorepo's `sdk/js`). The SDK takes no
`@prisma/*` dependency. Use it as Prisma's SQLite adapter:

```prisma
datasource db {
  provider = "sqlite"
}
```

```js
import { PrismaClient } from "@prisma/client";
import { createTerraBaseClient } from "@terrascale/sdk";
import { PrismaTerraScale } from "@terrascale/sdk/prisma";

const db = createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  token: process.env.TERRASCALE_ACCESS_TOKEN,
}).database("app");
const prisma = new PrismaClient({ adapter: new PrismaTerraScale(db) });
```

No `previewFeatures` or `url` wiring is needed with Prisma 7: the adapter is
passed to the client constructor. Queries run over the SQL API; interactive
transactions (`prisma.$transaction(async tx => …)`) run over a dedicated SQL
WebSocket session and the server ends each one after 5 seconds, so keep them
short. Database errors map to Prisma's error kinds: unique, not-null and
foreign-key violations, missing tables and missing columns surface as
P2002/P2011/P2003/P2021/P2022; everything else keeps the server's message.
Migrations are not run by Prisma: `prisma migrate dev` and `db push` (and the
shadow database they need) are not supported. Instead generate SQL with
`prisma migrate diff --from-empty --to-schema prisma/schema.prisma --script > migration.sql`
(later diffs use `--from-migrations prisma/migrations`), then apply the script
with `ts migrate` or the SQL API.

The retired `./local`, `./postgres`, `./schema`, `./sql`, `./tanstack` and `./codegen`
entrypoints and the `terrascale-local` and `terrascale-models` CLIs are gone. SQL runs on the root database
handle. React/Svelte exports retain generic observer, hook and store utilities. Their
retired `createDatabaseQuery` binding is removed; current bounded-query service
and framework integration remains planned, without a replacement adapter claim.

Hosted OAuth resource requests use one exact registered HTTPS `resource`. `oidc.authorizationUrl` and the code, device, refresh, and client-credentials token grants retain its bytes, including path and query. Omission on a browser code or refresh flow preserves the stored no-resource binding; it never repairs a rejected audience or falls back to browser login. Refresh requests can ask for narrower scopes. Client credentials require explicit resource and scope values. The token method uses `client_secret_post`; client Basic authentication is not exposed by this SDK. `oidc.refreshPublic(clientId, refreshToken)` refreshes a public client (like the CLI device client) sending `grant_type=refresh_token` with `client_id` and no client secret; ts-auth rotates the refresh token on every use, so persist the returned `refresh_token`. Confidential clients keep `oidc.token` with `client_secret_post`.

ts-auth has one global issuer per tenant, `{apiBaseUrl}/{slug}` (the platform issuer is `https://auth.terrascale.tech/terrascale`, with no regional host and no trailing slash); every call goes to `apiBaseUrl`. Platform tokens for TerraBase use audience `https://api.terrascale.tech` and only the scopes `databases:read`, `databases:write` and the two data scopes, sent as one space-separated `scope` string.

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

Built-in project API-key tokens renew in the background from 60 seconds before expiry
(or at half-life for short tokens). Concurrent callers share one exchange and continue
using a valid token; only missing, actually expired, or refused tokens require a wait.

## Region-set placement

Create and update with one to nine distinct serving region ids in `regions`, separately from `allRegions: true`
and from a rename. Response `regions` are sorted without ordering meaning; integer `placementVersion` changes
with membership or the writer and stays unchanged for an identical set. Transitional response fields are ignored.
Region status exposes every selected region in `regions`, with nullable `role`, `revision`, `lastPulledAt`,
`lagMs` and `lastVerified` (Python uses snake_case; .NET PascalCase).
A removed regional endpoint returns `421 wrong_region`; use the global API or a current selected region.

Dropping the region ordering writes can return `503 replica_not_ready` after adding requested regions.
Additions remain selected. Repeat the same request after replication (about 30 seconds for a new region).
A repeat during that move returns the same 503; a different change returns `409 move_in_progress`.
The SDK exposes `TerraBaseV1Error.code` with `V1ErrorCode.replicaNotReady` and `V1ErrorCode.moveInProgress`. Neither code is automatically retried;
general transient-503 retries do not include `replica_not_ready`, so callers can distinguish it from an outage.
The caller bounds this repeat loop to 90 seconds and propagates other errors:

`replica_not_ready` means the regions you added are stored; send the same request again. `move_unresolved` means an earlier move is unresolved; send the same request again later.

```js
const signal = AbortSignal.timeout(90_000);
for (;;) { try { await client.setDatabasePlacement(project, database, { regions: ["fra"] }, { signal }); break; }
  catch (e) { if (e.code !== "replica_not_ready") throw e; await new Promise(resolve => setTimeout(resolve, 1000)); } }
```

## Project Authentication management

Pass the project secret API key as the first credential argument to `client.projects.disableUser(secretKey, projectId, userId)`. Reads require `auth:read` and changes require `auth:write`. Step-up operations require a person’s sign-in token. Keep keys in the backend.

Project organization management supports CRUD, memberships, emailed invitations and DNS verified domains. See [the project organizations contract](../../docs/auth/project-organizations.md).
