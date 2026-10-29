# HTTP API route table (TS-589)

This file is the single source of truth for TerraBase's public HTTP routes. Change this table in the same
pull request as a public route. Hidden legacy routes remain available during the SDK migration described below.

Status, 2026-10-07: the public route table is implemented with hidden compatibility aliases. It follows the
service ownership decided by the user on 2026-10-06
(umbrella `AGENTS.md`, "Service ownership"; TS-597):

- **ts-auth owns identity:** users, organizations, projects, memberships, roles, invitations, SSO and API keys.
  An API key is a ts-auth client credential that the SDKs exchange for a short-lived JWT (TS-598).
- **TerraBase owns data:** databases, collections, documents, transactions, queries, backups, indexes, schema
  and usage metering. Each public database belongs to a ts-auth project. Existing organization-backed catalog
  records, memberships and keys remain for hidden legacy callers; their data and storage layout are preserved.

## Conventions

1. **One hierarchy, scoped by project.** Every route lives under the ts-auth project that owns the data:

   ```text
   /v1/projects/{project}
   /v1/projects/{project}/databases/{database}
   /v1/projects/{project}/databases/{database}/collections/{collection}
   /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key?id={id}
   ```

   There is no `/management`, `/data` or organization segment. The OpenAPI tag says which group a route
   belongs to, not the URL.
2. **Path parameters.**

   | Parameter | Value |
   | --- | --- |
   | `{project}` | ts-auth project id. It must equal the token's project claim. |
   | `{database}` | Database **name**, unique in its project and never renamed (`[a-z][a-z0-9_-]{0,62}`). Management routes took the database id until now. |
   | `{collection}` | Collection name, same rules as a database name. |
   | `{index}` | Index name. |
   | `{backup}`, `{recommendation}`, `{query}` | Id of the backup, index recommendation or saved query. |
3. **Authentication: ts-auth JWTs only** (ts-auth `docs/auth/platform-contract.md` sections 3 and 6, TS-598). Every route
   except the `anonymous` ones takes `Authorization: Bearer <ts-auth access token>`. That covers users of
   ts-dashboard after a token exchange, and API keys after the SDK exchanges them through client credentials.
   TerraBase validates each token offline:
   - `typ` is `at+jwt` or `application/at+jwt`, and the token is ES256-signed by a `kid` from the issuer's JWKS;
   - `iss` is exactly the configured `auth-issuer` (`https://auth.terrascale.tech/terrascale`: ts-auth issues this one issuer in every region).
     In production `auth-audience` must include `https://api.terrascale.tech`, and `ts-terrabase` refuses to start with any other
     pair (`TerraBaseOptions.PlatformAuthIssuer`/`PlatformAuthAudience`). Setting the pair is still the user-approved activation
     of ts-auth trust (the current deployed trust must be verified);
     project tenants' issuers are never trusted: those customer end-user tokens authenticate customer
     applications, while a **project access token** for TerraBase uses the trusted platform issuer and carries `project_id`;
   - `aud` includes TerraBase's audience, and `exp` and `nbf` hold with 30 seconds of skew;
   - `project_id` (a canonical uppercase ULID) equals the route's `{project}`. A token for another project, or one without
     `project_id` (organization, sign-in and service tokens), answers `404`, the same answer as a database that
     does not exist;
   - with a `database` claim (a key narrowed to one database), only that database is reachable: every other
     `{database}` answers `404`, the project's database list holds only it, and creating another answers `404`;
   - `scope` (space-separated) holds the scope the operation needs, or the route answers `403 forbidden_scope`.

   ts-auth maps roles to scopes; TerraBase checks scopes, never roles
   on public routes. `sub` (a user id, or an API key id, its ULID) is recorded as the actor. An authorized database create enrolls a fresh project using the signed `project_id` and `org_id` claims.

   | Scope | Grants |
   | --- | --- |
   | `databases:read` | Read databases, backups, the backup policy, indexes, index recommendations, the schema, usage and saved queries; read-only SQL |
   | `databases:write` | Create, branch and delete databases; create and restore backups; set the backup policy; create and drop indexes; apply recommendations; change saved queries |
   | `documents:read` | Read collections and documents; document queries |
   | `documents:write` | Create and drop collections; write documents; transactions |

   The public contract advertises only project JWTs. Hidden compatibility paths still accept existing `tb_`
   document keys and membership tokens. `/admin/api-keys`, the internal listener's platform operator and
   `X-TerraBase-User-Id` remain available only for existing legacy workflows; they are absent from OpenAPI.
4. **Listeners.** Every route in the Management, Data and System tables is served on the client listener (TLS).
   Canonical management routes are client-only. The internal listener retains the probes, `/metrics`,
   `/admin/*` and hidden legacy management bootstrap paths. Administrative operations never appear in public OpenAPI.
5. **Groups.** Every public route has exactly one tag. Scalar shows the tags under three `x-tagGroups`:
   **Management**, **Data** and **System**. Group and tag names are the table headings below.
6. **OpenAPI metadata.** Each operation has the `operationId` from the table, a one-line `summary`, a
   `description`, request and response examples, and its errors. Every error is the `ErrorBody` envelope
   `{"error":{"code","message"}}` ([error handling](error-handling.md)). Each status lists the `code` values the
   operation can return.
7. **Unchanged by TS-589:** response bodies, status codes, region pinning (`X-TerraScale-Region-Pin`, `421`,
   [region identity](region-identity.md)), conditional headers, and pagination. Data routes page with
   `limit`/`cursor` → `next_cursor` and use snake_case bodies (Documents API v1). Management routes page with
   `limit`/`after` → `next` and use camelCase bodies.

Path prefixes used in the tables:

| Prefix | Path |
| --- | --- |
| `P` | `/v1/projects/{project}` |
| `D` | `P/databases/{database}` |
| `C` | `D/collections/{collection}` |

The **Old route** column uses `MP` = `/v1/management/organizations/{organization}/projects/{project}` and
`MD` = `MP/databases/{database-id}`. A dash means that the route is new. Data routes keep their paths.

## Management

### Databases

This is the public way to create a database. The old data-plane `PUT D` remains hidden for legacy callers.

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| POST | `P/databases` | `createDatabase` | `databases:write` | `MP/databases`, and hidden legacy data-plane `PUT D` |
| GET | `P/databases` | `listDatabases` | `databases:read` | `MP/databases` |
| GET | `D` | `getDatabase` | `databases:read` | `MD` |
| PATCH | `D` | `updateDatabase` | `databases:write` | `MD` (replica regions, TS-643) |
| DELETE | `D` | `deleteDatabase` | `databases:write` | `MD` |
| POST | `D/branches` | `branchDatabase` | `databases:write` | `MD/branches` |

### Backups

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| POST | `D/backups` | `createBackup` | `databases:write` | `MD/backups` |
| GET | `D/backups` | `listBackups` | `databases:read` | `MD/backups` |
| POST | `D/backups/{backup}/restore` | `restoreBackup` | `databases:write` | `MD/backups/{backup}/restore` |
| GET | `D/backup-policy` | `getBackupPolicy` | `databases:read` | `MD/backup-policy` |
| PUT | `D/backup-policy` | `setBackupPolicy` | `databases:write` | `MD/backup-policy` |
| POST | `D/point-in-time/plan` | `planPointInTime` | `databases:read` | `MD/point-in-time/plan` (TS-493) |
| GET | `P/backup-destination` | `getBackupDestination` | `databases:read` | `MP/backup-destination` |
| PUT | `P/backup-destination` | `setBackupDestination` | `databases:write` | `MP/backup-destination` |
| DELETE | `P/backup-destination` | `deleteBackupDestination` | `databases:write` | `MP/backup-destination` |
| GET | `D/point-in-time` | `getRestoreWindow` | `databases:read` | `MD/point-in-time` |
| POST | `D/point-in-time/restore` | `restorePointInTime` | `databases:write` | `MD/point-in-time/restore` |

TS-635 adds one customer S3-compatible destination per project and explicit restore source selection.
Destination configuration requires a project-wide token; a token narrowed to a database receives 404.
Window reads accept `?destination=platform|customer`; preview and restore bodies accept the same
`destination` selector. Omission selects platform. Restore and preview times must lie within the
selected source's earliest/latest window, inclusive. The hidden organization aliases retain their
membership-based authorization. Credentials are write-only and stored sealed with the TerraBase master key in the catalog;
public responses contain no credentials.
Preview describes snapshot selection from backup metadata; it does not reserve objects or
prove that their contents pass the integrity checks performed during restore. See
[backup restore](backup.md#restore).

TS-588 adds `GET D/backups/{backup}/download` (`downloadBackup`, `databases:read`) here.

### Imports

One-time PostgreSQL (pg_dump plain) and Turso/SQLite (database file) imports into a new, empty database: contract,
states, error codes and limits in [import.md](import.md). Project access tokens only; served by the destination's home
region (`421 wrong_region` elsewhere).

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| POST | `D/import` | `createImport` | `databases:write`, `documents:write` | - |
| GET | `D/import` | `getImport` | `databases:read` | - |
| PUT | `D/import/source?offset={n}` | `uploadImportSource` | `databases:write`, `documents:write` | - |
| PATCH | `D/import` | `updateImport` | `databases:write`, `documents:write` | - |
| POST | `D/import/start` | `startImport` | `databases:write`, `documents:write` | - |
| POST | `D/import/cancel` | `cancelImport` | `databases:write` | - |
| POST | `D/import/resume` | `resumeImport` | `databases:write`, `documents:write` | - |

### Indexes

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| POST | `D/indexes` | `createIndex` | `databases:write` | `MD/indexes` |
| GET | `D/indexes` | `listIndexes` | `databases:read` | `MD/indexes` |
| DELETE | `D/indexes/{index}` | `dropIndex` | `databases:write` | `MD/indexes/{index}` |
| GET | `D/index-recommendations` | `listIndexRecommendations` | `databases:read` | `MD/index-recommendations` |
| POST | `D/index-recommendations/{recommendation}/apply` | `applyIndexRecommendation` | `databases:write` | `MD/index-recommendations/{recommendation}/apply` |

Automatic-index policy routes are v1-only; their former unversioned paths return `404`.

### Schema

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| GET | `D/schema` | `getDatabaseSchema` | `databases:read` | `MD/collections` (collections with document counts and bytes) |
| GET | `D/schema/{collection}` | `getCollectionSchema` | `databases:read` | `MD/collections/{collection}/schema` |

The Management collection list moves here, so `D/collections` has one meaning: the Data list below.

### Usage

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| GET | `P/usage` | `getProjectUsage` | `databases:read` | `MP/usage` |
| GET | `D/usage` | `getDatabaseUsage` | `databases:read` | `MD/usage` |

ts-dashboard reads usage here for plans, billing and spending caps; those stay in the dashboard. New metering
routes (TS-581) go in this group and follow these conventions. Both routes take `granularity` (`hour` or `day`),
`from` and `to`, and answer that period's reads and writes and one point per bucket (TS-581,
[management API](management-api.md#usage)). There is no organization usage: ts-dashboard sums its projects.

Project and database `/analytics` and `/replication` routes use the `/v1/projects` prefix
with the same project JWT scope and database-name path selectors.
Ranges, regional ownership, response fields, wire-byte accounting and limits:
[Project replication and analytics](analytics.md).

## Data

### Collections

| Method | Route | operationId | Scope |
| --- | --- | --- | --- |
| POST | `D/collections` | `createCollection` | `documents:write` |
| GET | `D/collections` | `listCollections` | `documents:read` |
| GET | `C` | `getCollection` | `documents:read` |
| DELETE | `C` | `dropCollection` | `documents:write` |

### Documents

Point routes require exactly one UTF-8 query-encoded `id`, with no other query parameters. Encode the
raw key once (for example `a/b` as `id=a%2Fb`, and literal `a%2Fb` as `id=a%252Fb`). Dot segments,
punctuation and Unicode remain distinct keys. `+` encodes space; `%2B` encodes a literal plus. Invalid
UTF-8 or percent escapes return `400 invalid_request`. See [the key contract](id-contract.md).

| Method | Route | operationId | Scope |
| --- | --- | --- | --- |
| POST | `C/documents` | `createDocument` | `documents:write` |
| GET | `C/documents` | `listDocuments` | `documents:read` |
| GET | `C/documents/by-key?id={id}` | `getDocument` | `documents:read` |
| PUT | `C/documents/by-key?id={id}` | `putDocument` | `documents:write` |
| PATCH | `C/documents/by-key?id={id}` | `patchDocument` | `documents:write` |
| DELETE | `C/documents/by-key?id={id}` | `deleteDocument` | `documents:write` |

A successful write (`createDocument`, `putDocument`, `patchDocument`, `deleteDocument`, `runTransaction`) answers
`"durability"` according to the accepting machine's topology:

- `"local_durable"`: the write committed, with `synchronous = FULL`, to the database file of the member that answered.
  A cell of one (a machine without a member directory, such as development) answers this way. A machine in a
  cell never falls back to this answer when another placed region is unavailable.
- `"remote_durable"`: the write committed locally, and a machine of another placed region confirmed a durable copy
  within 10 s. The attempt's whole effect is then in two regions. This is the target contract for document and
  transaction routes under [0013a](decisions/0013a-remote-durable-acknowledgement.md). It is implemented on these routes; wider durability qualification remains pending.
  Other mutating routes still answer `local_durable` (0013a section 6).

Neither label confirms a backup. Continuous backup ships committed writes to the bucket
asynchronously, about once per second, so the newest acknowledged writes may be in no backup yet. Some failures drop
backup history; see [continuous backup](backup.md).

Durability status: every write uses `synchronous = FULL` (#181). Simulated tenant WAL power-loss tests are merged
(#242; [evidence and limits](../../benchmark/bench/2026-10-13-tenant-power-loss.md)). A3's process tests cover a `201`
followed by immediate origin SIGKILL and isolated survivor restart in both gru↔sjc directions. Still pending: unsynced
main-database pages after checkpoints, directory persistence, sector corruption, `EIO`/`ENOSPC`, connection reuse,
receiver fault recovery and live qualification. Do not treat either label as a certified power-loss durability guarantee.

An assertion-only transaction writes nothing, so it has no commit to confirm. It answers without a wait and without a
remote-durability claim, with `"durability": "unchanged"` (0013a section 1).

A document write ends in one of three outcomes:

1. **Committed:** a `2xx` answer with `local_durable` or `remote_durable`, as above. After the database's SQLite commit returned, the
   storage engine still moves its commit count, wakes what waits for commits and releases the transaction. Each of
   these runs even when work before it failed. A failure there is logged and the write is still answered this way,
   except in two cases that answer outcome 3 instead: a group commit that also created or dropped a collection or
   adopted an incarnation (no document write does today), and a change committed in its own transaction rather than
   a group commit, such as creating or dropping a collection or changing a schema or an index. The quota and usage
   accounting of a committed write is covered too: its failure is logged and the write is still answered this way.
   Any other failure later in the request is not, and its `5xx` is outcome 3.
2. **Not committed:** a `4xx` answer. The write was refused (validation, precondition, quota, uniqueness) and rolled
   back. A `503` with `Retry-After` and code `remote_region_unavailable` or `confirmation_busy` is also not executed:
   it was refused before the write ran (0013a section 2).
3. **Unknown:** a `5xx` answer, a lost connection or a proxy error. The write may or may not have committed. A `503`
   `write_outcome_unknown` is this outcome: it carries `region` and `machine`, and no `Retry-After`. Clients and SDKs
   never replay it automatically. A read does not resolve the attempt: its changes may still arrive after the read.
   Any resend is a new attempt and needs application-specific proof that repeating it is safe. Increments, appends,
   transactions and server-generated IDs can apply twice. `Idempotency-Key` remains refused under 0013a.

### Queries

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| POST | `C/query` | `queryDocuments` | `documents:read` | unchanged |
| POST | `D/sql` | `runSql` | `databases:read` | `MD/sql` |
| POST | `D/sql/check` | `checkSql` | `databases:read` | `MD/sql/check` |
| GET | `D/query-shapes` | `listQueryShapes` | `databases:read` | `/management/organizations/{organization}/projects/{project}/databases/{database-id}/query-shapes` |
| POST | `D/sql/explain` | `explainSql` | `databases:read` | — |
| POST | `P/saved-queries` | `createSavedQuery` | `databases:write` | `MP/saved-queries` |
| GET | `P/saved-queries` | `listSavedQueries` | `databases:read` | `MP/saved-queries` |
| GET | `P/saved-queries/{query}` | `getSavedQuery` | `databases:read` | `MP/saved-queries/{query}` |
| PUT | `P/saved-queries/{query}` | `updateSavedQuery` | `databases:write` | `MP/saved-queries/{query}` |
| DELETE | `P/saved-queries/{query}` | `deleteSavedQuery` | `databases:write` | `MP/saved-queries/{query}` |

TS-722 adds the SQL EXPLAIN route above. It uses a database name and project JWT scopes,
returns the existing SQL check body (plan steps, selected secondary index names and suggestions), and
rejects invalid SQL with `400 invalid_request`. It does not execute or meter the query. The former unversioned EXPLAIN path returns `404`; no alias is registered.

### Transactions

| Method | Route | operationId | Scope |
| --- | --- | --- | --- |
| POST | `D/transactions` | `runTransaction` (was `transaction`) | `documents:write` |

### Live

| Method | Route | operationId | Scope |
| --- | --- | --- | --- |
| GET | `D/changes` | `streamDatabaseChanges` | `documents:read` |
| GET | `C/shape?where=&handle=&offset=&live=&cursor=` | `getShape` | `documents:read` |

Shape reads use the [v1 protocol](shape-reads.md).
`offset` is required. Bootstrap (`offset=-1`, without a handle) redirects with `307` to the newest sealed
snapshot; a stale handle returns `409 must_refetch`. Validation rejects malformed or duplicate parameters
with `400`. Before redirecting, the endpoint checks that every continuation target fits the 8 KiB request
line with 64 bytes reserved; otherwise it returns `414 uri_too_long`. Requests exceeding the listener's
request-line limit can be rejected before JSON error handling; large resume cursors belong in `TB-Shape-Resume`.
The route is client-listener only and preserves project/database isolation.
The origin connects the snapshot, chunk and shared live-tail components. Bootstrap follows the newest sealed snapshot,
then immutable chunks and `live=true` long-polls. Filters use canonical `where` values; lineage changes and offsets below
the retention floor require a `409 must_refetch` restart. Origin bytes stay plaintext and privately revalidated, with
strong ETags and `304` responses. `cursor_resume` carries the reached R8 vector; cross-machine resume proofs remain
fail closed pending FR-23. Tigris mirror encryption and upload belong to the FR-22 background worker.

The database's changes feed as Server-Sent Events (slice R8, [multi-region replication](multi-region-replication.md)
section 14). `change` events carry `{collection, id, op, version, origin, document}`; `cursor` events (when the stream
starts and every 15 s) carry `{cursor}`. Every event's `id` is the cursor after it, valid on any machine of any region.
Query `cursor` (a cursor, or `now`; the start of retention when absent) and `collections` (comma-separated, at most
64); `Last-Event-ID` takes precedence over `cursor`. Delivery is at least once per (origin, version, collection, id).
Errors: `400 invalid_cursor`, `410 cursor_expired` (retention deleted an event the cursor still promised, or the cursor
was issued more than 7 days ago), `429 changes_busy` (32 streams per database per machine). Slice R2c deleted
SSE subscriptions (`C/subscribe`) and the per-collection polling feed (`C/changes`); those routes return `404`.

## System

### Health

| Method | Route | operationId | Auth | Listeners |
| --- | --- | --- | --- | --- |
| GET | `/healthz` | `healthz` | anonymous | client, admin |
| GET | `/readyz` | `readyz` | anonymous | client, admin |
| GET | `/health/replication` | `replicationHealth` | anonymous | client, admin |

Unchanged: ts-orchestrator, ts-router, ts-atlas, ts-status and ts-deploy probe these paths.

`/health/replication` is the public replication summary ts-status checks per region (use the responding node's verified region identity). It answers `Cache-Control: public, max-age=5` and carries region codes and lag seconds only:

```json
{"region":"gru","status":"degraded","maxLagSeconds":2.5,
 "peers":[{"region":"fra","status":"degraded","lagSeconds":null},{"region":"sjc","status":"ok","lagSeconds":2.5}]}
```

- `peers` are the other regions this node's member directory lists, plus any region a
  replication stream reports, in region order.
- `lagSeconds` is the age of this node's oldest unreplicated change towards that region (the largest over its
  databases); `null` means no replication stream to that region reports.
- A peer is `degraded` without a stream, above 30 s of lag, or with a detected divergence. The node is `degraded` when
  any peer is. `maxLagSeconds` is the largest lag over every stream, or `null` when none reports.
- The status code is always 200: the body carries the state. A lone node (no other region) answers `ok` with no peers.
- Lag comes from the outbound replication status (`IReplicationStatus`). Between R2a and R4a of design C
  (multi-region-replication.md) client databases do not replicate, so every seen region reports `degraded` with
  `lagSeconds: null` until R4a's peer streams implement that status.

### API reference

| Method | Route | Answer |
| --- | --- | --- |
| GET | `/` | `302`, `Location: scalar/` |
| GET | `/scalar/` | Scalar page |
| GET | `/openapi/v1.json` | OpenAPI document |

These are not operations in the document ([API reference](api-reference.md)). Unchanged.

## Internal listener (private service network only, not in the public document)

| Method | Route | Old route | Use |
| --- | --- | --- | --- |
| GET | `/v1/admin/node/pressure` | `/admin/node/pressure` (404) | Node pressure ([node pressure](node-pressure.md)) |
| GET | `/metrics` | unchanged | Prometheus scrape |

## Hidden legacy identity and dashboard routes

These routes have no public TerraBase replacement. Their ownership moves to the services below. During the
SDK migration they remain available with their existing authentication and response behavior, but are hidden
from OpenAPI. This release neither resets production nor changes existing storage identities or files.

| Old routes | New owner |
| --- | --- |
| `/v1/management/organizations[/{organization}]`: create, list, read, rename, delete | ts-auth |
| `/v1/management/current-organization` | ts-auth (or the dashboard session) |
| `…/members…`, `…/leave`, `…/invitations…`, `/v1/management/invitations/{lookup,accept}`, `…/ownership-transfer…` | ts-auth |
| `…/organizations/{organization}/projects[/{project}]`: create, list, read, rename, delete | ts-auth |
| `PUT …/organizations/{organization}/plan` | ts-dashboard (plans, subscriptions, billing) |
| `…/audit-events`, `…/audit-retention` (organization audit) | ts-auth, with membership |
| `/v1/management/commits…`, `/v1/management/versions`, `/v1/management/documents/{collection}…` | ts-dashboard stores its own state as ordinary TerraBase documents in its own project |
| `PUT …/members/{user}` (admin listener, TS-529 bootstrap) and the platform operator | ts-auth bootstrap through the SDKs |
| `PUT /v1/projects/{project}/databases/{database}` (data-plane create) | `createDatabase` above |

## Old routes

Old management database, backup, index, schema, usage, SQL and saved-query paths are hidden aliases. They
still address databases by catalog id and require their existing organization membership or internal-listener operator
credential. Canonical routes address database names and require project JWT scopes. Existing `tb_` keys still
reach document routes and the hidden data-plane database `PUT` under their existing scope and tenant rules.

Keep these compatibility paths until COS confirms that the SDKs and production callers have migrated. No
production wipe, re-foundation or storage reset is part of TS-589. Existing documents, versions, snapshots and
replication identities retain their original catalog ids and storage files.

## Migrating a client

For existing legacy data, verify its project binding before switching credentials. The hidden client-only
`POST /v1/management/organizations/{legacyOrganization}/projects/{project}/binding` takes
`{"organization":"<ts-auth organization id>"}` and the user's ts-auth **sign-in** token. The same user must own
the legacy storage organization and be an effective admin of that project in ts-auth; TerraBase checks the
latter through ts-auth's live management API. Project tokens, `tb_` keys and the internal listener's platform operator cannot
perform this verification. Unverified legacy mappings are never automatically trusted, while non-project-JWT
legacy callers keep working. See [the binding workflow](management-api.md#verified-project-bindings).

1. **Credential.** Obtain a ts-auth project token with the scopes the calls need: an API key through client
   credentials (`grant_type=client_credentials&project=…`), or a signed-in user's token through token exchange
   (RFC 8693) naming the project (ts-auth `docs/auth/platform-contract.md` section 6).
2. **Management paths.** Drop `/v1/management/organizations/{organization}`:
   `/v1/management/organizations/{o}/projects/{p}/databases/{id}/backups` becomes `/v1/projects/{p}/databases/{name}/backups`.
3. **Database names.** Pass the database **name** where Management took the id.
4. **Moved routes.** Use `D/schema` and `D/schema/{collection}` for the old Management collection list and
   schema. SQL and saved queries keep their paths without the Management prefix.
5. **Create databases** with `POST P/databases`. The data-plane `PUT` is a hidden legacy path.
6. **Data paths** do not change. Only the credential does.

## Code layout

The implementation retains the feature slices required by `AGENTS.md`:

```text
src/TerraBase/Features/Management/{Backups,Indexes,Schema,Usage,Queries}
src/TerraBase/Features/Documents
src/TerraBase/Features/{Health,ApiReference,UserAuth,ApiKeys,Pressure}
src/TerraBase/Engine
src/TerraBase/Engine/Sqlite
```

`Management/ProjectApiRoute.cs` declares the public management hierarchy. `Management/LegacyRoutes.cs`
rewrites compatibility paths before endpoint routing and retains their legacy identity for catalog forwarding.
The public OpenAPI operation tags expose the Management, Data and System groups independently of folders.
Storage access continues through the `Engine/` interfaces and the existing SQLite implementation.
