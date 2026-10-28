# Platform identity contract (TS-598)

The [zero-configuration contract](zero-configuration.md) is the required
production policy. Option names below inventory the current implementation;
manual switches, pre-created secret dependencies and optional delivery paths
remain gaps. They are not production setup instructions. Tenant authorization,
customer enrollment and storage safety/consistency requirements remain binding.

ts-auth is the single owner of identity and access for TerraScale (epic TS-597,
umbrella `AGENTS.md`, "Service ownership"). This document is the contract that
TerraBase (TS-589), the SDKs (ts-go TS-590, ts-dotnet TS-591, ts-js TS-592,
ts-cli TS-600) and ts-dashboard (TS-599) build on. The machine-readable
contract is [`platform-api.openapi.yaml`](platform-api.openapi.yaml)
(`make openapi-check` lints it).

Status: contract v1.1, 2026-10-06: v1 plus the TS-597 decisions (the
`usage:read` and `billing:*` scopes, project suspension, platform
administrators, the ts-cli device flow and ts-auth's own storage token).
Nothing here is served until its slice lands;
[`parity.md`](parity.md) tracks each slice. The served `/openapi/v1.json`
(Scalar at `/scalar`) stays the contract of what is actually served. Any change
to this contract is announced on TS-597.

Greenfield: production data is wiped and re-bootstrapped (TS-602), so there is
no migration from TerraBase's current organizations, members, invitations or
`tb_…` keys.

## 1. Tenants and issuers

A **tenant** is an isolated identity namespace: its own users, credentials,
sessions, signing keys, OAuth clients and tokens. Each tenant is one OpenID
Connect issuer.

| Tenant | Who signs in | Issuer |
| --- | --- | --- |
| `terrascale` | TerraScale customers: people using the dashboard, the CLI and the MCP server | `https://auth.terrascale.tech/terrascale` |
| `{project id}` | The end users of one customer project | `https://auth.terrascale.tech/{project id}` |

- **Origin.** The issuer is `https://auth.terrascale.tech/{tenant}`: one
  global origin, served through regional ingress. The issuer never
  comes from a request's `Host` header.
- **Project tenants.** Creating a project creates its tenant, whose slug is the
  project id (a canonical uppercase ULID). Deleting the project deletes the
  tenant: its routes answer `404`, and ts-auth refuses its tokens. The slug
  `terrascale` is reserved; project ids can never equal it.
- **One base path per tenant.** Discovery is
  `{issuer}/.well-known/openid-configuration`, the JWKS
  `{issuer}/.well-known/jwks.json`, and the OAuth endpoints
  `{issuer}/oauth/{authorize,token,userinfo,revoke,logout}`. Hosted pages are
  `/ui/{tenant}/…` and management APIs `/v1/{tenant}/…`.
- **Isolation.**
  - Every record is stored in its tenant's address space and carries its tenant.
    Reads recheck it.
  - Every route resolves the tenant from its path, and an unknown or deleted
    tenant answers `404`. Nothing falls back to another tenant.
  - Each tenant has its own key ring, so its `kid`s are its own and a token
    validates only against its issuer's JWKS.
  - A user, session, code, client or rate-limit bucket of one tenant does not
    exist in another. The same email in two tenants is two unrelated accounts.
  - TerraBase trusts only the `terrascale` issuer. A project tenant's tokens are
    for the customer's own applications and reach no TerraScale API.
- **Organizations, projects, memberships, invitations and API keys exist only
  in the `terrascale` tenant.** A project tenant's own surface (its OAuth
  clients, end users, sign-in and branding) is specified in a later revision of
  this contract.

## 2. Resources

The platform management API is under `https://auth.terrascale.tech/v1/terrascale`.

```text
organization ─┬─ members            (org roles)
              ├─ invitations        (to the organization or to one project)
              ├─ ownership transfer
              ├─ API keys           (organization keys)
              ├─ audit events
              └─ projects ─┬─ members   (project roles)
                           └─ API keys  (project keys)
```

| Resource | Shape (JSON, snake_case) |
| --- | --- |
| Organization | `{id, name, created_by, created_at, role}`; `role` is the caller's organization role, `null` for a project-only collaborator |
| Member | `{user_id, email, role, joined_at}` |
| Project | `{id, organization_id, name, issuer, created_by, created_at, role}`; `issuer` is the project tenant's, `{origin}/{id}`; `role` is the caller's effective project role |
| Project member | `{user_id, email, role, granted_at}` |
| Invitation | `{id, email, role, project_id, invited_by, created_at, expires_at, status}` |
| Ownership transfer | `{from_user_id, to_user_id, created_at, expires_at}` |
| API key | `{id, name, organization_id, project_id, database, scopes, created_by, created_at, expires_at, previous_secret_expires_at}` |
| Audit event | `{id, actor, action, target, detail, occurred_at}` |

- Entity ids in APIs and JWT `sub`, `org_id` and `project_id` are plain canonical
  uppercase 26-character Crockford ULIDs, without prefixes.
- Names are 1 to 128 characters after trimming, contain a letter or digit and no
  control characters. Project names are unique within their organization,
  ignoring case (`409 name_taken`). Organization names are not unique.
- Timestamps are RFC 3339 UTC strings.
- Lists take `?limit=1..100` (default 20) and `?after=<cursor>` and answer
  `{"items":[…],"next":null|"<cursor>"}`. Pass `next` back as `after`.
- ts-auth stores no plan, subscription, database or usage. Those belong to
  ts-dashboard and TerraBase (section 9).

## 3. Roles and permissions

Organization roles, from least to most privileged: `viewer`, `member`,
`admin`, `owner`. Project roles: `viewer`, `member`, `admin`.

- An organization role implies a role on **every** project of the organization:
  `viewer` → `viewer`, `member` → `member`, `admin` and `owner` → `admin`.
- A **project member** has an explicit role on one project. Organization members
  can be granted a higher role on one project, and people outside the
  organization can be invited to one project only (a *collaborator*, as GitHub's
  outside collaborators). A collaborator reads the organization (its `role` is
  `null`) and only the projects it was granted. Once its last project role is
  removed, or its last project deleted, it is no longer in the organization.
- The **effective project role** is the higher of the implied and the explicit
  role. Explicit roles only add access; nothing subtracts.
- An organization can have several owners. The last owner cannot be demoted,
  removed or leave (`409 last_owner`).

| Operation | Needs |
| --- | --- |
| Read an organization | organization member or collaborator |
| List organization members, read the ownership transfer | organization `viewer` |
| Rename the organization | organization `admin` |
| Delete the organization | organization `owner`; `409 not_empty` while it has projects |
| Change a member's role, remove a member | organization `admin`; `owner` when the member is or becomes an owner |
| Leave | any organization member or collaborator |
| Delete your own account (`DELETE /v1/terrascale/account`) | signed in; `204` when deleted, `202` while a project closure is pending (it finishes in the background); `409 last_owner` while the last owner of an organization with other members, `409 not_empty`, `closure_conflict` or `binding_conflict` when TerraBase refuses one of its projects (see [project closure](project-closure.md)) |
| Invite to the organization | organization `admin`; `owner` to invite an owner |
| Invite to a project | project `admin` |
| List and revoke invitations | organization `admin`; project `admin` for its project's invitations |
| Start an ownership transfer | organization `owner`; the nominee confirms; the owner or nominee cancels |
| Create a project | organization `member`; the creator becomes the project's explicit `admin` |
| List projects | every project for organization members; granted projects for collaborators |
| Read a project, list its members | project `viewer` |
| Rename or delete a project | project `admin`; delete answers `204` when removed, `202` while TerraBase's closure is pending, `409 not_empty`, `closure_conflict` or `binding_conflict` when TerraBase refuses ([project closure](project-closure.md)) |
| Grant, change or remove a project member | project `admin`; a new grant needs an organization member (`404` otherwise: invite anyone else) |
| Project API keys | project `member` |
| Organization API keys | organization `admin` |
| Audit events | organization `admin` |

**Scopes.** ts-auth turns roles into scopes. This is the only place roles are
interpreted; TerraBase and the platform API check scopes, never roles.

| Scope | Grants |
| --- | --- |
| `databases:read` | TerraBase: read databases, schema, indexes, backups, backup policies and saved queries; read-only SQL |
| `databases:write` | TerraBase: create, branch and delete databases; backups, restores and policies; indexes; saved queries |
| `documents:read` | TerraBase: read collections and documents; queries |
| `documents:write` | TerraBase: write collections and documents; transactions |
| `usage:read` | TerraBase usage, and the platform API's usage views |
| `billing:read` | Platform API (ts-dashboard): read the organization's plan, subscription, invoices and spending caps |
| `billing:write` | Platform API: change the plan, payment method and spending caps |

| Token | Role | Scopes |
| --- | --- | --- |
| Project token | project `viewer` | `databases:read documents:read usage:read` |
| Project token | project `member`, `admin` | the viewer's and `databases:write documents:write` |
| Organization token | organization `viewer` | the project viewer's and `billing:read` |
| Organization token | organization `member`, `admin` | the project member's and `billing:read` |
| Organization token | organization `owner` | the project member's, `billing:read` and `billing:write` |

Project tokens never carry billing scopes.

## 4. Authentication

| Caller | Credential | Reaches |
| --- | --- | --- |
| A signed-in platform user (dashboard, CLI, MCP) | Sign-in access token of the `terrascale` tenant (`Authorization: Bearer`) | The management API, as that user |
| Code acting in an organization or project | Organization or project token (section 6) | TerraBase |
| A service (`TS_AUTH_SERVICE_CLIENTS`) | Client-credentials token, `sub` = client id, no organization | Whatever the receiving service grants that client id by its own configuration |

The management API accepts only sign-in access tokens of the `terrascale`
tenant: `typ` `at+jwt`, ES256 from the tenant's key ring, `iss` equal to the
issuer, `aud` including the issuer, unexpired. Organization, project and
service tokens are refused there (`401`). Every request re-reads the user and
its sign-in, so a deleted user or a revoked sign-in (RFC 7009 revocation or
refresh-token reuse) is refused at once. Sign-in access tokens carry the user's
authentication epoch (`ts_auth_epoch`), so after sign-out everywhere or a
password reset the management API refuses every token issued before. TS-707
also refuses primary-only sign-in tokens for users with active MFA; factor
state is read and validated on each request. Corrupt or unavailable state
fails closed. Public MFA enrollment and sign-in remain later slices.

Authorization fails closed. A caller who is neither member nor collaborator of
an organization gets `404` for it and everything under it, exactly as for an id
that does not exist. A member whose role is too low gets `403 forbidden_role`.

Errors use the `{"error":{"code","message"}}` envelope: `400 invalid_request`,
`401 unauthorized`, `403 forbidden_role` or `email_mismatch`, `404 not_found`,
`409` `name_taken`, `not_empty`, `last_owner`, `already_member`,
`invitation_pending`, `invitation_expired`, `not_pending`, `rotation_pending`,
`closure_conflict` or `binding_conflict`, and
`503 temporarily_unavailable`. Messages never echo input.

## 5. API keys

An API key is an OAuth 2.0 client credential owned by an organization or a
project. It is not a bearer token: code exchanges it at the token endpoint
(section 6) for a short-lived token.

- **Format.** `<ULID>_<52 chars>`, 79 characters. The first 26 characters
  are the canonical uppercase ULID client id; the secret uses lowercase Crockford
  base32 (`0-9a-z` without `i`, `l`, `o`, `u`) and holds 260 random bits.
  The client secret is the **whole** key string.
- **Shown once.** Creation and rotation answer the key string once (`key`).
  ts-auth stores only its SHA-256 and compares in constant time.
- **Scope.** A project key reaches only its project, and only one database if it
  has `database` (a TerraBase database ULID, unchanged by a rename). An organization key reaches every
  project of its organization, one project per token. `scopes` is a non-empty
  set of section 3's scopes for the key's kind (a project key: the project
  token scopes; an organization key: also `billing:read` and `billing:write`),
  within the creator's own scopes there: only an owner creates an organization
  key with `billing:write`. A project token of an organization key carries no
  billing scope.
- **Expiry.** `expires_at` is optional; an expired key is refused like a
  revoked one.
- **Revocation.** `DELETE` revokes at once: no new token is issued. Tokens
  already issued stay valid until they expire (at most 60 seconds). Nothing
  else disables a key: it does not depend on the sign-in that created it, so
  signing out, signing out everywhere or a password reset leaves it working.
- **Last use.** `last_used_at` is the key's last successful token exchange,
  recorded at most once per 5 minutes; null until the first exchange.
- **Rotation.** `POST …/rotate` issues a new secret for the same `id`. The
  previous secret keeps working for `previous_secret_ttl` seconds (0 to 86400,
  default 0), then only the new one does. While that grace is active, another
  rotation answers `409 rotation_pending`, including a request with no grace,
  so concurrent rotations cannot shorten the overlap. Revocation still stops
  both secrets immediately.
- **Ownership.** Keys belong to their organization or project, not to their
  creator, so they survive the creator leaving. Deleting the project or
  organization revokes them.

## 6. Tokens

All tokens of the `terrascale` tenant are ES256 JWTs signed by its key ring
(`{issuer}/.well-known/jwks.json`) with `typ` `at+jwt` (RFC 9068).
Every token endpoint request is `POST {issuer}/oauth/token`,
`application/x-www-form-urlencoded`, client authentication with HTTP Basic
(`client_secret_basic`) or `client_id` and `client_secret` in the body
(`client_secret_post`).

### 6.1 API key → token (client credentials)

```http
POST /terrascale/oauth/token
Authorization: Basic base64(01M4EQ2FRJ8S8B29E8TQVWNW4K:01M4EQ2FRJ8S8B29E8TQVWNW4K_<secret>)
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&project=01M4EQ2FRJ8S8B29E8TQVWNW41&scope=documents:read%20documents:write
```

| Parameter | Meaning |
| --- | --- |
| `project` | Organization keys: the project the token acts in; omit it for an organization token. Project keys: optional, must equal the key's project |
| `scope` | Optional subset of the key's scopes; default all of them |

Answer: `{"access_token":"…","token_type":"Bearer","expires_in":900,"scope":"documents:read documents:write"}`.
No refresh or identity token.

### 6.2 User token → organization or project token (token exchange)

A signed-in user's client (ts-dashboard, ts-cli) exchanges the user's sign-in
access token for a token that acts in one organization or project (RFC 8693).
Exchanging consumes nothing, so concurrent exchanges are safe; refresh tokens
are not involved.

```http
POST /terrascale/oauth/token
Authorization: Basic base64(01M4EQ2FRJ8S8B29E8TQVWNW4J:<secret>)
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:token-exchange
&subject_token=<sign-in access token>
&subject_token_type=urn:ietf:params:oauth:token-type:access_token
&organization=01M4EQ2FRJ8S8B29E8TQVWNW42
&project=01M4EQ2FRJ8S8B29E8TQVWNW41
```

| Parameter | Meaning |
| --- | --- |
| `subject_token` | A sign-in access token of this tenant, issued to the same client |
| `organization` | Required |
| `project` | Optional; omit it for an organization token |
| `scope` | Optional subset of what the user's role allows; default all of it |

Answer: `{"access_token":"…","issued_token_type":"urn:ietf:params:oauth:token-type:access_token","token_type":"Bearer","expires_in":60,"scope":"…"}`.
The token expires after 60 seconds or with the subject token, whichever is
first. Membership and roles are read at the exchange, so a role change applies
to the next exchange. TS-707 refuses primary-only subject tokens for active
MFA accounts with `invalid_grant`; corrupt or unavailable factor state
answers `503 temporarily_unavailable`. Machine client credentials and API
keys remain independent of user MFA.
Exchange preserves the sign-in token's original numeric `auth_time`. It
requires a nonnegative integer no more than 30 seconds ahead of the
exchanging replica; missing, malformed, negative or further-ahead values
answer `invalid_grant`. Submitted values cannot repair or override it.

### 6.3 Claims

| Claim | Sign-in token | User org/project token | API key token |
| --- | --- | --- | --- |
| `iss` | issuer | issuer | issuer |
| `sub` | user id | user id | key ULID |
| `client_id` | the client | the exchanging client | key id |
| `aud` | issuer, `https://api.terrascale.tech` and every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value | `https://api.terrascale.tech` and every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value | `https://api.terrascale.tech` and every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value |
| `exp`, `iat`, `jti` | 5 minutes | at most 60 seconds | 60 seconds |
| `scope` | OpenID scopes (`openid email profile offline_access`) | section 3 scopes | section 3 scopes |
| `email`, `email_verified` | yes | yes | no |
| `auth_time` | server-issued Unix seconds at authentication | unchanged from the sign-in token | no |
| `ts_auth_epoch` | the user's authentication epoch | no | no |
| `org_id` | no | the organization | the key's organization |
| `project_id` | no | the project, for a project token | the project, for a project token |
| `database` | no | no | the key's database, when it has one |
| `platform_admin` | `true` for a platform administrator (section 8.2), otherwise absent | no | no |

`aud` on gru-1 is `https://gru.api.terrascale.tech`,
`https://platform.terrascale.tech` and `https://mcp.terrascale.tech` (plus the
issuer on sign-in tokens): every resource server checks its own.

Service client tokens are unchanged: `sub` = `client_id` = the service's id,
configured audiences, no `scope`, `org_id`, `project_id` or `auth_time`.
User sign-in access and identity tokens retain the original `auth_time` when
refreshed; submitted values cannot set or renew it. Organization/project
exchange preserves the original value, including an accepted clock
difference; it never replaces it with the receiving replica's time. Refresh
and exchange never renew authentication or prove MFA completion. The
issuance-before-enforcement deployment gate is documented in `docs/auth/mfa.md`.

### 6.4 Rules for TerraBase and other resource servers

1. Validate `typ` `at+jwt` (or `application/at+jwt`), ES256 by a `kid` from the
   issuer's JWKS, `iss` equal to `https://auth.terrascale.tech/terrascale`
   exactly, `aud` including your audience, and `exp`/`nbf` (decision 0007's
   rules stay).
2. A route under `/v1/organizations/{organization}` needs `org_id` equal to
   `{organization}`; anything else answers `404`.
3. A route under `…/projects/{project}` needs `project_id` equal to `{project}`.
   An organization token (no `project_id`) does **not** reach project routes:
   ts-auth attests that a project belongs to its organization only in a project
   token.
4. With a `database` claim, only that database (by name) is reachable; others
   answer `404`.
5. A missing scope answers `403`.
6. A token without `org_id` (sign-in and service tokens) reaches no tenant
   route. Record `sub` as the actor.

TerraBase stores no memberships and no API keys; databases reference the
project id of the token that created them.

### 6.5 Errors

OAuth errors (RFC 6749 §5.2, `{"error","error_description"}`):

| Status | `error` | When |
| --- | --- | --- |
| 400 | `invalid_request` | Malformed or missing parameter |
| 400 | `unsupported_grant_type`, `unauthorized_client` | The grant is not allowed for this client |
| 400 | `invalid_grant` | The subject token is invalid, expired, revoked, from another client or no longer names a user |
| 400 | `invalid_scope` | A requested scope exceeds the key or role |
| 400 | `invalid_target` | Unknown organization or project, a project outside the key's organization, or one the user cannot reach; the same answer for each |
| 401 | `invalid_client` | Unknown, wrong, revoked or expired key, alike |
| 429 | `rate_limited` | With `Retry-After` |

### 6.6 Revocation windows

Access tokens are validated offline, so these take effect when outstanding
tokens expire:

| Change | ts-auth | TerraBase |
| --- | --- | --- |
| API key revoked, expired or rotated past its grace | at once | within 60 seconds |
| Member removed, role lowered, project or organization deleted | at once (the management API and every exchange read it) | within 60 seconds |
| Sign-in revoked (RFC 7009, refresh-token reuse), user deleted | at once | within 5 minutes of the last exchange |
| Signed out everywhere, password reset | at once (refresh, the management API and the exchange) | within 5 minutes of the last exchange |

### 6.7 Limits

- Token requests per API key: 1,000 per minute (`429 rate_limited`).
- SDKs cache a token until 60 seconds before `expires_in` and exchange again
  after a `401` from the resource server, once.

## 7. Invitations, ownership transfer and audit

- **Invitations** go to an email address, for an organization role or (with
  `project_id`) a project role. They expire after 7 days and work once.
  ts-auth emails the invitee a link to the dashboard's accept page,
  `<dashboard origin>/invitations/<token>`, where the origin is that of the
  first `TS_AUTH_DASHBOARD_REDIRECT_URIS` entry. The create call also answers the token once,
  for callers that deliver it themselves. The token is `tsi_` and 52 base32
  characters; only its SHA-256 is stored.
- **Accepting and declining** need a sign-in token whose `email` is verified and
  equals the invitation's, ignoring case; the body never supplies the email.
  Accepting an organization invitation makes the user a member with its role
  (`409 already_member` if it is one). Accepting a project invitation grants the
  project role (`409 already_member` if the user's effective project role is
  already at least that); the user joins the organization only as a
  collaborator.
  `lookup` answers a pending invitation's organization, project, role and expiry
  to any signed-in user holding the token.
- One pending invitation per email and target (`409 invitation_pending`). An
  expired one can be replaced.
- **Ownership transfer**: one pending transfer per organization, valid 7 days,
  to an organization member who is not an owner. Only the nominee confirms; the
  nominee becomes an owner and the owner who started it an `admin`. The offer
  lapses when its starter stops being an owner, its nominee becomes one, or
  either side leaves, and stays lapsed if that is undone.
- **Audit events** record every change of this API, newest first, for at
  least 90 days. `actor` is a user id (or, for a suspension, the service
  client's id), `target` a user id, email, project id or key id, `detail` the
  new role, name, scopes or suspension reason. Actions:
  `organization.created`, `organization.renamed`, `member.role_changed`,
  `member.removed`, `member.left`, `member.joined`, `invitation.created`,
  `invitation.revoked`, `invitation.accepted`, `invitation.declined`,
  `ownership.transfer_started`, `ownership.transfer_confirmed`,
  `ownership.transfer_cancelled`, `project.created`, `project.renamed`,
  `project.closure_requested`, `project.deleted`, `project_member.role_set`, `project_member.removed`,
  `api_key.created`, `api_key.rotated`, `api_key.revoked`,
  `project.suspended`, `project.unsuspended`. Deleting an
  organization deletes its events.

## 8. Platform operations

### 8.1 Project suspension

ts-dashboard suspends a project, for example when it reaches a spending cap,
without a signed-in user.

| Method | Route | Answer |
| --- | --- | --- |
| `PUT` | `/v1/terrascale/organizations/{organization}/projects/{project}/suspension` | `{"reason":"…"}` (1 to 256 characters) → `200 {"project_id","reason","suspended_at"}`; suspending again keeps the first `suspended_at` |
| `DELETE` | same | `204`; `404` when it is not suspended |
| `GET` | same | `200`, or `404` when it is not suspended |

- `PUT` and `DELETE` authenticate a confidential service client with HTTP
  Basic: one registered in `TS_AUTH_SERVICE_CLIENTS` whose id is also listed in
  `TS_AUTH_PLATFORM_CLIENTS`. `GET` accepts that client too, or the sign-in
  token of anyone with a role on the project.
- While a project is suspended, the client-credentials grant (project keys, and
  organization keys asking for it) and the token exchange for it answer
  `invalid_target`. Organization tokens and the management API keep working, so
  the organization can still pay and manage the project. Tokens already issued
  lapse within 60 seconds.
- Suspensions are audited (`project.suspended`, `project.unsuspended`) with the
  client id as the actor.

### 8.2 Platform administrators

- A user of the `terrascale` tenant can be a platform administrator. Its
  sign-in access tokens and `userinfo` carry `"platform_admin": true`;
  organization, project and API key tokens never do.
- Only an operator sets and clears it, through the authorized administrative channel on a ts-auth host;
  there is no HTTP route and no operator key. The TS-602 bootstrap makes the
  first user an administrator this way.
- The flag shows in the next token the user is issued: a refresh or a new
  sign-in. ts-dashboard decides what an administrator may do in the platform.

## 9. What ts-auth does not do

- **TerraBase** owns databases, collections, documents, backups, indexes,
  schema and usage. ts-auth never calls TerraBase for users. Deleting a project
  does not delete its databases: the platform deletes them first. ts-auth removes
  a project (or an account's sole-owner projects) only after TerraBase accepted
  its permanent closure fence in every region, through ts-auth's own peer
  transport ([project closure](../terrabase/project-closure.md)); until then the
  request answers 202 and finishes in the background, and while TerraBase holds
  the project's databases it answers 409 `not_empty`. After the project is gone,
  no token reaches them.
- **ts-dashboard** owns plans, subscriptions, billing and spending caps, and UI
  preferences such as a user's current organization. It reads identity through
  this API and suspends a project through section 8.1.
- **ts-auth's own storage.** ts-auth reaches TerraBase at
  `http://gru.ts-terrabase.internal:8081` for everything but discovery and the JWKS (those at its own region's), TerraBase's internal
  listener on the private service network. Storage requests carry the shared
  `TERRABASE_PEER_SECRET` supplied through OCI Vault on each app; see
  [peer authentication](../terrabase/peer-authentication.md) for rollout and enforcement.
  The separate storage-signing-key design is retired. Signing keys live in
  TerraBase, sealed under `TS_AUTH_MASTER_KEY`, also supplied through OCI Vault.

## 10. Clients

| Client | Type | Grants |
| --- | --- | --- |
| `ts-dashboard` | confidential | authorization code + S256 PKCE, refresh token, token exchange |
| `ts-cli` (client id `00000000000000000000000007`) | public | device code (RFC 8628), refresh token, token exchange |
| service clients (`TS_AUTH_SERVICE_CLIENTS`) | confidential | client credentials |
| API keys (ULID clients) | confidential | client credentials |

**Device flow for ts-cli.** Users sign in and approve on ts-auth's own hosted
page (HA1, 2026-10-22), like every other TerraScale sign-in:

1. ts-cli posts `client_id=00000000000000000000000007` (the `ts-cli` client;
   client ids are TerraScale ids) and optionally `scope`, such as
   `openid email offline_access`, to `POST /terrascale/oauth/device`. The
   answer is RFC 8628's `{device_code, user_code, verification_uri,
   verification_uri_complete, expires_in, interval}`; `verification_uri` is
   `{issuer origin}/ui/terrascale/device` and `verification_uri_complete` adds
   `?user_code=`. Codes live 10 minutes; `interval` is 5 seconds.
2. The page asks a browser without a session to sign in first and returns with
   the code. It shows the code, the client and the signed-in account; opening
   it never approves. The user approves or denies with an explicit,
   origin-checked form post. Each code lookup charges the IP sign-in budget and
   20 per user per 15 minutes. Active MFA users are refused, as at every user
   grant.
3. ts-cli polls `POST /terrascale/oauth/token` with
   `grant_type=urn:ietf:params:oauth:grant-type:device_code`, `device_code` and
   `client_id`, at `interval` seconds, and handles `authorization_pending`,
   `slow_down` (a poll sooner than `interval` after the previous one),
   `access_denied` and `expired_token` as RFC 8628 says. A device code is
   redeemed once. Approved,
   it gets a sign-in access token and a refresh token, and exchanges the access
   token for organization and project tokens (section 6.2).

## 11. Slices

Each slice is one pull request with its integration tests, and updates
[`parity.md`](parity.md) and the served OpenAPI.

1. This contract and the ownership boundary in `AGENTS.md`.
2. Management API authentication; organizations, members, projects and
   project members.
3. API keys, the client-credentials exchange and the token exchange.
4. Invitations, ownership transfer and audit events.
5. The `usage:read` and `billing:*` scopes, project suspension and platform
   administrators.
6. The ts-cli device flow.
7. Project tenants: per-project issuers, discovery, JWKS and key rings (served).
8. ts-auth's own storage token (section 9), once TS-589 and TS-591 support it.
9. The project tenant surface (its OAuth clients, end users, sign-in), in a
   later revision of this contract.

OAuth for MCP clients (RFC 8707 resource indicators, RFC 7591 or client ID
metadata documents) is a follow-up outside these slices.
