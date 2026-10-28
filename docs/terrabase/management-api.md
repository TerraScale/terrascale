# Management API (`/v1/management`)

The [zero-configuration contract](zero-configuration.md) supersedes production
settings. Legacy `disabled`/`enabled`, schedule and retention fields below are
current API inventory pending TS-773, not recommended feature switches or tuning.
Authorization, project binding and destructive-operation safeguards still apply.

This describes the legacy compatibility implementation retained after TS-589. The service ownership
decision of 2026-10-06 supersedes its organization, project, invitation, credential and organization
audit contracts: those belong to ts-auth. The public routes and JWT scopes are in
[the HTTP API table](api.md). See [the TS-493 dashboard map](dashboard-endpoints.md) for the
remaining work and its owners; do not extend legacy identity routes.

The Management feature (`src/TerraBase/Features/Management`, OpenAPI tag
`Management`) stores organizations, projects and the databases they own, so ts-dashboard
has a server-side home for state it keeps in memory today. This document maps every
ts-dashboard feature to the TerraBase endpoint that backs it, or to the service that owns it
instead.

Sources, read on 2026-10-04: ts-dashboard `internal/` and `web/` (routes under
`/api/v1/dashboard`, `/api/v1/access` and `/api/v1/admin`), `terrabase-rust/docs/control-plane-v1.md`,
ts-orchestrator and ts-auth.

TS-589 moves public data management to the [project route table](api.md). The
organization and membership operations below remain hidden legacy compatibility
paths; ts-auth owns public identity and projects. COS approved verified migration
bindings on 2026-10-07 ([decision 0011](decisions/0011-project-catalog-trust.md)).

## Dashboard integration

The dashboard backend calls these APIs through ts-go; browsers use server-rendered
forms. ts-auth owns identity and access. Production requires persistent state
and real authorization, with fakes and demos confined to test binaries.
Existing demo/runtime paths are implementation gaps; this API document does not
establish completion of the cross-product cutover.

## Access and authorization

Decided by a human on 2026-10-04: only the ts-dashboard backend calls this API, with a dashboard
service credential and the acting user in headers. Superseded on 2026-10-05 (TS-519,
[0007](decisions/0007-ts-auth-user-tokens.md)): public calls carry the user's ts-auth access token, and a
platform operator works only on the internal listener, for bootstrap before ts-auth
exists. TerraBase authorizes every request against the organizations the acting user belongs to.

### Access tokens

- The endpoints are served on the **client listener** (TLS), which accepts only ts-auth access tokens,
  and on the **internal listener**, which accepts only the
  [platform operator](#management-service-credential). A token on the admin listener, or
  the credential on the client listener, gets `401`.
- The trusted issuer, audiences and TLS listener must be discovered/provisioned
  automatically. Their existing `TERRABASE_AUTH_*` inputs are a TS-773 gap.
  Missing trust inputs still deny every Management call with `401`.

- Each request presents `Authorization: Bearer <ts-auth access token>`. The token must:
  - be a JWT with header `typ` `at+jwt` or `application/at+jwt` (RFC 9068). ID tokens and other
    JWTs from the same issuer are refused;
  - be signed with ES256/384/512, RS256/384/512 or PS256/384/512 (`none` and HS* are refused), by
    the cached key its `kid` names;
  - have `iss` equal to `auth-issuer` exactly and an `aud` that includes one of `auth-audience`, compared exactly (a trailing `/` makes another audience);
    on `ts-terrabase` the pair can only be `https://auth.terrascale.tech/terrascale` and `https://api.terrascale.tech`, the same in every region;
  - have an `exp`. `exp` and `nbf` are checked with 30 seconds of clock skew;
  - have a `sub` of 1 to 256 characters without control characters;
  - not be a ts-auth guest's: `is_anonymous` `true` is refused.
- Any failure answers `401` with no body. The recorded failure reason is a type name or a fixed message, never the token.
- The token grants no document access. API keys (`tb_…`) grant no management access. Either one
  used on the other's routes gets `401`.
- ts-dashboard calls with the signed-in user's token. A ts-auth client-credentials token (`sub` =
  client id) for an accepted audience acts as a user whose id is that client id: it reaches only
  organizations that id is a member of.

### Verified project bindings

Existing catalog project mappings are storage records, not proof of ts-auth
authority. Project JWTs receive `404` until a binding for the exact trusted
issuer and storage organization is verified; database creation does not adopt
an unverified mapping. Legacy callers without project JWTs keep their existing
membership/key access and data.

`POST /v1/management/organizations/{legacyOrganization}/projects/{project}/binding`
is hidden from OpenAPI and served only on the TLS client listener. Send
`{"organization":"<ts-auth organization id>"}` with the user's ts-auth sign-in
bearer token. That subject must currently be an owner of the legacy storage
organization. TerraBase forwards the same token only to
`{configured issuer origin}/v1/{configured issuer tenant}/organizations/{organization}/projects/{project}`;
redirects are not followed. ts-auth must answer with the exact project and
organization IDs and effective `role: "admin"`. Its live management API checks
membership, sign-in revocation and authentication epoch. Project/API-key tokens,
document keys and the internal listener's platform operator cannot establish this binding.

TerraBase rechecks legacy ownership inside the catalog transaction and records
the binding's version, issuer, project, storage organization, ts-auth organization,
verifying subject and time. Failed verification writes no binding. Subsequent
project JWTs resolve the verified namespace offline; this action preserves
database IDs, files and document versions. Fresh JWT enrollment creates an
opaque namespace and its verified binding atomically, without legacy membership.
Fresh bindings rely on the signed project claim and leave the ts-auth
organization unset; project JWTs do not require `org_id`.

This explicit verification replaces automatic JWT reuse of legacy mappings by
COS's 2026-10-07 decision. Client-chosen project IDs in Management commits now
require the internal listener's platform operator; generated-ID legacy creation
remains available to ordinary members ([commit contract](management-commits.md#management-operations)).

### Signing keys

- TerraBase reads `{auth-issuer without a trailing /}/.well-known/openid-configuration`, then its
  `jwks_uri`. Both must be `https` and answer `2xx`; redirects are not followed. A discovery document whose `issuer` is not exactly `auth-issuer`
  is not used, and its `jwks_uri` is never requested. Only RSA and EC keys are kept; symmetric (`oct`) keys are ignored. A document over
  1 MiB fails. One 10-second deadline covers both requests of a fetch, and shutdown cancels it.
- Keys are cached in memory. Startup waits up to 10 seconds for the first fetch. Until it succeeds,
  every token gets `401` and a background loop retries every 2 seconds. `/readyz` does not wait for the
  keys: ts-auth stores its data in TerraBase, so readiness must not depend on it.
  After that the loop refreshes every 10 minutes. A failed refresh keeps the cached keys, so ts-auth
  being down after startup does not stop validation.
- Requests never fetch keys, with one exception: a token whose `kid` is not cached triggers one
  refresh, shared by concurrent requests and at most once per 30 seconds. If the keys changed, the
  token is validated once more.
- Counter `terrabase.auth.key_fetches`, label `outcome` (`success` or `failure`). TerraBase's fetch
  failure logs name the setting (`auth-issuer`) and the failure type, never the issuer URL or the audiences.

### Management service credential

The separate bootstrap management bearer credential is retired. Internal platform
operator requests carry `TERRABASE_PEER_SECRET` from OCI Vault on each app;
[peer authentication](peer-authentication.md) describes its rollout and enforcement.
The operator acts as the canonical ULID in `X-TerraBase-User-Id`, only on the
internal listener, without an `Authorization` header. Membership and roles still
apply, and each use is logged without the credential.

### Acting user and organization

- The acting user is the token's `sub`, or the user the platform operator names on the
  admin listener: the ts-auth user id stored in memberships, `createdBy` and audit `actor`. On the
  client listener no request header names the user; `X-TerraBase-Organization-Id` is no longer read.
- The organization comes from the route. TerraBase reads no organization or tenant claim: ts-auth
  issues none for TerraScale's platform organizations. The exact `iss` pins the ts-auth instance.

### Membership

- Creating an organization makes the acting user its first member, with the `owner` role. Roles,
  invitations, removing members and ownership transfer are described in [Members and roles](#members-and-roles).
- Every operation checks membership in the same SQLite transaction as the read or write it guards.
  A membership change can therefore never land between the check and the write.
- An organization the user is not a member of, and everything under it (projects, databases,
  branches), answers `404`, exactly like an id that does not exist. A non-member cannot confirm
  that an organization exists.
- Project access is decided at the organization level for now: every member with the `member` role or
  higher can manage every project and database in the organization; a `viewer` reads them and gets `403` on
  every change, including the database tooling (indexes, recommendations applied, backups, backup policies,
  restores and saved queries); it still reads them and runs SQL, which is read-only. Renaming the
  organization needs `admin` and deleting it needs `owner` (`403` otherwise).
  Per-project roles are a follow-up.
- `GET /organizations` lists only the acting user's organizations. The current organization can
  be set only to an organization the user belongs to, and it reads as `404` once the user no longer
  belongs to it.
- Platform-admin routes in the dashboard (`/api/v1/admin/…`) act on any organization. They need a
  separate admin authorization design and are not served by this API.

### Fresh catalog bootstrap

An empty system database bootstraps `catalog/version` to 6 in one transaction. Existing older catalogs are
refused with `503`; there is no upgrade, ownership backfill or `management-legacy-owner` setting.
The closed-beta ULID rollout deletes old database files, catalog records, backups and affected external state
before restart. Service bindings and credentials are recreated. See [the ID contract](id-contract.md).

### Database lifecycle recovery (TS-480)

The current catalog stores `pending_database_cleanup` documents in `_system.db`, keyed by database ULID
and holding its tenant context. Unsupported catalog versions are refused; changing the version marker
does not make an older catalog compatible.

- **Create and branch:** check that the target has no existing files and commit a cleanup intent
  under the engine lifecycle lock, keeping that lock through storage publication. Existing unknown
  targets, including case-equivalent names, are preserved without creating an intent. Register the
  database and remove the intent in one catalog transaction after storage succeeds.
  A process crash before registration leaves an intent that proves which
  unpublished files may be removed. An unresolved create intent reserves its destination against
  competing engine creation, including after cancellation or an error, until registration or cleanup
  clears the intent. Startup finishes persisted intents before permitting new requests.
- **Delete:** remove the database and name claim and insert its cleanup intent in the same catalog
  transaction while reserving the destination under the lifecycle lock. Retain that reservation until
  the intent is cleared, including when only sidecars remain. Remove `.db`, `-wal`, `-shm`, `-journal`
  and private creation files for that database, then remove the intent. Filesystem failures leave the
  intent available for recovery.
- **Startup:** replay all current-format intents before any listener serves requests. Replay is idempotent,
  including when only sidecars remain. A replay error stops startup; the intent stays for the next
  start. An intent conflicting with a registered database stops startup without deleting its data.
- **Unknown files:** preserve files without a catalog registration and log each path. A backup snapshot
  whose record its organization still holds, in the same project, is registered. The
  administrative `/metrics` exposes `terrabase_storage_unknown_files`, a count of individual files
  found at startup, including sidecars, unknown root files and directories named as database
  artifacts. Inspect and recover them manually; TerraBase never sweeps unknown data. The gauge is
  a startup observation, refreshed on restart. System database or sidecar symbolic links stop
  startup before their targets are opened. Tenant database opens and branch sources also refuse
  symbolic links in the main file or any known sidecar, including dangling links.

The data directory, its ancestors and tenant directories must be writable only by the trusted
TerraBase service account and operators. Do not move or replace them while the server runs;
stop the server before filesystem maintenance. Link checks are repeated after lifecycle callbacks
and before publication or cleanup, but path-based SQLite access does not provide isolation from
a hostile process with filesystem write access. The ownership lock coordinates TerraBase
processes, not arbitrary filesystem writers; deployment permissions provide that boundary.

This covers TerraBase process crashes. The catalog itself is in the system database, which syncs every commit
(decision 0012). Since #181 tenant databases' write transactions sync their commits too (0003 as amended). Their
power-loss evidence covers the write-ahead log only. Database-file, checkpoint and sync-error cases are still to be
proven. Older catalogs return
`503` and are never migrated or swept.
Unsupported catalog versions are audited without interpreting tenant rows; their files are preserved,
logged and counted as unknown while management requests keep returning `503`.

## Conventions

- **IDs** are canonical uppercase 26-character Crockford ULIDs. Lists in id order (organizations, projects, databases)
  sort by timestamp, with monotonic generation within a millisecond per process. Clock changes can change timestamp order.
  A malformed entity id answers `400`; an unknown valid id answers `404`. The document key is the entity id; stored payloads omit its redundant `id`.
  Document ids are canonical ULIDs too (`400 invalid_document_id` otherwise).
- **Organization and project names** are 1 to 128 characters after trimming. They must contain a
  letter or digit and no control characters, and are compared ignoring case.
  - Project names are unique within their organization.
  - Organization names are unique among the organizations created by the same user (`createdBy`).
    A global rule would let one tenant probe for another's organization names. The dashboard uses
    the same per-owner rule (`organization/service.go:89`).
- **Database names** follow the data API rule: a lowercase letter, then lowercase letters, digits,
  `_` or `-`, at most 63 characters. They are unique per project.
- **Lists** take `?after=<cursor>&limit=1..100` (default 20) and return `{"items":[…],"next":null|"<cursor>"}`.
  This matches the document list endpoint. On lists in id order the cursor is the last item's id; lists kept in a
  per-organization sequence (invitations, audit events, backups, saved queries) return an opaque cursor.
  Either way, pass `next` back as `after`.
- **Errors:** `400` for invalid input, `403` when the acting member's role does not allow the request,
  `404` for an unknown resource or parent (or an organization the user is not a member of), `409` for a
  duplicate name, a non-empty delete or a rule in [Members and roles](#members-and-roles). Bodies are
  `{"error":{"code","message"}}` with a stable code (`invalid_request`, `invalid_query`, `forbidden_role`, `not_found`,
  `name_taken`, `not_empty`, `last_owner`, `already_member`, `invitation_pending`, `already_owner`,
  `invitation_expired`) and a message that never echoes input ([error handling](error-handling.md)).
- A database carries `region` (the region that answered), `homeRegion`, `replicaRegions` and `allRegions` (its [placement](#region-placement)).
- The data API addresses a database as `/v1/projects/{projectId}/databases/{name}/collections/…`,
  with the database name from this API. The API key's organization selects the tenant.
- Every organization has a trusted resource `plan`: `hobby` (default), `pro` or `enterprise`.
  [Organization QoS](qos.md) enforces request, query, memory/cache and live-document storage ceilings.
  Only the platform operator acting as an owner can publish a plan; public user tokens cannot upgrade it.

## Endpoints

Base path: `/v1/management`.

| Method | Route | Result |
| --- | --- | --- |
| `POST` | `/organizations` | `{name}` → `201` organization with the acting user as its first member; `409` name taken |
| `GET` | `/organizations` | page of the acting user's organizations |
| `GET` | `/organizations/{organization}` | organization |
| `PATCH` | `/organizations/{organization}` | `{name}` → renamed organization, `409` name taken; needs `admin` |
| `PUT` | `/organizations/{organization}/plan` | `{plan}` (`hobby`, `pro` or `enterprise`) → organization; `400` for any other value; needs the platform operator acting as an `owner` (`403` for public user tokens). Recorded in the audit log as `settings.plan_updated` |
| `DELETE` | `/organizations/{organization}` | `204`; `409` while it has projects; needs `owner`. Also deletes its members, invitations, transfer and audit log |
| `GET` | `/current-organization` | the organization the acting user last switched to; `404` if none or no longer a member |
| `PUT` | `/current-organization` | `{organizationId}` → organization; `404` if the user is not a member |
| `POST` | `/organizations/{organization}/projects` | `{name}` → `201` project; needs `member` (as every project and database change below) |
| `GET` | `/organizations/{organization}/projects` | page of projects |
| `GET` | `/organizations/{organization}/projects/{project}` | project |
| `PATCH` | `/organizations/{organization}/projects/{project}` | `{name}` → renamed project |
| `DELETE` | `/organizations/{organization}/projects/{project}` | `204`; `409` while it has databases |
| `POST` | `…/projects/{project}/databases` | `{name, homeRegion?, replicaRegions? \| allRegions?}` → `201` empty database, placed in two regions or more on a cell; `400 placement_needs_two_regions`, `503 remote_region_unavailable`; `421 wrong_region` when `homeRegion` is not the called region ([placement](#region-placement)) |
| `GET` | `…/projects/{project}/databases` | page of databases |
| `GET` | `…/projects/{project}/databases/{database}` | database |
| `PATCH` | `…/projects/{project}/databases/{database}` | `{replicaRegions}` or `{allRegions: true}` → database with its new placement; home region only (`421 wrong_region` elsewhere) |
| `DELETE` | `…/projects/{project}/databases/{database}` | `204`; deletes the data. Branches are independent and stay. Home region only (`421 wrong_region` elsewhere), once it has no replica regions (`409 database_replicated`) |
| `POST` | `…/databases/{database}/branches` | `{name}` → `201` database in the same project holding a consistent snapshot copy; home region only, and the branch has no replicas |
| `POST` | `/commits` | atomic commit of Management operations and dashboard documents ([management-commits.md](management-commits.md)) |
| `GET` | `/commits/{id}` | recorded outcome of the acting user's commit; `404` if it never committed |
| `GET` | `/versions?organization=…` | account and organization versions for fencing commits |
| `GET` | `/documents/{collection}/by-key?id={id}` | a document of the dashboard document space; one UTF-8 query-encoded key ([contract](id-contract.md)) |
| `POST` | `/documents/{collection}/query` | query v1 over the dashboard document space |

Shapes:

```json
{"id":"0199…","name":"Acme","createdBy":"<ts-auth user id>","createdAt":"2026-10-04T12:00:00+00:00","plan":"hobby"}
{"id":"0199…","organizationId":"0199…","name":"Production","createdAt":"…"}
{"id":"0199…","projectId":"0199…","name":"main","branchedFrom":null,"createdAt":"…","region":"gru-1"}
```

Database create, branch, get and list responses report the serving node's `region`,
also returned in `X-TerraBase-Region`. It is response metadata, not persisted
placement. See [region identity](region-identity.md) for Atlas session pins and `421` responses.


### Region placement

A database's placement names its home region and its replica regions (TS-643, [cross-region replication](cross-region.md#placement)).
Region ids are Atlas region ids (`gru-1`, `sfo-1`), the values of `X-TerraBase-Region`.

- `homeRegion` is where the database is created and managed: create, change regions, delete and branch go to it.
  `replicaRegions` are the other regions that hold a full copy; every listed region serves reads and writes, and
  writes replicate between them.
- On create, `homeRegion` defaults to the called region. A replica region must be one this deployment replicates with,
  listed once, and not the home (`400 invalid_request`). Each replica region must register the organization and project
  under the same ids to take the database.
- **Two regions or more** (decision 0013a, option A) apply on a machine that belongs to a production cell:
  - A create without `replicaRegions` places replicas in every other region the membership lists. With none listed, it
    answers `503 remote_region_unavailable` and creates nothing.
  - A create with an empty `replicaRegions` answers `400 placement_needs_two_regions`.
  - A `PATCH` may still remove every replica region, which is the step before a delete. That database then takes no
    writes until a region is added back: its writes are refused with `409 placement_needs_two_regions`, without
    `Retry-After` ([0013c](decisions/0013c-single-region-write-conflict.md)).
  - A machine without a member directory (development, single-machine tests) is a cell of one. It still
    defaults to no replica regions.
- `PATCH` sets the complete new `replicaRegions`; `homeRegion` may be repeated but cannot change. An added region takes
  the database and fills its copy. A removed region stops serving it at once and keeps its copy unserved, so the writes it
  accepted keep reaching the others; added back, it resumes from that copy.
- A region outside the placement answers the database's data requests, and its database-scoped Management requests other
  than reading, updating and deleting the database, with `421 wrong_region` and the headers
  `X-TerraBase-Database-Home` and `X-TerraBase-Database-Regions` (comma-separated, home first). Clients resolve one of
  those regions through Atlas and retry there.
- A database recorded before placement existed has `homeRegion` = the region holding it and no replicas.
- `allRegions: true` places a full copy in every serving region, including regions that join later: [all-regions placement](all-regions-placement.md).

## Members and roles

Roles, from least to most privileged: `viewer`, `member`, `admin`, `owner`. A `viewer` is read-only: it reads
the organization, projects, databases, members and the pending transfer, and may leave. A viewer is never
mapped to a writable role. The routes below are under
`/v1/management/organizations/{organization}` and follow [Access and authorization](#access-and-authorization):
a user who is not a member gets `404`, and a member whose role is too low gets `403`.

| Method | Route | Needs | Result |
| --- | --- | --- | --- |
| `GET` | `/members` | viewer | page of members in user id order |
| `PUT` | `/members/{user}` | admin; admin listener only | `{email}` → member; see [Adding a member on the administrative listener](#adding-a-member-on-the-administrative-listener) |
| `PUT` | `/members/{user}/role` | admin; owner if the member is or becomes an owner | `{role}` → member; `409` when it would demote the last owner |
| `DELETE` | `/members/{user}` | admin; owner to remove an owner | `204`; `409` for the last owner |
| `POST` | `/leave` | viewer | `204`; `409` for the last owner |
| `POST` | `/invitations` | admin; owner to invite an owner | `{email, role}` (role defaults to `member`) → `201` `{invitation, token}`; `409` while an unexpired invitation for the email is pending |
| `GET` | `/invitations` | admin | page of every invitation with its `status` (`pending`, `accepted`, `declined`, `revoked`), in creation order; `after` is the previous page's `next` |
| `DELETE` | `/invitations/{invitation}` | admin | `204`; the invitation becomes `revoked` and its token stops working; `409` when it is no longer pending |
| `GET` | `/ownership-transfer` | viewer | pending transfer; `404` if none |
| `POST` | `/ownership-transfer` | owner | `{userId}` → `201` transfer to a member who is not an owner; replaces a pending one |
| `POST` | `/ownership-transfer/confirm` | the nominee | nominee's membership, now `owner`; the owner who started it becomes `member` |
| `DELETE` | `/ownership-transfer` | owner or the nominee | `204` |
| `GET` | `/audit-retention` | admin | `{days}`, `90` unless set |
| `PUT` | `/audit-retention` | admin | `{days}` (1 to 90) → `{days}` |
| `GET` | `/audit-events` | admin | page of audit events, newest first, within the retention; `after` is the previous page's `next` |

Accepting is the one route outside an organization: `POST /v1/management/invitations/accept` with
`{token}`; the invitation token names the organization. The email is the access token's `email` claim, used
only when `email_verified` is `true`; the body never supplies it, because the caller may be the user itself.
It answers the new membership; `403` when the access token has no verified email or it differs from the
invitation, `404` for an unknown token, `409` when the invitation expired, is no longer pending or the user is
already a member.

`POST /v1/management/invitations/lookup` with `{tokenHash}` (lowercase hex SHA-256 of the token) answers a
pending, unexpired invitation with its organization's id and name, and `404` otherwise. Invitations whose
token the dashboard generates (sending only its hash), decline and resend are commit operations: see
[Invitations by token hash](management-commits.md#invitations-by-token-hash).

Rules:

- **The last owner** cannot be demoted, removed or leave. An organization can have several owners;
  only owners grant, revoke or transfer ownership.
- **Invitations** expire after 7 days. The token (`<organization>.<invitation>.<secret>`) is returned
  only by the create call; TerraBase stores SHA-256 hashes of it and compares them in constant time.
  Inviting an address whose pending invitation expired creates a new one and keeps the expired one in the
  history. Accepting, declining and revoking keep the invitation with its new status; each token works at
  most once. TerraBase sends no email: the dashboard delivers the link. On accept and decline, the access
  token's verified email must match the invitation ignoring case.
- **Ownership transfer** follows the dashboard: one pending transfer per organization, valid for 7 days.
  Only the nominee confirms. The offer lapses (`404`) as soon as its starter stops being an owner or
  either side leaves, even if the starter later regains ownership.
- **Audit events** record every change above as `{id, actor, action, target, detail, occurredAt}`.
  Actions: `member.added`, `member.role_changed`, `member.removed`, `member.left`, `invitation.created`,
  `invitation.revoked`, `invitation.accepted`, `invitation.declined`, `invitation.resent`, `ownership.transfer_started`,
  `ownership.transfer_confirmed`, `ownership.transfer_cancelled`, `settings.audit_retention_updated`,
  `settings.plan_updated`. `target` is a user id or an invitation email; `detail` is the new role, the
  number of days, the new plan or, for `member.added`, the supplied email. Retention
  is measured on each event's exact `occurredAt`: events older than the retention stop being listed at once
  and are deleted, oldest first, as new events are recorded.
- **Order:** invitations list in creation order and audit events newest first, by a per-organization
  sequence. The sequence comes from a counter document incremented in the same write transaction as the
  insert, and is the key the list is stored under. The order therefore survives restarts, does not depend
  on the clock, and holds within one millisecond. Invitation and event ids are opaque ULIDs. On these two
  lists `next` is an opaque cursor to pass back as `after`, not an item id.

Shapes:

```json
{"userId":"<ts-auth user id>","role":"owner","joinedAt":"2026-10-04T12:00:00+00:00"}
{"invitation":{"id":"0199…","email":"ada@example.com","role":"admin","invitedBy":"<user id>","createdAt":"…","expiresAt":"…"},"token":"0199….0199….<secret>"}
{"fromUserId":"<user id>","toUserId":"<user id>","createdAt":"…","expiresAt":"…"}
{"id":"0199…","actor":"<user id>","action":"member.role_changed","target":"<user id>","detail":"admin","occurredAt":"…"}
```

### Adding a member on the administrative listener

Decided by COS on 2026-10-05 (TS-535): the TS-529 bootstrap creates the `terrascale` organization as its own
identity, then hands it to a person. The platform operator carries no verified email, so it cannot accept
an invitation; this operation adds the member directly.

`PUT /v1/management/organizations/{organization}/members/{user}` with `{email}`:

- Served **only on the internal listener**, with the platform operator and `X-TerraBase-User-Id`
  naming the acting user. On the client listener it answers `404` (or `421` for a mismatched region pin), whatever
  credential or token the request presents.
- `{user}` is the ts-auth user id to add (1 to 256 characters, no control characters) and `email` its address (one
  address, at most 254 characters). TerraBase does not verify the email and does not store it on the membership: it is
  recorded only in the audit event. A pending invitation for the same email is left alone; accepting it later answers
  `409` (already a member).
- Needs `admin`, like any membership change. Adds the user with role `member` and answers `200` with the membership.
- Idempotent: a user who is already a member is returned unchanged (`200`), whatever its role, and nothing is recorded.
- Audited as `member.added`: `actor` is the user the credential names, `target` the added user id and `detail` the
  supplied email. The credential's log line (`Management service credential acted as user …: PUT …`) records the call too.
- Errors use the `{"error":{"code","message"}}` body ([error handling](error-handling.md)): `400 invalid_request` for
  an invalid user id or email, `403 forbidden_role` when the acting user's role is below `admin`, `404 not_found` for
  an organization the acting user is not a member of.

The bootstrap, every call on the admin listener with the credential:

1. `POST /organizations` `{name}` as the bootstrap identity, which becomes its owner.
2. `PUT /organizations/{organization}/members/{user}` `{email}` as the bootstrap identity.
3. `POST …/ownership-transfer` `{userId}` naming the user, as the bootstrap identity.
4. `POST …/ownership-transfer/confirm` with `X-TerraBase-User-Id` naming the user: the user becomes an owner and the
   bootstrap identity a `member`.
5. `PUT …/members/{bootstrap identity}/role` `{"role":"admin"}` as the user, to keep the bootstrap identity an admin.

Step 2 is safe to repeat until step 4. After step 4 the bootstrap identity is no longer an admin, so repeating step 2
answers `403`: resume from step 5.

Audit filters/export and organization/project change events belong to ts-auth under the
2026-10-06 ownership decision. TerraBase database change events still need a delivery and
consumer contract; the legacy organization audit log is not their target.

## Database tooling endpoints

These back the dashboard's database tooling pages (TS-463). Same access, conventions and errors as above: a
ts-auth access token (or, on the administrative listener only, the platform operator) on every route,
and `404` for an organization the acting user is not a member of and everything under it. Like projects and databases, the
tooling is open to every member whatever its role (see [Membership](#membership)); per-project roles are a follow-up.
D = `/v1/management/organizations/{organization}/projects/{project}/databases/{database}`,
P = `/v1/management/organizations/{organization}/projects/{project}`. Every route returns `404` when the
organization, project or database does not exist.

| Method | Route | Result |
| --- | --- | --- |
| `GET` | `D/backups` | page of backups |
| `POST` | `D/backups` | `201` backup taken now |
| `GET` | `D/backup-policy` | backup policy (defaults until saved) |
| `PUT` | `D/backup-policy` | `{fullBackupCron, retentionDays, disabled}` → policy |
| `POST` | `D/backups/{backup}/restore` | `{name?}` → `201` new database with the backup's data |
| `GET` | `D/collections` | collections with document counts and bytes |
| `GET` | `D/collections/{collection}/schema` | inferred fields |
| `GET` | `D/indexes[?collection=]` | indexes |
| `POST` | `D/indexes` | `{collection, fields, name?}` → `201` index; `409` duplicate |
| `DELETE` | `D/indexes/{index}` | `204` |
| `GET` | `D/index-recommendations` | recommendations from the SQL workload |
| `POST` | `D/index-recommendations/{recommendation}/apply` | `201` applied or `200` already applied |
| `POST` | `D/sql` | `{sql}` → rows; `400` invalid SQL |
| `POST` | `D/sql/check` | `{sql}` → validity, plan, used indexes, suggestions |
| `GET` | `D/usage[?granularity=&from=&to=]` | storage, documents, reads and writes of the database, with a series over the period |
| `GET` | `P/usage[?granularity=&from=&to=]` | the same summed over the project, with one item per database; 400 `query too expensive` past 100 databases (before counting) or when counts are still running after 5 s |
| `GET` | `P/saved-queries` | page of saved queries |
| `POST` | `P/saved-queries` | `{name, sql}` → `201` saved query |
| `GET` | `P/saved-queries/{query}` | saved query |
| `PUT` | `P/saved-queries/{query}` | `{name, sql}` → saved query |
| `DELETE` | `P/saved-queries/{query}` | `204` |

Membership is checked in the same catalog transaction as every write to the catalog that creates access or
data: backups, backup policies and restores. The other routes (SQL, indexes, schema, usage, saved queries)
check it when the request starts. Background work (scheduled backups, usage flushes) acts for no user.

Per-database state (backups, policy, usage, query counts) lives in the catalog under the database and is
deleted with it. Saved queries are deleted with their project; a project with API keys cannot be deleted (`409`).

### Backups

A backup is a consistent snapshot of a database, taken by the server and stored in its tenant's directory, next to the
database: `<data-directory>/<organization>/<project>/<backup id>.db` (a copy within the organization, decision 0005). No
catalog entry names it as a database, so the data API cannot reach it. Only complete (registered) backups are listed and
can be restored.

| Field | Meaning |
| --- | --- |
| `id` | Canonical uppercase ULID. |
| `databaseId` | The backed-up database. |
| `status` | Always `ready`. |
| `trigger` | `manual` (`POST`) or `scheduled` (policy). |
| `sizeBytes` | Bytes on disk. |
| `createdAt` | When the backup completed; retention counts from here. |

- `GET …/backups` lists the registered backups oldest first. `next` is an opaque cursor for `after`.
- `POST …/backups` takes a backup now. It has no body and returns `201` with `Location`.
- `POST …/backups/{backup}/restore` creates a **new** database in the same project with the backup's data. Restore never overwrites a database.
  - `name` is optional and follows the database name rules. A taken name returns `409`.
  - Without a name, the first free name of `<source>-restore`, `<source>-restore-2`, … is used. The source part is shortened to stay within 63 characters.
  - `branchedFrom` is the backed-up database. An unknown backup returns `404`.
- Errors use the `{"error":{"code","message"}}` envelope: `400 invalid_request` (cron, retention, limit or name), `403
  forbidden_role` when a viewer backs up, restores or saves a policy, `404 not_found`, and `409 name_taken`.

#### Lifecycle

Each backup is a record in the catalog, kept under its organization, that only moves forward:

| State | Meaning |
| --- | --- |
| `pending` | Recorded with a storage sweep entry, not started. No file exists yet. |
| `snapshotting` | The snapshot copy is running; its file may exist. |
| `registered` | Complete: listed, restorable, subject to retention. |
| `expiring` | Expired, failed or abandoned: unlisted, its file is owed to the storage sweep. |
| deleted | Terminal: the record and its sweep entry are removed once the file is confirmed gone. |

- Every transition re-reads the record and the database in its own catalog write transaction, so a database delete,
  retention or policy change made meanwhile is always seen. A snapshot that fails, or whose database is deleted during
  the copy, goes to `expiring`.
- Backup files are only deleted by the **storage sweep**, a durable queue in the catalog. The transaction that makes a
  file unreferenced (an expiry, a failure, or a database delete for its backups) records the sweep entry, and an entry
  is removed only once its files are confirmed gone. Each scheduler pass works through 100 entries from a rotating cursor; failures are retried
  on later passes.
- A `pending` or `snapshotting` backup that no attempt of the running process is working on was abandoned (its process
  stopped mid-way, or recording its failure failed): a later sweep pass expires it and deletes its published snapshot,
  temporary copy and sidecars. A scheduled abandoned attempt records its start as the inferred failure time, preserving
  any newer scheduled success or failure.
- Deleting a database records, in the catalog transaction that deletes it, both its cleanup intent (see database
  lifecycle above) and a sweep entry that expires its backups, 100 per pass; its backup schedule, usage and query field
  counts are deleted in the same transaction.

### Backup policy

| Field | Rule |
| --- | --- |
| `fullBackupCron` | Standard 5-field cron, UTC. `null` or empty means no scheduled backups. |
| `retentionDays` | Required, 1 to 35. |
| `disabled` | Pauses scheduled backups. Defaults to `false`. |
| `updatedAt` | Last save; `null` until saved. |

- Before the first save, `GET` returns `{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false,"updatedAt":null}`.
  The scheduler applies this same default: a database is backed up daily at 02:00 UTC from its creation until a policy
  is saved, and the saved policy counts from its save. Every database is enrolled with the scheduler when it is
  registered. One that never had a scheduled backup is due once a cron occurrence has passed since its
  creation or policy save, and the next passes take those backups 16 per minute.
- Once a minute the server takes a `scheduled` backup when the next cron occurrence after the later of the last
  successful scheduled backup and `updatedAt` has passed. Missed occurrences produce one backup, not one per occurrence.
- Every scheduled attempt records its result: a success sets the last success (kept even after retention deletes that
  backup), and any failure (an error, or a source whose files are gone) sets the last failure.
- Before recording a selected scheduled attempt as pending, the catalog write re-checks the saved policy and last
  result. A policy change or completed scheduled backup can therefore make that selection no longer due.
- A pass takes at most 16 backups, longest due first; the rest follow on the next pass. A database whose last attempt
  failed waits behind every database that became due before the failure, so databases that keep failing take turns
  instead of blocking the others.
- Registered backups older than `retentionDays` expire, manual and scheduled alike. Retention is decided and applied in
  one catalog write transaction that re-reads the policy, so a policy saved meanwhile is the one applied. It walks a
  per-database index of the backups by `createdAt`, kept with the list, so a clock stepped back never holds an expired
  backup behind a newer one. It runs before the pass takes backups, also while `disabled` is true, and uses the default
  7 days for databases with only manual backups.
- TerraBase has no incremental backups. The dashboard's `incrementalBackupCron` is not supported and is ignored.

### Collections and schema

`GET …/databases/{database}/collections` lists every collection of the database (collections are explicit, created
through the data API), in name order: `{"items":[{"name":"people","documents":3,"bytes":412}]}`. `documents` counts live
documents (a deleted document's tombstone is not counted) and `bytes` is their JSON size. An empty collection is listed
with `0` and `0`.

Collection statistics, document counts and usage scans use the same engine read budget as SQL: 5 million work
units (8 per row, plus 4 per JSON read) or 5 seconds of execution. Exhausting it lists collections with `400` `invalid_query`,
`query too expensive`, and infers a schema with `400` `invalid_query`, `schema too expensive`; usage still answers a
plain `400` `"query too expensive"`. Cancellation stops scans at their next row (a per-row function; Turso has no
interrupt), and the function's stop condition is removed before session reuse.

`GET …/databases/{database}/collections/{collection}/schema` infers the fields of a collection:
`{"collection":"people","documents":3,"sampledDocuments":3,"fields":[{"path":"address.city","present":2,"types":{"string":2}}]}`.
The schema is inferred and read-only: TerraBase has no declared schemas yet.

- It samples the first 1000 live documents in id order, or 8 MiB of JSON at most.
- It walks object members up to depth 8 and joins names with `.`. It does not descend into arrays.
- The inferred field table is limited to 10000 paths, 1024 UTF-8 bytes per quoted path and 1 MiB of path bytes total.
  Paths are checked before concatenation. The complete JSON response, including escaping, is limited to 8 MiB.
  Exceeding any bound, or exhausting the engine scan budget, returns `400` `invalid_query`, `schema too expensive`; no
  partial fields are returned.
- The data API rejects duplicate member names, but documents stored before it did may still hold them. Schema inference
  takes the first occurrence in each object (including its subtree), like SQL filters and projections; later duplicates
  do not add types or fields.
- `present` counts the sampled documents that contain the path.
- `types` counts each JSON type at that path: `string`, `boolean`, `null`, `object`, `array`, `integer` (a number without
  fraction or exponent that fits a 64-bit integer) or `number` (any other number).
- Fields are in path order. An invalid collection name gives `400` and a collection that does not exist `404`. An empty
  collection gives `0` documents and no fields.

### Usage

| Route | Body |
| --- | --- |
| `GET …/databases/{database}/usage` | `{databaseId, storageBytes, logicalBytes, collections, documents, reads, writes, period, series}` |
| `GET …/projects/{project}/usage` | `{projectId, databases, storageBytes, logicalBytes, documents, reads, writes, period, series, items}` |

- Query, the same on every route: `granularity` (`hour`, the default, or `day`: UTC buckets), `from` (inclusive,
  rounded down to its bucket) and `to` (exclusive, rounded up; default now), as RFC 3339 date-times
  (`2026-10-06T10:00:00Z`, optional fraction, `Z` or a numeric offset; no date-only, time-only or offset-less forms). Without `from`, the
  period is the 24 hours or 30 days that end with the bucket holding `to`. `from` must be before `to` (checked before
  rounding), both from 1970 to 9998, and the period 1 to 840 hourly or 400 daily buckets; anything else, or an unknown
  granularity, is `400 invalid_request`.
- `period` is `{from, to, granularity, reads, writes}` over the requested buckets. `series` has one
  `{start, reads, writes}` point per bucket, oldest first, zeros included. A project's `items` (its databases) carry
  their `period` but an empty `series`.
- `storageBytes` is the size on disk. `logicalBytes` is the metered logical storage: the JSON bytes of the live
  documents, read from SQLite's record headers.
- Counting rule: each request the engine answers is **one read**, however many documents it returns: a document get,
  a list page, a query page (its optional count included) and a Management
  SQL run. Each committed put, patch or delete is **one write**, each operation of a transaction included; transaction
  assertions are not counted. A read counts once the engine answered it, even if the document does not exist; a
  request rejected before that (unknown collection, invalid id, too expensive) and a rolled-back transaction count
  nothing. Collection and index changes and metadata reads count nothing.
- `reads` and `writes` at the top level are totals since each database was created. They are best-effort dashboard
  usage, not billing data. A branch or restored database starts at zero.
- `documents` counts live documents; `collections` counts every collection, empty ones included.
- Project usage sums every database of the project; `items` are in creation order. There is no organization usage:
  TerraBase stores no organizations (`docs/terrabase/api.md`), so ts-dashboard sums its projects. Every database is listed before
  any is counted, so past 100 nothing is counted; a request that has run 5 seconds starts no further catalog read or count, and one whose last listing or
  count ends past them answers `400` too.
- Counts are kept in memory per database and UTC hour and saved to the catalog every 10 seconds: the lifetime totals
  (`databases/{id}/usage`, document `ops`), and one document per hour and per day (`usage-hours`, id
  `yyyy-MM-ddTHH`; `usage-days`, id `yyyy-MM-dd`). The kept buckets are the 840 hours and 400 days that end with the
  current one. A flush deletes every older bucket of each database it saves, and saves counts of an hour or day already
  past them (held through an outage) only in the kept day or the lifetime totals. So a database holds at most the kept
  buckets plus those that expired since its last flush. Reads always include the counts not yet saved.
- Unsaved counts are kept per database and hour. Before each flush, those past the kept hours fold into a day-only
  entry, and those past the kept days into a lifetime-only one, so a backlog holds at most about 1,250 entries per
  database. A series has counts only in kept buckets: a bucket past them reads as zero, saved or not, even before a
  flush prunes it.
- A failed flush keeps its counts pending and the next flush retries them. Each flush saves its id with the counts it
  adds, so a flush that fails after its commit is recognized by the next flush or usage read and not counted twice.
- A clean shutdown flushes once more, within the host's shutdown timeout. If that fails, its counts are lost and a warning is logged.
- A crash loses the counts since the last successful flush: about 10 seconds of operations while the catalog accepts
  writes, longer while flushes fail. The documents
  those operations wrote stay committed.
- Counts are kept per database of a tenant (organization, project and database). Databases created through the data API
  (`PUT /v1/projects/{project}/databases/{name}`) are in the catalog too, so they are counted like any other.
- A flush checks inside its catalog write that the database is still in its project of its organization (one point read
  per level); a deleted database's counts are dropped. Creating a database with a deleted one's name makes a new database
  with a new id, whose usage starts at zero.

### SQL

`POST /v1/projects/{project}/databases/{database}/sql/explain`
accepts `{sql}` and returns `{ok, error, collection, hasLimit, plan, indexes, suggestions:[{field, reason}]}`.
This v1 EXPLAIN endpoint requires a ts-auth project access token whose `project_id` equals the
route's project, whose `scope` grants `databases:read`, and whose optional `database` claim matches the
database name. It is served only on the public listener. `plan` contains SQLite's plan steps and `indexes` contains only the
names of the secondary indexes selected by that plan, not every index defined on the collection.
The former unversioned EXPLAIN path returns `404`; no alias is registered.
It prepares the plan without executing the query, metering a document read or recording recommendation
usage. Invalid SQL returns `400 invalid_request`; an exhausted engine work budget returns
`400 invalid_query`. A database absent or outside the token's project or database restriction returns
`404 not_found`; project-grant or database-restriction mismatches return `404 unknown_database`.
A missing scope returns `403 forbidden_scope`. A sign-in token without a project cannot explain databases.
The project must have a verified ts-auth catalog binding; an unbound project returns `404 unknown_database`.
Project grants must be JSON strings: non-string claims, a malformed or noncanonical `project_id`,
or an invalid `database` restriction return `401`.
The complete JSON body is bounded to 128 KiB (enough for a fully escaped 16,384-character statement); larger bodies
return `413 payload_too_large`, including chunked requests. Memory is reserved before reading or decoding it;
exhausted admission returns `503 unavailable`. A cross-region node outside the database's placement returns
`421 wrong_region` with `X-TerraBase-Database-Home` and `X-TerraBase-Database-Regions` before planning.
Body reception has a 30-second deadline starting before the endpoint's first catalog lookup, including on HTTP/2. An unfinished upload
returns `408 request_timeout` and releases its memory reservation without waiting for the client to disconnect.

The data page runs a read-only SQL subset against one database:
`SELECT * | field, … FROM collection [WHERE expr] [ORDER BY field [ASC|DESC], …] [LIMIT n]`, with an optional trailing `;`.
TerraBase parses it (SqlParserCS, SQLite dialect) and runs its own query; client SQL never reaches the engine.

| Rule | Value |
| --- | --- |
| Text | At most 16 KiB. Keywords are case-insensitive. Comments are ignored. |
| Fields | `_id` (document id), `_rev` (the document's version exactly as the data API reports it, `tbv1-…`; it can be selected, not compared or sorted) or dotted member paths (`address.city`): letters, digits and `_`, at most 8 members. Identifiers may be double-quoted, one member each (`"a"."b"`); a quoted name containing a dot (`"a.b"`) is refused, so a member literally named `a.b` cannot be addressed. |
| `WHERE` | `field op literal` (`= != <> < <= > >=`), `field [NOT] IN (…)` (1–256 values), `field [NOT] LIKE 'prefix%'`, `field IS [NOT] NULL`, `AND`, `OR`, `NOT`, parentheses. At most 256 conditions and 1024 values. |
| Literals | Single-quoted strings, integers, decimals, `TRUE`, `FALSE`. |
| `ORDER BY` | At most 4 keys. Ties sort by `_id`, in the direction of the first key. |
| `LIMIT` | 1–1000, default 100. |

- Anything else (joins, subqueries, functions, `WITH`, aliases, DML, DDL, parameters, several statements) is rejected. The error names the construct and never repeats the SQL text.
- `LIKE` takes only a prefix pattern: one trailing `%`, no other `%` or `_`, no `ESCAPE`. Unlike SQLite (and like PostgreSQL) it is case-sensitive. It runs as the range `field >= 'prefix' AND field < successor`, and `IN` as equalities joined by `OR`, so an index on the field serves both ([historical engine spike](https://github.com/TerraScale/terrascale/blob/e7fa366ddb289f5612c93d316b316fb92c568151/docs/terrabase/turso-spike.md), planner findings).
- Comparisons follow SQL: `!=` and `NOT IN` never match a missing or `null` field. `TRUE` and `FALSE` match JSON booleans (and, like SQLite, the numbers `1` and `0`).
- Statements read live documents only: a deleted document's tombstone is never selected.
- `POST …/sql` → `{"matched":2,"truncated":false,"rows":[{"id":"01K6RZ0Z1EF90RTETYDXR836HB","value":{…}}]}`. `value` is the whole document for `*`; otherwise it has one member per field, named as written, and a missing field is `null`. Rows stop at the limit or before the complete serialized response exceeds 8 MiB (escaped ids, values, row wrappers, separators and envelope included); `truncated` reports either. Only the projected output counts against that bound: rows are projected as they are read, one stored document at a time, so a small projection of large documents is not cut short by their stored size. A statement that needs more than the engine's work budget, the one the document query API uses (5 million SQLite instructions or 5 seconds, counted on the work SQLite does, not the rows it returns), answers `400` `invalid_query`, `query too expensive` (so does `POST …/sql/check`), also when an unindexed filter matches nothing or an unindexed sort reads the whole collection before the first row. Documents at the data API's accepted nesting depth (16) are returned whole. Invalid SQL returns `400` `invalid_request` with the rule it breaks; `…/sql/check` reports it as `{"ok":false,"error":…}` instead. Saved queries answer `400 invalid_request` for an invalid name or SQL, `403 forbidden_role` when a viewer saves, updates or deletes one, and `404 not_found`.
- `POST …/sql/check` → `{ok, error, collection, hasLimit, plan, indexes, suggestions:[{field, reason}]}`. It never returns `400` for bad SQL: it returns `ok: false` and the error. `plan` is the engine plan, `indexes` the indexes it reads, and `suggestions` the filtered or sorted fields (not `_id` or `_rev`) that no index of the collection leads with.
- A temporary `ORDER BY` sorts only document ids and keys, then reads bodies for the returned page. It admits at most
  10000 candidate documents and 8 MiB of their JSON before sorting; excess returns `400` `invalid_query`, `query too expensive`.
  An index that supplies the ordering avoids this temporary-sort admission. The streaming deadline pauses between
  SQLite steps, including while the consumer projects a row.
- Each run counts its suggested fields per `collection:field`, also one the work budget stopped. These counts drive the index recommendations.
  A database keeps at most 1000 counted fields: fields already counted keep counting, and new ones past the cap are not recorded.
  A run never waits for the catalog: counts are kept in memory and saved with the usage counts (every 10 seconds and at
  shutdown); recommendations add the counts not saved yet without writing. These counts are best-effort: a crash loses
  those since the last save, and a field first used while the buffer is full (1000 pending fields for its database, or
  about 10 000 in total) is not counted.
  Recommendation reads and apply hold the flush semaphore across the catalog read and pending-count merge, so an
  overlapping flush cannot omit or double-count its batch. SQL runs still record counts without waiting for the catalog.
  Like usage, each save stores its id beside the counts, so a save that fails after its commit is recognized by the
  next save or recommendation read and not counted twice.

### Saved queries

Saved queries belong to a project: `{id, name, sql, createdAt, updatedAt}`. `name` follows the project name rules (1–128 characters).
`sql` must be valid in the subset above. `PUT` replaces `name` and `sql` and sets `updatedAt`. Updates and deletes re-check the
user's membership in the same catalog write as the change.

Lists are in creation order by the per-organization sequence described for invitations, so the order survives restarts and
clock changes. `next` is an opaque cursor to pass back as `after`, not a query id. A page holds at most 1 MiB of saved
queries beyond the first, so it can hold fewer than `limit`.

### Indexes

`{"name":"people_email","collection":"people","fields":["email"],"state":"active"}`

- `fields` holds 1–4 distinct member paths; `_id` and `_rev` are not allowed (the primary key already orders them).
- `name` matches `[a-z][a-z0-9_]{0,62}` and is unique per database. Without a name the index is named
  `<collection>_<field>_…` when the collection and every field are only lowercase letters and digits, so `_` can only mark
  a boundary (`people_email`). Otherwise every part is escaped (`_` → `_u`, `.` → `_d`, `-` → `_h`, an upper-case
  letter → `_c` and the letter), the parts are joined by `__`, which no escaped part contains, and an 8-hex hash of the
  exact definition follows: `people_a`/`email` is `people_ua__email_<hash>` and `people`/`a_email` is
  `people__a_uemail_<hash>`. Distinct definitions get distinct names; only a name cut to 63 characters (the cut keeps
  room for the fields) relies on the hash alone.
- `409` `index_exists` means the name, or the same collection and fields, already exists.
- Errors use the `{"error":{"code","message"}}` envelope: `400 invalid_request`, `403 forbidden_role` when a viewer
  creates, drops or applies, `404 not_found` and `409`.
- The index is built before the response, so `state` is always `active`. Writes to the database wait while it builds.
- The index is a SQLite expression index on `(collection, fields…)` restricted to the live documents of its collection
  (`WHERE collection = … AND body IS NOT NULL`), so tombstones take no index space. After a build TerraBase analyzes only
  the new index, seeds or raises the estimates of the two collection-leading indexes (the primary key and
  `documents_live`) from its row count, and reloads planner statistics, so equality, `IN`, range filters and sorts on
  the leading field use the index. A range
  filter sorted only by `_id` may still read the primary key when SQLite estimates that cheaper.
- Indexes live in the database file: branches and restores keep them. An index does not require its collection to exist,
  and dropping a collection keeps its indexes (empty), which apply again if the collection is recreated.

### Index recommendations

`{"items":[{"id":"people:email","collection":"people","fields":["email"],"reason":"…","queries":12}]}`

- One recommendation per field counted by `POST …/sql` whose collection has no index that leads with it. Most queried first, then by id.
- `POST …/index-recommendations/{id}/apply` creates a single-field index with the default name: `201 {"status":"applied","index":…}`,
  or `200 {"status":"already-applied","index":…}` when an index already leads with the field. An id with no count returns `404`.
  When another index already has the default name, it returns `409` `name_taken`.
- This is a deterministic workload rule, not the terrabase-rust auto-index decider (sampled statistics, scoring, 100-observation threshold).

## Dashboard feature map

### Backed by this API

| Dashboard feature | Dashboard route | TerraBase endpoint |
| --- | --- | --- |
| New organization wizard, organization step | `POST /api/v1/dashboard/organization/new-organization` (`step=organization`) | `POST /organizations` |
| Org switcher | `POST /api/v1/dashboard/switch-organization`, `…/organization/switch` | `PUT /current-organization` |
| Shell: current organization | page render | `GET /current-organization` |
| Organization list (switcher) | page render | `GET /organizations` |
| Organization settings | `GET /organization` (page) | `GET /organizations/{organization}` |
| Rename organization | `POST /api/v1/dashboard/organization/rename` | `PATCH /organizations/{organization}` |
| Delete organization | `POST /api/v1/dashboard/organization/deletion` | `DELETE /organizations/{organization}` (see differences) |
| Create project (also wizard `step=project`) | `POST /api/v1/dashboard/organization/projects` | `POST /organizations/{organization}/projects` |
| Project list and project switcher list | page render | `GET /organizations/{organization}/projects` |
| Rename project | `POST /api/v1/dashboard/organization/projects/{projectID}/rename` | `PATCH …/projects/{project}` |
| Delete project | `POST /api/v1/dashboard/settings/delete-project` | `DELETE …/projects/{project}` |
| Database list | `GET /api/v1/dashboard/databases`, `GET /databases` | `GET …/projects/{project}/databases` |
| Database detail | `GET /databases/{id}` | `GET …/databases/{database}` |
| Create database | `POST /api/v1/dashboard/databases` | `POST …/projects/{project}/databases` |
| Branch (clone) database | `POST /api/v1/dashboard/databases/branch` | `POST …/databases/{database}/branches` |
| Delete database | `POST /api/v1/dashboard/databases/delete` | `DELETE …/databases/{database}` |
| Backups list, backup now | `GET`/`POST /api/v1/dashboard/backups`, `…/settings/backup-now` | `GET`/`POST D/backups` |
| Backup policy | `POST /api/v1/dashboard/backups/policy`, `…/settings/backup-policy` | `GET`/`PUT D/backup-policy` |
| Restore | `POST /api/v1/dashboard/backups/restore` | `POST D/backups/{backup}/restore` (no point-in-time `atMillis`) |
| Collections, schema | `GET /api/v1/dashboard/schema` | `GET D/collections`, `GET D/collections/{collection}/schema` (inferred, read-only) |
| Indexes | `GET`/`POST /api/v1/dashboard/indexes` | `GET`/`POST`/`DELETE D/indexes…` |
| Index recommendations | `POST /api/v1/dashboard/indexes/recommendations/apply` | `GET D/index-recommendations`, `POST …/{recommendation}/apply` |
| SQL console | `POST /api/v1/dashboard/data/sql[/check]` | `POST D/sql`, `POST D/sql/check` |
| Saved queries | `/api/v1/dashboard/data/saved-queries[/delete]` | `P/saved-queries…` |
| Usage | `GET /usage` | `GET D/usage`, `GET P/usage` |
| Members list | `GET /access`, organization page | `GET …/members` |
| Change member role | `/api/v1/access/members/{userID}/role`, `/api/v1/dashboard/organization/members/{userID}/role` | `PUT …/members/{user}/role` |
| Remove member | `/api/v1/access/members/{userID}/revoke`, `/api/v1/dashboard/organization/members/{userID}/remove` | `DELETE …/members/{user}` |
| Leave organization | `/api/v1/dashboard/organization/leave` | `POST …/leave` |
| Invitations | `/api/v1/access/invitations[/{id}/revoke\|/resend]`, `/invitations/{token}/respond` | commits (`invite`, `resend_invitation`, `revoke_invitation`, `accept_invitation`, `decline_invitation`), `GET …/invitations`, `POST /invitations/lookup` |
| Ownership transfer | `/api/v1/dashboard/organization/members/ownership[/confirm\|/cancel]` | `GET`/`POST`/`DELETE …/ownership-transfer`, `POST …/ownership-transfer/confirm` |
| Audit log and retention | `GET /audit`, `/api/v1/dashboard/settings/audit-retention` | `GET …/audit-events`, `GET`/`PUT …/audit-retention` |

### TerraBase, in follow-up threads stacked on this one

| Dashboard feature | Dashboard routes |
| --- | --- |
| Declared collection schemas | `POST /api/v1/dashboard/schema` |
| Live-query subscriptions | `/api/v1/dashboard/subscriptions/attach\|pause\|resume\|detach` |

### Owned elsewhere

| Dashboard feature | Dashboard routes | Owner |
| --- | --- | --- |
| Invitations, resend, organization/project changes and their audit | `/api/v1/access/invitations…`, `/audit`, `/audit/export`, organization/project settings | ts-auth; ts-dashboard calls it through the SDK |
| Project switcher (current project) | `POST /api/v1/dashboard/switch-project` | ts-dashboard session; projects come from ts-auth |
| M2M credentials, key rotation | `/api/v1/dashboard/credentials/m2m\|rotate` | ts-auth client credentials |
| Spending controls | `/api/v1/dashboard/usage/spending-controls` | ts-dashboard; usage comes from TerraBase |
| Platform admin: rename organizations/projects | `/api/v1/admin/organizations/…`, `/api/v1/admin/projects/…` | ts-auth |
| Login, logout, current user, register, onboarding, password reset | `/api/v1/auth/password/login`, `/api/v1/auth/logout`, `/api/v1/auth/me`, `/login`, `/register`, `/onboarding`, `/reset-password` | ts-auth |
| Profile, password, email, language | `/api/v1/dashboard/profile[/password\|/email-change\|/language]` | ts-auth |
| MFA (TOTP, recovery codes) | `/api/v1/dashboard/profile/mfa/enroll\|verify\|remove\|recovery-codes` | ts-auth |
| Passkeys | `/api/v1/dashboard/profile/passkeys/register\|remove` | ts-auth |
| Sessions, personal access tokens, CLI device flow, OAuth consent | `/api/v1/dashboard/profile/access-tokens[…]`, `/api/v1/dashboard/cli/device-authorization\|token`, `/cli/approve`, `/oauth/consent`, `/agent/approve` | ts-auth |
| Project auth console (end-user auth instances) | `/api/v1/auth-management/…`, `/api/v1/dashboard/settings/authentication` | ts-auth |
| Billing, checkout, plan change, customer portal, Stripe webhook | `/billing/checkout[/return]`, `/billing/change[/undo]`, `/billing/portal`, `/api/auth/stripe/webhook`, wizard `step=payment-method` | Billing (Stripe), in the dashboard |
| Plans (admin) | `/api/v1/admin/plans[/{planID}/visibility\|delete]` | Billing |
| Admin users, estate actions | `/api/v1/admin/users/{accountID}`, `/api/v1/admin/estate/action` | ts-auth (users); dashboard (estate) |
| Notifications | `/api/v1/notifications/{id}/read`, `/api/v1/notifications/read-all` | ts-dashboard |
| Database placement, regions, partition directory | none in the dashboard; SDK `placement` on create | ts-orchestrator |
| MCP server | `/mcp` | ts-dashboard (a client of this API) |

The following paragraph records the older implementation assessment, not the current ownership contract.
ts-orchestrator owns no organizations or projects and has no HTTP create, list, delete or branch
for databases. Its API is read-only (regions, decisions, history). It places database
partitions through operator-signed NATS `placement.intent` envelopes. ts-auth's C# rewrite serves
only health and docs today, and its planned organizations are per-instance end-user
organizations (`/v1/{slug}/organizations`), not TerraScale's platform organizations. Neither service
duplicates this API.

## Ownership and tenancy

Historical decision, superseded by the 2026-10-06 ownership decision and [HTTP API table](api.md):
decided by a human on 2026-10-04 (see [0005](decisions/0005-multi-tenancy.md)), TerraBase owns
organizations and projects. The organization is the tenant, because it owns billing; a project is a
folder of databases inside it. `terrabase-rust/docs/control-plane-v1.md` still says the dashboard
owns them and that a tenant is a project; it needs updating in that repository. This API still does
not implement the control-plane-v1 tenant routes (`/v1/dashboard/tenants…`), and control-plane-v1 does
not cover branches.

## Differences from other contracts

| Topic | This API | Elsewhere |
| --- | --- | --- |
| Paths | `/v1/management/…` | ts-go SDK calls `/api/v1/databases…`; control-plane-v1 uses `/v1/dashboard/tenants/{tenant}/databases` |
| Tenant | The organization is the tenant; projects group its databases | SDK sends the dashboard **organization** id as `X-TerraScale-Tenant-ID`; control-plane-v1 says tenant = project |
| IDs | Canonical uppercase ULIDs | All repositories use the same contract, document ids included |
| Database names | `^[a-z][a-z0-9_-]{0,62}$` | dashboard and SDK use `^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$` (leading digit allowed, no `_`, no trailing `-`) |
| JSON | camelCase, ISO 8601 timestamps | control-plane-v1 uses snake_case, Unix-ms strings and `next_cursor` |
| Idempotency | commits carry a client commit id with a durable, queryable outcome ([management-commits.md](management-commits.md#uncertain-outcomes)); other routes have none | control-plane-v1 requires `Idempotency-Key` on mutations |
| Authentication | ts-auth access token (`at+jwt`); `sub` is the user, the route names the organization | control-plane-v1 uses a `control`-role `tbk1.` key with no user identity |
| Regions, placement, residency | not modeled | dashboard and SDK send them on project and database create; ts-orchestrator places partitions |
| Organization delete | deletes an empty organization | dashboard only marks `deletionRequested` and keeps the data |
| Project delete | refuses a project that still has databases | dashboard refuses the default or last project |
| Database delete and branch | synchronous | SDK and control-plane-v1 return provisioning operations (`202`) |
| Restore | synchronous, into a new database, from a stored backup only | SDK restores to a point in time (`at`) and returns a provisioning operation |
| Indexes | named, 1–4 fields, built synchronously, always `active` | dashboard creates one `field` per call; control-plane-v1 indexes use JSON Pointer keys, `backfilling` states and `Idempotency-Key` |
| Schema | inferred from sampled documents, read-only | dashboard and SDK read and write declared schemas with generations |
| SQL result | `{matched, truncated, rows:[{id, value}]}` | SDK `SQLResult{matched, rows[{id, value}]}`; no explain variant |
| Usage | best-effort reads and writes, lifetime and per UTC hour or day; documents, file and logical bytes | control-plane-v1 reports the current calendar month as decimal strings |
| Roles | `owner`, `admin`, `member`, `viewer`; several owners; admins manage members and invitations | dashboard: exactly one owner, who alone manages members (it keeps one by granting `owner` only through a transfer) |
| Members | user id, role and join time | dashboard members also carry email and name; ts-auth owns profiles |

These are deliberate simplifications for the first slice. The dashboard adapter maps
names and IDs until the contracts are aligned.
