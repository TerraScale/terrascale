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

## Access

- The endpoints are served on the **client listener** (TLS) and need an API key with the
  `management` scope. Issue one with `POST /admin/api-keys` on the admin listener, for example
  `{"name":"dashboard","scopes":["management"]}`.
- The `management` scope grants no document access. The `documents:*` scopes grant no management access.
- TerraBase does not authenticate end users. The dashboard authenticates them (later through
  ts-auth) and passes the user id in `/users/{user}/…`. TerraBase stores it as an opaque string
  and does not check membership.

## Conventions

- **IDs** are lowercase hyphenated UUIDv7 strings, so lists in id order are in creation order.
  An unknown id answers `404`.
- **Organization and project names** are 1 to 128 characters after trimming. They must contain a
  letter or digit and no control characters, and are unique per parent, ignoring case. This is the
  dashboard's own rule (`organization/service.go:89`).
- **Database names** follow the data API rule: a lowercase letter, then lowercase letters, digits,
  `_` or `-`, at most 63 characters. They are unique per project.
- **Lists** take `?after=<id>&limit=1..100` (default 20) and return `{"items":[…],"next":null|"<id>"}`.
  This matches the document list endpoint.
- **Errors:** `400` for invalid input, `404` for an unknown resource or parent, `409` for a
  duplicate name or a non-empty delete. Bodies are a plain message that never echoes input.
- Every database gets a generated `storageName` (`db-<32 hex>`), which is the name the data API
  uses: `/v1/databases/{storageName}/collections/…`.

## Endpoints

Base path: `/v1/management`.

| Method | Route | Result |
| --- | --- | --- |
| `POST` | `/organizations` | `{name}` → `201` organization, `409` name taken |
| `GET` | `/organizations` | page of organizations |
| `GET` | `/organizations/{organization}` | organization |
| `PATCH` | `/organizations/{organization}` | `{name}` → renamed organization, `409` name taken |
| `DELETE` | `/organizations/{organization}` | `204`; `409` while it has projects |
| `GET` | `/users/{user}/current-organization` | the organization the user last switched to; `404` if none or deleted |
| `PUT` | `/users/{user}/current-organization` | `{organizationId}` → organization |
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
{"id":"0199…","name":"Acme","createdAt":"2026-10-04T12:00:00+00:00"}
{"id":"0199…","organizationId":"0199…","name":"Production","createdAt":"…"}
{"id":"0199…","projectId":"0199…","name":"main","storageName":"db-0199…","branchedFrom":null,"createdAt":"…"}
```

## Dashboard feature map

### Backed by this API

| Dashboard feature | Dashboard route | TerraBase endpoint |
| --- | --- | --- |
| New organization wizard, organization step | `POST /api/v1/dashboard/organization/new-organization` (`step=organization`) | `POST /organizations` |
| Org switcher | `POST /api/v1/dashboard/switch-organization`, `…/organization/switch` | `PUT /users/{user}/current-organization` |
| Shell: current organization | page render | `GET /users/{user}/current-organization` |
| Organization list (switcher, admin) | page render | `GET /organizations` |
| Organization settings | `GET /organization` (page) | `GET /organizations/{organization}` |
| Rename organization | `POST /api/v1/dashboard/organization/rename` | `PATCH /organizations/{organization}` |
| Delete organization | `POST /api/v1/dashboard/organization/deletion` | `DELETE /organizations/{organization}` (see differences) |
| Admin: rename organization | `POST /api/v1/admin/organizations/{accountID}/{organizationID}` | `PATCH /organizations/{organization}` |
| Create project (also wizard `step=project`) | `POST /api/v1/dashboard/organization/projects` | `POST /organizations/{organization}/projects` |
| Project list and project switcher list | page render | `GET /organizations/{organization}/projects` |
| Rename project | `POST /api/v1/dashboard/organization/projects/{projectID}/rename` | `PATCH …/projects/{project}` |
| Admin: rename project | `POST /api/v1/admin/projects/{accountID}/{organizationID}/{projectID}` | `PATCH …/projects/{project}` |
| Delete project | `POST /api/v1/dashboard/settings/delete-project` | `DELETE …/projects/{project}` |
| Database list | `GET /api/v1/dashboard/databases`, `GET /databases` | `GET …/projects/{project}/databases` |
| Database detail | `GET /databases/{id}` | `GET …/databases/{database}` |
| Create database | `POST /api/v1/dashboard/databases` | `POST …/projects/{project}/databases` |
| Branch (clone) database | `POST /api/v1/dashboard/databases/branch` | `POST …/databases/{database}/branches` |
| Delete database | `POST /api/v1/dashboard/databases/delete` | `DELETE …/databases/{database}` |

### TerraBase, in follow-up threads stacked on this one

| Dashboard feature | Dashboard routes |
| --- | --- |
| Members, roles, invitations, leave organization | `/api/v1/access/invitations[/{id}/resend\|revoke]`, `/api/v1/access/members/{userID}/role\|revoke`, `/api/v1/dashboard/organization/members/{userID}/role\|remove`, `/api/v1/dashboard/organization/leave` |
| Ownership transfer | `/api/v1/dashboard/organization/members/ownership[/confirm\|/cancel]` |
| Project switcher (current project) | `POST /api/v1/dashboard/switch-project` |
| Backups, backup policy, restore | `/api/v1/dashboard/backups[/policy\|/restore]`, `/api/v1/dashboard/settings/backup-policy\|backup-now\|restore` |
| Collections, schema, indexes, index recommendations | `/api/v1/dashboard/schema`, `/api/v1/dashboard/indexes[/recommendations/apply]` |
| Documents and SQL console | `/api/v1/dashboard/data/documents`, `/api/v1/dashboard/data/sql[/check]` (document routes exist at `/v1/databases/…`; SQL does not yet) |
| Saved queries, live-query subscriptions | `/api/v1/dashboard/data/saved-queries[/delete]`, `/api/v1/dashboard/subscriptions/attach\|pause\|resume\|detach` |
| Usage and spending controls | `GET /usage`, `/api/v1/dashboard/usage/spending-controls` |
| API keys and M2M credentials | `/api/v1/dashboard/credentials/key\|m2m\|rotate\|revoke`, `/api/v1/projects/{projectID}/auth/api-keys[…]` |
| Audit log and retention | `GET /audit[/export]`, `/api/v1/dashboard/settings/audit-retention` |

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

## Ownership conflict

`terrabase-rust/docs/control-plane-v1.md` says **the dashboard owns organizations, projects,
users, plans and billing**, and one TerraBase *tenant* corresponds to one dashboard project. This
feature moves organizations and projects into TerraBase, as this task requested. A human must decide
which way it goes:

1. Keep it here, and update control-plane-v1 so that TerraBase owns organizations and projects
   and a project is the tenant boundary.
2. Or move organizations and projects back to the dashboard (or ts-auth), and reduce this API
   to tenants and databases as control-plane-v1 describes.

Until that decision, this API does not implement the control-plane-v1 tenant routes
(`/v1/dashboard/tenants…`), and control-plane-v1 does not cover branches.

## Differences from other contracts

| Topic | This API | Elsewhere |
| --- | --- | --- |
| Paths | `/v1/management/…` | ts-go SDK calls `/api/v1/databases…`; control-plane-v1 uses `/v1/dashboard/tenants/{tenant}/databases` |
| Tenant | Projects own databases | SDK sends the dashboard **organization** id as `X-TerraScale-Tenant-ID`; control-plane-v1 says tenant = project |
| IDs | UUIDv7 strings | dashboard uses `org_`, `prj_`, `db_` + 32 hex |
| Database names | `^[a-z][a-z0-9_-]{0,62}$` | dashboard and SDK use `^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$` (leading digit allowed, no `_`, no trailing `-`) |
| JSON | camelCase, ISO 8601 timestamps | control-plane-v1 uses snake_case, Unix-ms strings and `next_cursor` |
| Idempotency | none | control-plane-v1 requires `Idempotency-Key` on mutations |
| Regions, placement, residency | not modeled | dashboard and SDK send them on project and database create; ts-orchestrator places partitions |
| Organization delete | deletes an empty organization | dashboard only marks `deletionRequested` and keeps the data |
| Project delete | refuses a project that still has databases | dashboard refuses the default or last project |
| Database delete and branch | synchronous | SDK and control-plane-v1 return provisioning operations (`202`) |

These are deliberate simplifications for the first slice. The dashboard adapter maps
names and IDs until the contracts are aligned.
