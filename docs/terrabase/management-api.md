# Management API (`/v1/management`)

The Management feature (`src/TerraScale.TerraBase/Features/Management`, OpenAPI tag
`Management`) stores organizations, projects and the databases they own, so ts-dashboard
has a server-side home for state it keeps in memory today. This document maps every
ts-dashboard feature to the TerraBase endpoint that backs it, or to the service that owns it
instead.

Sources, read on 2026-10-04: ts-dashboard `internal/` and `web/` (routes under
`/api/v1/dashboard`, `/api/v1/access` and `/api/v1/admin`), `terrabase-rust/docs/control-plane-v1.md`,
ts-orchestrator and ts-auth.

## Current state of the dashboard

- ts-dashboard starts only with `TS_DASHBOARD_PRODUCTION_DEMO=1` (`internal/app/app.go:51`).
  Organizations, projects, members, users and sessions live in an in-memory `core.Store`.
- Outside demo mode it reaches TerraBase through the ts-go SDK (`internal/adapters/tsgo/factory.go:25`)
  and Stripe through `stripe-go`. Nothing calls ts-auth yet.
- The browser never calls these routes as JSON. The dashboard's Go server renders `.templ`
  forms and handles the posts. The dashboard server is the client of this API, not the browser.

## Access and authorization

Decided by a human on 2026-10-04: only the ts-dashboard backend calls this API, with a dashboard
service credential and the acting user in headers. Superseded on 2026-10-05 (TS-519,
[0007](decisions/0007-ts-auth-user-tokens.md)): public calls carry the user's ts-auth access token, and a
management service credential works only on the loopback admin listener, for bootstrap before ts-auth
exists. TerraBase authorizes every request against the organizations the acting user belongs to.

### Access tokens

- The endpoints are served on the **client listener** (TLS), which accepts only ts-auth access tokens,
  and on the loopback **admin listener**, which accepts only the
  [management service credential](#management-service-credential). A token on the admin listener, or
  the credential on the client listener, gets `401`.
- Configure `TERRABASE_AUTH_ISSUER` (or `--auth-issuer=`) and `TERRABASE_AUTH_AUDIENCE` (or
  `--auth-audience=`), together; they require `client-port`. Without them the client listener answers
  `401` to every Management call. `dashboard-credential-file` no longer exists: it is an unknown
  setting (exit code `2`).
- Each request presents `Authorization: Bearer <ts-auth access token>`. The token must:
  - be a JWT with header `typ` `at+jwt` or `application/at+jwt` (RFC 9068). ID tokens and other
    JWTs from the same issuer are refused;
  - be signed with ES256/384/512, RS256/384/512 or PS256/384/512 (`none` and HS* are refused), by
    the cached key its `kid` names;
  - have `iss` equal to `auth-issuer` exactly and an `aud` that includes one of `auth-audience`, compared exactly (a trailing `/` makes another audience);
  - have an `exp`. `exp` and `nbf` are checked with 30 seconds of clock skew;
  - have a `sub` of 1 to 256 characters without control characters;
  - not be a ts-auth guest's: `is_anonymous` `true` is refused.
- Any failure answers `401` with no body. The recorded failure reason is a type name or a fixed message, never the token.
- The token grants no document access. API keys (`tb_…`) grant no management access. Either one
  used on the other's routes gets `401`.
- ts-dashboard calls with the signed-in user's token. A ts-auth client-credentials token (`sub` =
  client id) for an accepted audience acts as a user whose id is that client id: it reaches only
  organizations that id is a member of.

### Signing keys

- TerraBase reads `{auth-issuer without a trailing /}/.well-known/openid-configuration`, then its
  `jwks_uri`. Both must be `https` and answer `2xx`; redirects are not followed. A discovery document whose `issuer` is not exactly `auth-issuer`
  is not used, and its `jwks_uri` is never requested. Only RSA and EC keys are kept; symmetric (`oct`) keys are ignored. A document over
  1 MiB fails. One 10-second deadline covers both requests of a fetch, and shutdown cancels it.
- Keys are cached in memory. Startup waits up to 10 seconds for the first fetch. Until it succeeds,
  every token gets `401`, `/readyz` answers `503`, and a background loop retries every 2 seconds.
  After that the loop refreshes every 10 minutes. A failed refresh keeps the cached keys, so ts-auth
  being down after startup does not stop validation.
- Requests never fetch keys, with one exception: a token whose `kid` is not cached triggers one
  refresh, shared by concurrent requests and at most once per 30 seconds. If the keys changed, the
  token is validated once more.
- Counter `terrabase.auth.key_fetches`, label `outcome` (`success` or `failure`). TerraBase's fetch
  failure logs name the setting (`auth-issuer`) and the failure type, never the issuer URL or the audiences.

### Management service credential

Decided by COS on 2026-10-05: the TS-529 bootstrap creates the `terrascale` organization (projects
`ts-auth`, `ts-dashboard`) before ts-auth exists, because ts-auth itself persists there.

- Configure `TERRABASE_MANAGEMENT_CREDENTIAL_FILE` (or `--management-credential-file=`): a file holding a
  secret of at least 32 characters; surrounding whitespace is ignored and only its SHA-256 is kept. It
  needs no `client-port`. Unset, the credential authenticates no one. A missing, unreadable or short
  secret stops the process with exit code `2`, naming the setting, never the path.
- Accepted **only on the loopback admin listener**, never on the public client listener.
- Each request presents `Authorization: Bearer <secret>` and `X-TerraBase-User-Id: <user id>` (one value,
  1 to 256 characters, no control characters). TerraBase compares the two SHA-256 hashes with
  `CryptographicOperations.FixedTimeEquals`. A wrong secret or a missing or invalid user answers `401`.
- The named user then acts exactly like a token's `sub`: membership and roles apply as for anyone.
  The credential carries no verified email, so it cannot accept invitations (`403`).
- Every use is logged: `Management service credential acted as user <id>: <method> <path>` at
  Information, and `Management service credential rejected: <method> <path>` at Warning. The secret is
  never logged.
- Rotating the secret means restarting with a new file.

### Acting user and organization

- The acting user is the token's `sub`, or the user the management service credential names on the
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
- Project access is decided at the organization level for now: every member, whatever its role, can
  manage every project and database in the organization. Renaming the organization needs `admin` and
  deleting it needs `owner` (`403` otherwise). Per-project roles are a follow-up.
- `GET /organizations` lists only the acting user's organizations. The current organization can
  be set only to an organization the user belongs to, and it reads as `404` once the user no longer
  belongs to it.
- Platform-admin routes in the dashboard (`/api/v1/admin/…`) act on any organization. They need a
  separate admin authorization design and are not served by this API.

### Upgrading a catalog from before roles

Catalog version 2 (TS-461) stored members without roles. Version 3 stores each member as
`{userId, role, joinedAt}`. On its first Management request, a process upgrades a version 2 catalog in one
write transaction: every member becomes an `owner`, because version 2 could only make an organization's
creator (or the legacy owner) a member. `joinedAt` is the organization's creation time. No setting is needed.

### Upgrading a catalog from before membership

The first Management release (#5) stored organizations with no members and with globally
unique names. That is catalog version 1. This release uses version 4 and records it in the
`catalog/version` document of the `_system` database.

- On its first Management request, each process brings the catalog to version 4, in one write
  transaction.
- If the catalog is still version 1 and holds organizations, set `TERRABASE_MANAGEMENT_LEGACY_OWNER`
  (or `--management-legacy-owner=`) to a ts-auth user id. In that same transaction, every version 1
  organization:
  - gets that user as `createdBy` and only member, an `owner`;
  - moves its name claim from the global index to that user's own index.

  Projects and databases are untouched.
- Without the setting, the Management API answers `503` and logs an error naming the setting. The
  log gives only the number of organizations, no ids. Nothing is changed, and nothing is silently
  hidden.
- Once migrated, the version is recorded, so later starts need no setting.
- Version 3 upgrades to version 4 without changing existing resources.
- A catalog with any other version (for example, one written by a newer build) is never migrated
  or downgraded. The Management API answers `503`, logs an error and leaves the catalog unchanged.
- `management-legacy-owner` must be a non-blank user id: at most 256 characters and no control
  characters. Anything else stops the process with exit code `2`.
- A user's current organization from version 1 reads as `404` unless that user is the assigned owner.

### Database lifecycle recovery (TS-480)

Version 4 adds `pending_database_cleanup` documents in `_system.db`, keyed by database id and
holding its tenant context. Builds that only understand version 3 refuse this catalog; downgrade
requires a supported recovery procedure, not editing the version marker.

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
- **Startup:** replay all version 4 intents before any listener serves requests. Replay is idempotent,
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

This covers TerraBase process crashes. SQLite still uses `synchronous = NORMAL` (decision 0003),
so it does not add a power-loss or operating-system-crash durability guarantee. Older catalogs keep
existing migration and `503` behavior; they contain no version 4 intents and are never swept.
Unsupported catalog versions are audited without interpreting tenant rows; their files are preserved,
logged and counted as unknown while management requests keep returning `503`.

## Conventions

- **IDs** are lowercase hyphenated UUIDv7 strings. Lists in id order (organizations, projects, databases)
  are in creation order to the millisecond (ids created within the same millisecond sort by their random bits).
  An unknown id answers `404`.
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
  per-organization sequence (invitations, audit events, backups, API keys, saved queries) return an opaque cursor.
  Either way, pass `next` back as `after`.
- **Errors:** `400` for invalid input, `403` when the acting member's role does not allow the request,
  `404` for an unknown resource or parent (or an organization the user is not a member of), `409` for a
  duplicate name, a non-empty delete or a rule in [Members and roles](#members-and-roles). Bodies are
  `{"error":{"code","message"}}` with a stable code (`invalid_request`, `forbidden_role`, `not_found`,
  `name_taken`, `not_empty`, `last_owner`, `already_member`, `invitation_pending`, `already_owner`,
  `invitation_expired`) and a message that never echoes input ([error handling](error-handling.md)).
- The data API addresses a database as `/v1/projects/{projectId}/databases/{name}/collections/…`,
  with the database name from this API. The API key's organization selects the tenant.
- Every organization has a `plan`: `hobby` (default), `pro` or `enterprise`. It is stored only;
  nothing enforces it yet.

## Endpoints

Base path: `/v1/management`.

| Method | Route | Result |
| --- | --- | --- |
| `POST` | `/organizations` | `{name}` → `201` organization with the acting user as its first member; `409` name taken |
| `GET` | `/organizations` | page of the acting user's organizations |
| `GET` | `/organizations/{organization}` | organization |
| `PATCH` | `/organizations/{organization}` | `{name}` → renamed organization, `409` name taken; needs `admin` |
| `PUT` | `/organizations/{organization}/plan` | `{plan}` (`hobby`, `pro` or `enterprise`) → organization; `400` for any other value; needs `owner`. Recorded in the audit log as `settings.plan_updated` |
| `DELETE` | `/organizations/{organization}` | `204`; `409` while it has projects; needs `owner`. Also deletes its members, invitations, transfer and audit log |
| `GET` | `/current-organization` | the organization the acting user last switched to; `404` if none or no longer a member |
| `PUT` | `/current-organization` | `{organizationId}` → organization; `404` if the user is not a member |
| `POST` | `/organizations/{organization}/projects` | `{name}` → `201` project |
| `GET` | `/organizations/{organization}/projects` | page of projects |
| `GET` | `/organizations/{organization}/projects/{project}` | project |
| `PATCH` | `/organizations/{organization}/projects/{project}` | `{name}` → renamed project |
| `DELETE` | `/organizations/{organization}/projects/{project}` | `204`; `409` while it has databases |
| `POST` | `…/projects/{project}/databases` | `{name}` → `201` empty database |
| `GET` | `…/projects/{project}/databases` | page of databases |
| `GET` | `…/projects/{project}/databases/{database}` | database |
| `DELETE` | `…/projects/{project}/databases/{database}` | `204`; deletes the data. Branches are independent and stay. |
| `POST` | `…/databases/{database}/branches` | `{name}` → `201` database in the same project holding a consistent snapshot copy |

Shapes:

```json
{"id":"0199…","name":"Acme","createdBy":"<ts-auth user id>","createdAt":"2026-10-04T12:00:00+00:00","plan":"hobby"}
{"id":"0199…","organizationId":"0199…","name":"Production","createdAt":"…"}
{"id":"0199…","projectId":"0199…","name":"main","branchedFrom":null,"createdAt":"…","region":"gru-1"}
```

Database create, branch, get and list responses report the serving node's `region`,
also returned in `X-TerraBase-Region`. It is response metadata, not persisted
placement. See [region identity](region-identity.md) for Atlas session pins and `421` responses.

## Members and roles

Roles, from least to most privileged: `member`, `admin`, `owner`. The routes below are under
`/v1/management/organizations/{organization}` and follow [Access and authorization](#access-and-authorization):
a user who is not a member gets `404`, and a member whose role is too low gets `403`.

| Method | Route | Needs | Result |
| --- | --- | --- | --- |
| `GET` | `/members` | member | page of members in user id order |
| `PUT` | `/members/{user}/role` | admin; owner if the member is or becomes an owner | `{role}` → member; `409` when it would demote the last owner |
| `DELETE` | `/members/{user}` | admin; owner to remove an owner | `204`; `409` for the last owner |
| `POST` | `/leave` | member | `204`; `409` for the last owner |
| `POST` | `/invitations` | admin; owner to invite an owner | `{email, role}` (role defaults to `member`) → `201` `{invitation, token}`; `409` while an unexpired invitation for the email is pending |
| `GET` | `/invitations` | admin | page of pending invitations in creation order, expired ones included; `after` is the previous page's `next` |
| `DELETE` | `/invitations/{invitation}` | admin | `204`; the token stops working |
| `GET` | `/ownership-transfer` | member | pending transfer; `404` if none |
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
invitation, `404` for an unknown token, `409` when the invitation expired or the user is already a member.

Rules:

- **The last owner** cannot be demoted, removed or leave. An organization can have several owners;
  only owners grant, revoke or transfer ownership.
- **Invitations** expire after 7 days. The token (`<organization>.<invitation>.<secret>`) is returned
  only by the create call; TerraBase stores the SHA-256 of its secret and compares it in constant time.
  Inviting an address whose invitation expired replaces it. TerraBase sends no email: the dashboard delivers
  the link. On accept, the access token's verified email must match the invitation ignoring case.
- **Ownership transfer** follows the dashboard: one pending transfer per organization, valid for 7 days.
  Only the nominee confirms. The offer lapses (`404`) as soon as its starter stops being an owner or
  either side leaves, even if the starter later regains ownership.
- **Audit events** record every change above as `{id, actor, action, target, detail, occurredAt}`.
  Actions: `member.role_changed`, `member.removed`, `member.left`, `invitation.created`,
  `invitation.revoked`, `invitation.accepted`, `ownership.transfer_started`,
  `ownership.transfer_confirmed`, `ownership.transfer_cancelled`, `settings.audit_retention_updated`,
  `settings.plan_updated`. `target` is a user id or an invitation email; `detail` is the new role, the
  number of days or the new plan. Retention
  is measured on each event's exact `occurredAt`: events older than the retention stop being listed at once
  and are deleted, oldest first, as new events are recorded.
- **Order:** invitations list in creation order and audit events newest first, by a per-organization
  sequence. The sequence comes from a counter document incremented in the same write transaction as the
  insert, and is the key the list is stored under. The order therefore survives restarts, does not depend
  on the clock, and holds within one millisecond. Invitation and event ids are opaque UUIDs. On these two
  lists `next` is an opaque cursor to pass back as `after`, not an item id.

Shapes:

```json
{"userId":"<ts-auth user id>","role":"owner","joinedAt":"2026-10-04T12:00:00+00:00"}
{"invitation":{"id":"0199…","email":"ada@example.com","role":"admin","invitedBy":"<user id>","createdAt":"…","expiresAt":"…"},"token":"0199….0199….<secret>"}
{"fromUserId":"<user id>","toUserId":"<user id>","createdAt":"…","expiresAt":"…"}
{"id":"0199…","actor":"<user id>","action":"member.role_changed","target":"<user id>","detail":"admin","occurredAt":"…"}
```

Not covered yet: invitation resend (revoke and invite again), audit filters and export, and audit events
for organization, project and database changes.

## Database tooling endpoints

These back the dashboard's database tooling pages (TS-463). Same access, conventions and errors as above: a
ts-auth access token (or, on the administrative listener only, the management service credential) on every route,
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
| `GET` | `D/usage` | storage, documents, reads and writes of the database |
| `GET` | `P/usage` | the same summed over the project, with one item per database; 400 `query too expensive` past 100 databases (before counting) or when counts are still running after 5 s |
| `GET` | `P/saved-queries` | page of saved queries |
| `POST` | `P/saved-queries` | `{name, sql}` → `201` saved query |
| `GET` | `P/saved-queries/{query}` | saved query |
| `PUT` | `P/saved-queries/{query}` | `{name, sql}` → saved query |
| `DELETE` | `P/saved-queries/{query}` | `204` |
| `POST` | `P/api-keys` | `{name, scopes, databaseId?}` → `201 {key, secret}`; the secret is shown once |
| `GET` | `P/api-keys` | page of keys, never secrets |
| `DELETE` | `P/api-keys/{key}` | `204`; the key stops working at once |

Membership is checked in the same catalog transaction as every write to the catalog that creates access or
data: API keys, backups, backup policies and restores. The other routes (SQL, indexes, schema, usage, saved queries)
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
| `id` | Opaque UUID. |
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
  registered. Databases registered by older builds are enrolled once per catalog, on its first use after the upgrade
  (before any scheduler pass). One that never had a scheduled backup is due once a cron occurrence has passed since its
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

Collection statistics, document counts and usage scans use the same engine read budget as SQL: 5 million SQLite
instructions or 5 seconds of execution. Exhausting it returns `400` `"query too expensive"`; schema inference returns
`400` `"schema too expensive"`. Cancellation interrupts scans and the progress handler is removed before session reuse.

`GET …/databases/{database}/collections/{collection}/schema` infers the fields of a collection:
`{"collection":"people","documents":3,"sampledDocuments":3,"fields":[{"path":"address.city","present":2,"types":{"string":2}}]}`.
The schema is inferred and read-only: TerraBase has no declared schemas yet.

- It samples the first 1000 live documents in id order, or 8 MiB of JSON at most.
- It walks object members up to depth 8 and joins names with `.`. It does not descend into arrays.
- The inferred field table is limited to 10000 paths, 1024 UTF-8 bytes per quoted path and 1 MiB of path bytes total.
  Paths are checked before concatenation. The complete JSON response, including escaping, is limited to 8 MiB.
  Exceeding any bound, or exhausting the engine scan budget, returns `400` `"schema too expensive"`; no partial fields
  are returned.
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
| `GET …/databases/{database}/usage` | `{databaseId, storageBytes, collections, documents, reads, writes}` |
| `GET …/projects/{project}/usage` | `{projectId, databases, storageBytes, documents, reads, writes, items}` |

- `storageBytes` is the size on disk.
- `reads` counts data API document gets, and list and query pages (a query's optional count is part of its page).
  `writes` counts puts, patches and deletes once their transaction commits, each operation of a transaction included;
  transaction assertions are not counted. A read counts once the engine answered it, even if the document does not exist;
  a request rejected before that (unknown collection, invalid id) and a rolled-back transaction count nothing.
- `documents` counts live documents; `collections` counts every collection, empty ones included.
- Counters are totals since the database was created. They are best-effort dashboard usage, not billing data. There is no
  billing period. A branch or restored database starts at zero.
- Project usage sums every database of the project. `items` lists each database in creation order.
- Counts are kept in memory and saved to the catalog every 10 seconds. Reads always include the counts not yet saved.
- A failed flush keeps its counts pending and the next flush retries them. Each flush saves its id with the counts it
  adds, so a flush that fails after its commit is recognized by the next flush or usage read and not counted twice.
- A clean shutdown flushes once more, within the host's shutdown timeout. If that flush fails, its counts are lost and a
  warning is logged.
- A crash loses the counts since the last successful flush: about 10 seconds of operations while the catalog accepts
  writes, longer while flushes fail. The documents those operations wrote stay committed.
- Counts are kept per database of a tenant (organization, project and database). Databases created through the data API
  (`PUT /v1/projects/{project}/databases/{name}`) are in the catalog too, so they are counted like any other.
- A flush checks inside its catalog write that the database is still in its project of its organization (one point read
  per level); a deleted database's counts are dropped. Creating a database with a deleted one's name makes a new database
  with a new id, whose usage starts at zero.

### SQL

The data page runs a read-only SQL subset against one database:
`SELECT * | field, … FROM collection [WHERE expr] [ORDER BY field [ASC|DESC], …] [LIMIT n]`, with an optional trailing `;`.
TerraBase parses it (SqlParserCS, SQLite dialect) and runs its own query; client SQL never reaches the engine.

| Rule | Value |
| --- | --- |
| Text | At most 16 KiB. Keywords are case-insensitive. Comments are ignored. |
| Fields | `_id` (document id), `_rev` (the document's version exactly as the data API reports it, `tbv1-…`; it can be selected, not compared or sorted) or dotted member paths (`address.city`): letters, digits and `_`, at most 8 members. Identifiers may be double-quoted, one member each (`"a"."b"`); a quoted name containing a dot (`"a.b"`) is refused, so a member literally named `a.b` cannot be addressed. |
| `WHERE` | `field op literal` (`= != <> < <= > >=`), `field [NOT] IN (…)` (1–256 values), `field IS [NOT] NULL`, `AND`, `OR`, `NOT`, parentheses. At most 256 conditions and 1024 values. |
| Literals | Single-quoted strings, integers, decimals, `TRUE`, `FALSE`. |
| `ORDER BY` | At most 4 keys. Ties sort by `_id`, in the direction of the first key. |
| `LIMIT` | 1–1000, default 100. |

- Anything else (joins, subqueries, functions, `WITH`, aliases, DML, DDL, parameters, several statements) is rejected. The error names the construct and never repeats the SQL text.
- Comparisons follow SQL: `!=` and `NOT IN` never match a missing or `null` field. `TRUE` and `FALSE` match JSON booleans (and, like SQLite, the numbers `1` and `0`).
- Statements read live documents only: a deleted document's tombstone is never selected.
- `POST …/sql` → `{"matched":2,"truncated":false,"rows":[{"id":"0199b1f0-7c2e-7a41-8d3b-5e6f70819a2b","value":{…}}]}`. `value` is the whole document for `*`; otherwise it has one member per field, named as written, and a missing field is `null`. Rows stop at the limit or before the complete serialized response exceeds 8 MiB (escaped ids, values, row wrappers, separators and envelope included); `truncated` reports either. Only the projected output counts against that bound: rows are projected as they are read, one stored document at a time, so a small projection of large documents is not cut short by their stored size. A statement that needs more than the engine's work budget, the one the document query API uses (5 million SQLite instructions or 5 seconds, counted on the work SQLite does, not the rows it returns), answers `400` `"query too expensive"`, also when an unindexed filter matches nothing or an unindexed sort reads the whole collection before the first row. Documents at the data API's accepted nesting depth (16) are returned whole. Invalid SQL returns `400`.
- `POST …/sql/check` → `{ok, error, collection, hasLimit, plan, indexes, suggestions:[{field, reason}]}`. It never returns `400` for bad SQL: it returns `ok: false` and the error. `plan` is the engine plan, `indexes` the indexes it reads, and `suggestions` the filtered or sorted fields (not `_id` or `_rev`) that no index of the collection leads with.
- A temporary `ORDER BY` sorts only document ids and keys, then reads bodies for the returned page. It admits at most
  10000 candidate documents and 8 MiB of their JSON before sorting; excess returns `400` `"query too expensive"`.
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
- `409` means the name, or the same collection and fields, already exists.
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
- This is a deterministic workload rule, not the terrabase-rust auto-index decider (sampled statistics, scoring, 100-observation threshold).

### Project API keys

A project key is an API key of the project's organization narrowed to the project (decision 0005): it reaches the data API
(`/v1/projects/{project}/databases/{name}/…`) only for the databases of its project.

| Field | Meaning |
| --- | --- |
| `id` | UUIDv7. Lists are in creation order. |
| `projectId` | The owning project. |
| `name` | Label of 1 to 128 characters. Same rules as project names. |
| `scopes` | A non-empty subset of `documents:read` and `documents:write`. |
| `databaseId` | Optional. Restricts the key to one database of the project. `null` means every database of the project. |
| `lastFour` | The last 4 characters of the secret. |
| `createdAt` | Creation time. |

- `POST …/api-keys` returns `201 {key, secret}`. The secret (`tb_…`) is shown only once. Only its SHA-256 is stored.
- Any other scope gives 400. A `databaseId` that is not a database of this project gives 404.
- `GET …/api-keys` never returns secrets or hashes.
- Key lists use the same persisted per-organization sequence pattern as invitations, allocated in the key's write
  transaction. Pagination uses the opaque `next` sequence as `after`, not the key UUID. Keys created at one timestamp,
  after a clock change or after a restart keep committed creation order.
- `DELETE …/api-keys/{key}` returns 204. The key stops working at once: its next request gets 401.
- A key belongs to its organization (the tenant) and project. The data API resolves a database only inside the key's
  organization (`ApiKey.Reaches`): TerraBase never looks a database up by name across organizations.
- A database outside the key's project, or outside its `databaseId`, answers `404` like a database that does not exist,
  so a key cannot learn which databases exist. A project key creates databases (`PUT /v1/projects/{project}/databases/{name}`)
  only in its own project; a key restricted to one database creates none. Project keys never reach `/v1/management`.
- Deleting a project returns 409 while it has API keys. Revoke them first.
- Deleting a database leaves keys restricted to it in place. They then reach no database (404). Revoke them.
- A key that the administrative listener (`POST /admin/api-keys`) narrows to a project is stored and listed the same
  way: it appears here, can be revoked here, and blocks deleting its project. The scope it names is checked in the
  issuing write; a missing one answers 400. Its `name` and `scopes` follow the administrative endpoint's rules (up to
  100 characters; any client scope, such as `shapes:read`). Organization-wide keys are not listed here. Project keys
  issued by older builds are listed once per catalog, on its first use after the upgrade; only a hash of their secret was
  stored, so their `lastFour` is empty, their `createdAt` is the upgrade time, and an older key may have a non-UUID id.
  They follow the keys already listed at the upgrade, in the order of their secrets' hashes.

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
| API keys | `/api/v1/dashboard/credentials/key\|revoke`, `/api/v1/projects/{projectID}/auth/api-keys[…]` | `P/api-keys…` |
| Members list | `GET /access`, organization page | `GET …/members` |
| Change member role | `/api/v1/access/members/{userID}/role`, `/api/v1/dashboard/organization/members/{userID}/role` | `PUT …/members/{user}/role` |
| Remove member | `/api/v1/access/members/{userID}/revoke`, `/api/v1/dashboard/organization/members/{userID}/remove` | `DELETE …/members/{user}` |
| Leave organization | `/api/v1/dashboard/organization/leave` | `POST …/leave` |
| Invitations | `/api/v1/access/invitations[/{id}/revoke]`, `/invitations/{token}/respond` | `POST`/`GET …/invitations`, `DELETE …/invitations/{invitation}`, `POST /invitations/accept` |
| Ownership transfer | `/api/v1/dashboard/organization/members/ownership[/confirm\|/cancel]` | `GET`/`POST`/`DELETE …/ownership-transfer`, `POST …/ownership-transfer/confirm` |
| Audit log and retention | `GET /audit`, `/api/v1/dashboard/settings/audit-retention` | `GET …/audit-events`, `GET`/`PUT …/audit-retention` |

### TerraBase, in follow-up threads stacked on this one

| Dashboard feature | Dashboard routes |
| --- | --- |
| Invitation resend | `/api/v1/access/invitations/{id}/resend` |
| Project switcher (current project) | `POST /api/v1/dashboard/switch-project` |
| Declared collection schemas | `POST /api/v1/dashboard/schema` |
| Live-query subscriptions | `/api/v1/dashboard/subscriptions/attach\|pause\|resume\|detach` |
| Spending controls | `/api/v1/dashboard/usage/spending-controls` |
| M2M credentials, key rotation | `/api/v1/dashboard/credentials/m2m\|rotate` |
| Audit export and filters | `GET /audit/export`, `GET /audit?action=…&range=…&search=…` |
| Platform admin: rename any organization or project | `/api/v1/admin/organizations/{accountID}/{organizationID}`, `/api/v1/admin/projects/{accountID}/{organizationID}/{projectID}` (needs admin authorization, see above) |

### Owned elsewhere

| Dashboard feature | Dashboard routes | Owner |
| --- | --- | --- |
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

ts-orchestrator owns no organizations or projects and has no HTTP create, list, delete or branch
for databases. Its API is read-only (regions, decisions, history). It places database
partitions through operator-signed NATS `placement.intent` envelopes. ts-auth's C# rewrite serves
only health and docs today, and its planned organizations are per-instance end-user
organizations (`/v1/{slug}/organizations`), not TerraScale's platform organizations. Neither service
duplicates this API.

## Ownership and tenancy

Decided by a human on 2026-10-04 (see [0005](decisions/0005-multi-tenancy.md)): TerraBase owns
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
| IDs | UUIDv7 strings | dashboard uses `org_`, `prj_`, `db_` + 32 hex |
| Database names | `^[a-z][a-z0-9_-]{0,62}$` | dashboard and SDK use `^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$` (leading digit allowed, no `_`, no trailing `-`) |
| JSON | camelCase, ISO 8601 timestamps | control-plane-v1 uses snake_case, Unix-ms strings and `next_cursor` |
| Idempotency | none | control-plane-v1 requires `Idempotency-Key` on mutations |
| Authentication | ts-auth access token (`at+jwt`); `sub` is the user, the route names the organization | control-plane-v1 uses a `control`-role `tbk1.` key with no user identity |
| Regions, placement, residency | not modeled | dashboard and SDK send them on project and database create; ts-orchestrator places partitions |
| Organization delete | deletes an empty organization | dashboard only marks `deletionRequested` and keeps the data |
| Project delete | refuses a project that still has databases | dashboard refuses the default or last project |
| Database delete and branch | synchronous | SDK and control-plane-v1 return provisioning operations (`202`) |
| Restore | synchronous, into a new database, from a stored backup only | SDK restores to a point in time (`at`) and returns a provisioning operation |
| Indexes | named, 1–4 fields, built synchronously, always `active` | dashboard creates one `field` per call; control-plane-v1 indexes use JSON Pointer keys, `backfilling` states and `Idempotency-Key` |
| Schema | inferred from sampled documents, read-only | dashboard and SDK read and write declared schemas with generations |
| SQL result | `{matched, truncated, rows:[{id, value}]}` | SDK `SQLResult{matched, rows[{id, value}]}`; no explain variant |
| API keys | `tb_` secret, SHA-256 stored, scopes `documents:read\|write`, optional `databaseId` | control-plane-v1 `tbk1.<id>.<secret>` tokens with grants, row filters, rate limits, expiry and rotation |
| Usage | best-effort lifetime reads and writes; documents and bytes | control-plane-v1 reports the current calendar month as decimal strings |
| Roles | `owner`, `admin`, `member`; several owners; admins manage members and invitations | dashboard: also `viewer`; exactly one owner, who alone manages members |
| Members | user id, role and join time | dashboard members also carry email and name; ts-auth owns profiles |
| Invitation decline, resend | not modeled; revoke and invite again | dashboard stores `declined` and `revoked` and reissues tokens on resend |

These are deliberate simplifications for the first slice. The dashboard adapter maps
names and IDs until the contracts are aligned.
