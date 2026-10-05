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

Decided by a human on 2026-10-04: only the ts-dashboard backend calls this API. It authenticates
with a dashboard service credential and names the acting user in headers. TerraBase authorizes
every request against the organizations that user belongs to.

### Service credential

- The endpoints are served only on the **client listener** (TLS). Requests to them on the
  admin listener get `404`.
- Configure `TERRABASE_DASHBOARD_CREDENTIAL_FILE` (or `--dashboard-credential-file=`). It is the
  path of a file that holds the secret, at least 32 characters; surrounding whitespace is ignored.
  It requires `client-port`. A missing, unreadable or short secret stops the process with exit
  code `2`, and the error names the setting, never the path or the secret.
- TerraBase keeps only the SHA-256 of the secret. Each request presents
  `Authorization: Bearer <secret>`, and TerraBase compares the two SHA-256 hashes with
  `CryptographicOperations.FixedTimeEquals`. That is constant-time, and because both hashes are
  32 bytes it does not reveal the secret's length either.
- With no credential configured, the Management API answers `401` to everything.
- The credential grants no document access. API keys (`tb_…`) grant no management access.
  Either one used on the other's routes gets `401`.
- Rotating the secret means restarting with a new file. A credential store with overlapping
  secrets is a follow-up.

### Acting user and organization

| Header | Required on | Rule |
| --- | --- | --- |
| `X-TerraBase-User-Id` | every route | The ts-auth user id: one value, 1 to 256 characters, no control characters. Otherwise `400`. |
| `X-TerraBase-Organization-Id` | every `/organizations/{organization}/…` route | One value equal to `{organization}` in the path. Otherwise `400`. |

- TerraBase trusts these headers only because the service credential authenticated the caller.
  The dashboard must take the user id from its ts-auth session and never from browser input.
- The organization header states which organization the user is acting in. A mismatch with the
  path is a dashboard bug, so it fails with `400` instead of being resolved one way or the other.

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
- **Unknown files:** preserve files without a catalog registration and log each path. The
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

- **IDs** are lowercase hyphenated UUIDv7 strings, so lists in id order are in creation order.
  An unknown id answers `404`.
- **Organization and project names** are 1 to 128 characters after trimming. They must contain a
  letter or digit and no control characters, and are compared ignoring case.
  - Project names are unique within their organization.
  - Organization names are unique among the organizations created by the same user (`createdBy`).
    A global rule would let one tenant probe for another's organization names. The dashboard uses
    the same per-owner rule (`organization/service.go:89`).
- **Database names** follow the data API rule: a lowercase letter, then lowercase letters, digits,
  `_` or `-`, at most 63 characters. They are unique per project.
- **Lists** take `?after=<id>&limit=1..100` (default 20) and return `{"items":[…],"next":null|"<id>"}`.
  This matches the document list endpoint.
- **Errors:** `400` for invalid input, `403` when the acting member's role does not allow the request,
  `404` for an unknown resource or parent (or an organization the user is not a member of), `409` for a
  duplicate name, a non-empty delete or a rule in [Members and roles](#members-and-roles). Bodies are a plain
  message that never echoes input.
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
`{token, email}`, the acting user in `X-TerraBase-User-Id` and no organization header (the token names the
organization). It answers the new membership; `403` when `email` differs from the invitation, `404` for an
unknown token, `409` when the invitation expired or the user is already a member.

Rules:

- **The last owner** cannot be demoted, removed or leave. An organization can have several owners;
  only owners grant, revoke or transfer ownership.
- **Invitations** expire after 7 days. The token (`<organization>.<invitation>.<secret>`) is returned
  only by the create call; TerraBase stores the SHA-256 of its secret and compares it in constant time.
  Inviting an address whose invitation expired replaces it. TerraBase sends no email: the dashboard delivers
  the link and, on accept, passes the signed-in user's verified email, which must match the invitation
  ignoring case.
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
| Backups, backup policy, restore | `/api/v1/dashboard/backups[/policy\|/restore]`, `/api/v1/dashboard/settings/backup-policy\|backup-now\|restore` |
| Collections, schema, indexes, index recommendations | `/api/v1/dashboard/schema`, `/api/v1/dashboard/indexes[/recommendations/apply]` |
| Documents and SQL console | `/api/v1/dashboard/data/documents`, `/api/v1/dashboard/data/sql[/check]` (document routes exist at `/v1/projects/{project}/databases/{database}/…`; SQL does not yet) |
| Saved queries, live-query subscriptions | `/api/v1/dashboard/data/saved-queries[/delete]`, `/api/v1/dashboard/subscriptions/attach\|pause\|resume\|detach` |
| Usage and spending controls | `GET /usage`, `/api/v1/dashboard/usage/spending-controls` |
| API keys and M2M credentials | `/api/v1/dashboard/credentials/key\|m2m\|rotate\|revoke`, `/api/v1/projects/{projectID}/auth/api-keys[…]` |
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
| Authentication | dashboard service credential plus user and organization headers | control-plane-v1 uses a `control`-role `tbk1.` key with no user identity |
| Regions, placement, residency | not modeled | dashboard and SDK send them on project and database create; ts-orchestrator places partitions |
| Organization delete | deletes an empty organization | dashboard only marks `deletionRequested` and keeps the data |
| Project delete | refuses a project that still has databases | dashboard refuses the default or last project |
| Database delete and branch | synchronous | SDK and control-plane-v1 return provisioning operations (`202`) |
| Roles | `owner`, `admin`, `member`; several owners; admins manage members and invitations | dashboard: also `viewer`; exactly one owner, who alone manages members |
| Members | user id, role and join time | dashboard members also carry email and name; ts-auth owns profiles |
| Invitation decline, resend | not modeled; revoke and invite again | dashboard stores `declined` and `revoked` and reissues tokens on resend |

These are deliberate simplifications for the first slice. The dashboard adapter maps
names and IDs until the contracts are aligned.
