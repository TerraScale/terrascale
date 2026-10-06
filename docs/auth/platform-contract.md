# Platform identity contract (TS-598)

ts-auth is the single owner of identity and access for TerraScale (epic TS-597,
umbrella `AGENTS.md`, "Service ownership"). This document is the contract that
TerraBase (TS-589), the SDKs (ts-go TS-590, ts-dotnet TS-591, ts-js TS-592,
ts-cli TS-600) and ts-dashboard (TS-599) build on. The machine-readable
contract is [`platform-api.openapi.yaml`](platform-api.openapi.yaml)
(`make openapi-check` lints it).

Status: contract v1, 2026-10-06. Nothing here is served until its slice lands;
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
| `terrascale` | TerraScale customers: people using the dashboard, the CLI and the MCP server | `https://gru.auth.terrascale.tech/terrascale` |
| `{project id}` | The end users of one customer project | `https://gru.auth.terrascale.tech/{project id}` |

- **Origin.** The issuer is `{TS_AUTH_PUBLIC_ORIGIN}/{tenant}`, and the origin
  is regional: `https://{region}.auth.terrascale.tech`, so
  `https://gru.auth.terrascale.tech` in gru-1. The global
  `https://auth.terrascale.tech` is a ts-atlas proxy; its discovery documents
  name the regional issuer, so configure relying parties with the regional one.
  The issuer never comes from a request's `Host` header.
- **Project tenants.** Creating a project creates its tenant, whose slug is the
  project id (a lowercase hyphenated UUID). Deleting the project deletes the
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

The platform management API is under `https://gru.auth.terrascale.tech/v1/terrascale`.

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
| Project | `{id, organization_id, name, issuer, created_by, created_at, role}`; `issuer` is the project tenant; `role` is the caller's effective project role |
| Project member | `{user_id, email, role, granted_at}` |
| Invitation | `{id, email, role, project_id, invited_by, created_at, expires_at, status}` |
| Ownership transfer | `{from_user_id, to_user_id, created_at, expires_at}` |
| API key | `{id, name, organization_id, project_id, database, scopes, created_by, created_at, expires_at, previous_secret_expires_at}` |
| Audit event | `{id, actor, action, target, detail, occurred_at}` |

- Ids are lowercase hyphenated UUIDv7, except user ids (the `sub` of the
  user's tokens, a lowercase UUID) and API key ids (`tsk_…`, section 5).
- Names are 1 to 128 characters after trimming, contain a letter or digit and no
  control characters. Project names are unique within their organization,
  ignoring case (`409 name_taken`). Organization names are not unique.
- Timestamps are RFC 3339 UTC strings.
- Lists take `?limit=1..100` (default 20) and `?after=<cursor>` and answer
  `{"items":[…],"next":null|"<cursor>"}`. Pass `next` back as `after`.
- ts-auth stores no plan, subscription, database or usage. Those belong to
  ts-dashboard and TerraBase (section 8).

## 3. Roles and permissions

Organization roles, from least to most privileged: `viewer`, `member`,
`admin`, `owner`. Project roles: `viewer`, `member`, `admin`.

- An organization role implies a role on **every** project of the organization:
  `viewer` → `viewer`, `member` → `member`, `admin` and `owner` → `admin`.
- A **project member** has an explicit role on one project. Organization members
  can be granted a higher role on one project, and people outside the
  organization can be invited to one project only (a *collaborator*, as GitHub's
  outside collaborators). A collaborator reads the organization (its `role` is
  `null`) and only the projects it was granted.
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
| Invite to the organization | organization `admin`; `owner` to invite an owner |
| Invite to a project | project `admin` |
| List and revoke invitations | organization `admin`; project `admin` for its project's invitations |
| Start an ownership transfer | organization `owner`; the nominee confirms; the owner or nominee cancels |
| Create a project | organization `member`; the creator becomes the project's explicit `admin` |
| List projects | every project for organization members; granted projects for collaborators |
| Read a project, list its members | project `viewer` |
| Rename or delete a project | project `admin` |
| Grant, change or remove a project member | project `admin`; a new grant needs an organization member (`404` otherwise: invite anyone else) |
| Project API keys | project `member` |
| Organization API keys | organization `admin` |
| Audit events | organization `admin` |

**Scopes.** ts-auth turns roles into TerraBase scopes. This is the only place
roles are interpreted; TerraBase checks scopes, never roles.

| Scope | TerraBase grants |
| --- | --- |
| `databases:read` | Read databases, schema, indexes, backups, backup policies, usage and saved queries; read-only SQL |
| `databases:write` | Create, branch and delete databases; backups, restores and policies; indexes; saved queries |
| `documents:read` | Read collections and documents; queries |
| `documents:write` | Write collections and documents; transactions |
| `shapes:read` | Live shapes |

| Role | Scopes |
| --- | --- |
| `viewer` | `databases:read documents:read shapes:read` |
| `member`, `admin`, `owner` | all five |

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
refresh-token reuse) is refused at once. After sign-out everywhere or a password
reset, refresh is refused at once and access tokens already issued live out
their hour, as today (TS-491).

Authorization fails closed. A caller who is neither member nor collaborator of
an organization gets `404` for it and everything under it, exactly as for an id
that does not exist. A member whose role is too low gets `403 forbidden_role`.

Errors use the `{"error":{"code","message"}}` envelope: `400 invalid_request`,
`401 unauthorized`, `403 forbidden_role` or `email_mismatch`, `404 not_found`,
`409` `name_taken`, `not_empty`, `last_owner`, `already_member`,
`invitation_pending`, `invitation_expired` or `not_pending`, and
`503 temporarily_unavailable`. Messages never echo input.

## 5. API keys

An API key is an OAuth 2.0 client credential owned by an organization or a
project. It is not a bearer token: code exchanges it at the token endpoint
(section 6) for a short-lived token.

- **Format.** `tsk_<20 chars>_<52 chars>`, 77 characters of lowercase
  Crockford base32 (`0-9a-z` without `i`, `l`, `o`, `u`) and `_`. The first 24
  characters, `tsk_<20 chars>`, are the key's `id`: its OAuth `client_id`, the
  `sub` of its tokens and its id in URLs. The secret part holds 260 random
  bits. The client secret is the **whole** key string.
- **Shown once.** Creation and rotation answer the key string once (`key`).
  ts-auth stores only its SHA-256 and compares in constant time.
- **Scope.** A project key reaches only its project, and only one database if it
  has `database` (a TerraBase database name). An organization key reaches every
  project of its organization, one project per token. `scopes` is a non-empty
  subset of section 3's scopes; a project key's scopes must be within the
  creator's effective scopes.
- **Expiry.** `expires_at` is optional; an expired key is refused like a
  revoked one.
- **Revocation.** `DELETE` revokes at once: no new token is issued. Tokens
  already issued stay valid until they expire (at most 15 minutes).
- **Rotation.** `POST …/rotate` issues a new secret for the same `id`. The
  previous secret keeps working for `previous_secret_ttl` seconds (0 to 86400,
  default 0), then only the new one does.
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
Authorization: Basic base64(tsk_3k9f2m8q1x7v4c6n0b5z:tsk_3k9f2m8q1x7v4c6n0b5z_<secret>)
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&project=0199b3e4-5b6a-7c8d-9e0f-1a2b3c4d5e6f&scope=documents:read%20documents:write
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
Authorization: Basic base64(ts-dashboard:<secret>)
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:token-exchange
&subject_token=<sign-in access token>
&subject_token_type=urn:ietf:params:oauth:token-type:access_token
&organization=0199b3e4-0000-7000-8000-000000000001
&project=0199b3e4-5b6a-7c8d-9e0f-1a2b3c4d5e6f
```

| Parameter | Meaning |
| --- | --- |
| `subject_token` | A sign-in access token of this tenant, issued to the same client |
| `organization` | Required |
| `project` | Optional; omit it for an organization token |
| `scope` | Optional subset of what the user's role allows; default all of it |

Answer: `{"access_token":"…","issued_token_type":"urn:ietf:params:oauth:token-type:access_token","token_type":"Bearer","expires_in":900,"scope":"…"}`.
The token expires after 15 minutes or with the subject token, whichever is
first. Membership and roles are read at the exchange, so a role change applies
to the next exchange.

### 6.3 Claims

| Claim | Sign-in token | User org/project token | API key token |
| --- | --- | --- | --- |
| `iss` | issuer | issuer | issuer |
| `sub` | user id | user id | key id `tsk_…` |
| `client_id` | the client | the exchanging client | key id |
| `aud` | issuer and every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value | every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value | every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value |
| `exp`, `iat`, `jti` | 1 hour | at most 15 minutes | 15 minutes |
| `scope` | OpenID scopes (`openid email profile offline_access`) | section 3 scopes | section 3 scopes |
| `email`, `email_verified` | yes | yes | no |
| `org_id` | no | the organization | the key's organization |
| `project_id` | no | the project, for a project token | the project, for a project token |
| `database` | no | no | the key's database, when it has one |

Service client tokens are unchanged: `sub` = `client_id` = the service's id,
configured audiences, no `scope`, `org_id` or `project_id`.

### 6.4 Rules for TerraBase and other resource servers

1. Validate `typ` `at+jwt` (or `application/at+jwt`), ES256 by a `kid` from the
   issuer's JWKS, `iss` equal to `https://gru.auth.terrascale.tech/terrascale`
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
| API key revoked, expired or rotated past its grace | at once | within 15 minutes |
| Member removed, role lowered, project or organization deleted | at once (the management API and every exchange read it) | within 15 minutes |
| Sign-in revoked (RFC 7009, refresh-token reuse), user deleted | at once | within 15 minutes of the last exchange |
| Signed out everywhere, password reset | refresh at once; access tokens within 1 hour | within 1 hour (the sign-in token can still be exchanged until it expires) |

### 6.7 Limits

- Token requests per API key: 1,000 per minute (`429 rate_limited`).
- SDKs cache a token until 60 seconds before `expires_in` and exchange again
  after a `401` from the resource server, once.

## 7. Invitations, ownership transfer and audit

- **Invitations** go to an email address, for an organization role or (with
  `project_id`) a project role. They expire after 7 days and work once.
  ts-auth emails the invitee a link to `TS_AUTH_INVITATION_URL?token=<token>`
  (the dashboard's accept page). The create call also answers the token once,
  for callers that deliver it themselves. The token is `tsi_` and 52 base32
  characters; only its SHA-256 is stored.
- **Accepting and declining** need a sign-in token whose `email` is verified and
  equals the invitation's, ignoring case; the body never supplies the email.
  Accepting an organization invitation makes the user a member with its role
  (`409 already_member` if it is one). Accepting a project invitation grants the
  project role; the user joins the organization only as a collaborator.
  `lookup` answers a pending invitation's organization, project, role and expiry
  to any signed-in user holding the token.
- One pending invitation per email and target (`409 invitation_pending`). An
  expired one can be replaced.
- **Ownership transfer**: one pending transfer per organization, valid 7 days,
  to an organization member who is not an owner. Only the nominee confirms; the
  nominee becomes an owner and the owner who started it an `admin`. The offer
  lapses when its starter stops being an owner or either side leaves.
- **Audit events** record every change of this API, newest first, for at
  least 90 days. `actor` is a user id, `target` a user id, email, project id or
  key id, `detail` the new role, name or scopes. Actions:
  `organization.created`, `organization.renamed`, `member.role_changed`,
  `member.removed`, `member.left`, `member.joined`, `invitation.created`,
  `invitation.revoked`, `invitation.accepted`, `invitation.declined`,
  `ownership.transfer_started`, `ownership.transfer_confirmed`,
  `ownership.transfer_cancelled`, `project.created`, `project.renamed`,
  `project.deleted`, `project_member.role_set`, `project_member.removed`,
  `api_key.created`, `api_key.rotated`, `api_key.revoked`. Deleting an
  organization deletes its events.

## 8. What ts-auth does not do

- **TerraBase** owns databases, collections, documents, backups, indexes,
  schema and usage. ts-auth never calls TerraBase for users. Deleting a project
  does not delete its databases: the platform deletes them first. After the
  project is gone, no token reaches them.
- **ts-dashboard** owns plans, subscriptions, billing and spending caps, and UI
  preferences such as a user's current organization. It reads identity through
  this API, and suspends a project by revoking access through it.
- ts-auth persists to TerraBase with a bootstrap `tb_…` key issued by
  TerraBase's admin listener, never with a token it signed itself: TerraBase
  validates tokens with ts-auth's JWKS, which ts-auth reads from TerraBase, so
  that would deadlock a cold start.

## 9. Clients

| Client | Type | Grants |
| --- | --- | --- |
| `ts-dashboard` | confidential | authorization code + S256 PKCE, refresh token, the headless grants (TS-582), token exchange |
| `ts-cli` | public (RFC 8252 loopback redirect) | authorization code + S256 PKCE, refresh token, token exchange |
| service clients (`TS_AUTH_SERVICE_CLIENTS`) | confidential | client credentials |
| API keys (`tsk_…`) | confidential | client credentials |

## 10. Slices

Each slice is one pull request with its integration tests, and updates
[`parity.md`](parity.md) and the served OpenAPI.

1. This contract and the ownership boundary in `AGENTS.md`.
2. Management API authentication; organizations, members, projects and
   project members.
3. API keys, the client-credentials exchange and the token exchange.
4. Invitations, ownership transfer and audit events.
5. Project tenants: per-project issuers, discovery, JWKS and key rings.
6. The project tenant surface (its OAuth clients, end users, sign-in), in a
   later revision of this contract.
