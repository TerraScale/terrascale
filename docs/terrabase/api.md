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
3. **Authentication: ts-auth JWTs only** (ts-auth `docs/platform-contract.md` sections 3 and 6, TS-598). Every route
   except the `anonymous` ones takes `Authorization: Bearer <ts-auth access token>`. That covers users of
   ts-dashboard after a token exchange, and API keys after the SDK exchanges them through client credentials.
   TerraBase validates each token offline:
   - `typ` is `at+jwt` or `application/at+jwt`, and the token is ES256-signed by a `kid` from the issuer's JWKS;
   - `iss` is exactly the configured `auth-issuer` (`https://gru.auth.terrascale.tech/terrascale` in gru-1);
     project tenants' issuers are never trusted: those customer end-user tokens authenticate customer
     applications, while a **project access token** for TerraBase uses the trusted platform issuer and carries `project_id`;
   - `aud` includes TerraBase's audience, and `exp` and `nbf` hold with 30 seconds of skew;
   - `project_id` (a canonical uppercase ULID) equals the route's `{project}`. A token for another project, or one without
     `project_id` (organization, sign-in and service tokens), answers `404`, the same answer as a database that
     does not exist;
   - with a `database` claim (a key narrowed to one database), only that database is reachable: every other
     `{database}` answers `404`, the project's database list holds only it, and creating another answers `404`;
   - `scope` (space-separated) holds the scope the operation needs, or the route answers `403 forbidden_scope`.

   `org_id` is not used by the public API. ts-auth maps roles to scopes; TerraBase checks scopes, never roles
   on public routes. `sub` (a user id, or an API key id `tsk_…`) is recorded as the actor. Existing legacy projects
   resolve to their original records only after an explicit binding is verified against ts-auth. Unverified
   mappings answer `404` for project JWTs, including database creation. An authorized database create enrolls
   a fresh ts-auth project with a verified binding in an opaque storage namespace without user membership.
   Each binding is scoped to the exact trusted issuer and storage namespace
   ([COS decision, 2026-10-07](decisions/0011-project-catalog-trust.md)).

   | Scope | Grants |
   | --- | --- |
   | `databases:read` | Read databases, backups, the backup policy, indexes, index recommendations, the schema, usage and saved queries; read-only SQL |
   | `databases:write` | Create, branch and delete databases; create and restore backups; set the backup policy; create and drop indexes; apply recommendations; change saved queries |
   | `documents:read` | Read collections and documents; document queries |
   | `documents:write` | Create and drop collections; write documents; transactions |
   | `shapes:read` | Shape reads (Live) |

   The public contract advertises only project JWTs. Hidden compatibility paths still accept existing `tb_`
   document keys and membership tokens. `/admin/api-keys`, the loopback management service credential and
   `X-TerraBase-User-Id` remain available only for existing legacy workflows; they are absent from OpenAPI.
4. **Listeners.** Every route in the Management, Data and System tables is served on the client listener (TLS).
   Canonical management routes are client-only. The loopback admin listener retains the probes, `/metrics`,
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
membership-based authorization. Credentials are write-only and stored encrypted in OCI Vault;
public responses contain no credentials or Vault references.
Preview describes snapshot selection from backup metadata; it does not reserve objects or
prove that their contents pass the integrity checks performed during restore. See
[backup restore](backup.md#restore).

TS-588 adds `GET D/backups/{backup}/download` (`downloadBackup`, `databases:read`) here.

### Indexes

| Method | Route | operationId | Scope | Old route |
| --- | --- | --- | --- | --- |
| POST | `D/indexes` | `createIndex` | `databases:write` | `MD/indexes` |
| GET | `D/indexes` | `listIndexes` | `databases:read` | `MD/indexes` |
| DELETE | `D/indexes/{index}` | `dropIndex` | `databases:write` | `MD/indexes/{index}` |
| GET | `D/auto-index/{collection}` | `getAutoIndexPolicy` | `databases:read` | — |
| PUT | `D/auto-index/{collection}` | `setAutoIndexPolicy` | `databases:write` | — |
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
| GET | `C/shape` | `getShape` | `shapes:read` |
| GET | `C/subscribe` | `subscribeCollection` | `shapes:read` |

TS-690 adds the SSE subscription route above. It authenticates project-scoped ts-auth JWTs
and streams the existing shape contract; see [subscriptions](subscriptions.md) for selection, resume,
expiry and resource bounds. The former unversioned subscription path returns `404`; no alias is registered.
WebSocket delivery and plan entitlements remain subsequent slices.

## System

### Health

| Method | Route | operationId | Auth | Listeners |
| --- | --- | --- | --- | --- |
| GET | `/healthz` | `healthz` | anonymous | client, admin |
| GET | `/readyz` | `readyz` | anonymous | client, admin |

Unchanged: ts-orchestrator, ts-router, ts-atlas, ts-status and ts-deploy probe these paths.

### API reference

| Method | Route | Answer |
| --- | --- | --- |
| GET | `/` | `302`, `Location: scalar/` |
| GET | `/scalar/` | Scalar page |
| GET | `/openapi/v1.json` | OpenAPI document |

These are not operations in the document ([API reference](api-reference.md)). Unchanged.

## Administrative listener (loopback only, not in the public document)

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
| `…/projects/{project}/api-keys…`, `POST /admin/api-keys`, `tb_` key validation | ts-auth (API keys are client credentials) |
| `PUT …/organizations/{organization}/plan` | ts-dashboard (plans, subscriptions, billing) |
| `…/audit-events`, `…/audit-retention` (organization audit) | ts-auth, with membership |
| `/v1/management/commits…`, `/v1/management/versions`, `/v1/management/documents/{collection}…` | ts-dashboard stores its own state as ordinary TerraBase documents in its own project |
| `PUT …/members/{user}` (admin listener, TS-529 bootstrap) and the management service credential | ts-auth bootstrap through the SDKs |
| `PUT /v1/projects/{project}/databases/{database}` (data-plane create) | `createDatabase` above |

## Old routes

Old management database, backup, index, schema, usage, SQL and saved-query paths are hidden aliases. They
still address databases by catalog id and require their existing organization membership or loopback service
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
latter through ts-auth's live management API. Project tokens, `tb_` keys and the loopback credential cannot
perform this verification. Unverified legacy mappings are never automatically trusted, while non-project-JWT
legacy callers keep working. See [the binding workflow](management-api.md#verified-project-bindings).

1. **Credential.** Obtain a ts-auth project token with the scopes the calls need: an API key through client
   credentials (`grant_type=client_credentials&project=…`), or a signed-in user's token through token exchange
   (RFC 8693) naming the project (ts-auth `docs/platform-contract.md` section 6).
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
