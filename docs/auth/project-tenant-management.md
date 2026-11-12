# Project tenant management

Each customer project is a ts-auth tenant (`platform-contract.md` section 1):
its own end users, sessions, OAuth clients and signing keys, under the issuer
`https://auth.terrascale.tech/{project id}`. This guide specifies what a
project tenant serves to its end users and the management API the project's
owners use (through ts-dashboard and the SDKs).

## 1. End-user surface

A project tenant serves, while its project exists:

| Route | What |
| --- | --- |
| `/{project}/.well-known/openid-configuration`, `/{project}/.well-known/jwks.json` | Discovery and the tenant's own JWKS |
| `/{project}/oauth/{authorize,token,userinfo,revoke,logout}` | Authorization code with S256 PKCE, refresh tokens (`offline_access`), userinfo, revocation and RP-initiated logout |
| `/ui/{project}/sign-in`, `/ui/{project}/sign-up`, password reset pages | Hosted pages, with the project's branding |

- The hosted session cookie is per tenant: `tsauth_session` for `terrascale`,
  `tsauth_session_{project}` for a project. Signing in to a project never
  touches the TerraScale session, and one tenant's cookie never resolves in
  another.
- A project tenant has no device flow, token exchange, API keys, organizations
  or platform claims. Its tokens carry `sub`, `email` (with the `email` scope),
  `iss` = the project issuer and `aud` = the client id; TerraBase does not
  accept them.
- A disabled end user cannot sign in, and its sessions and tokens stop working.

## 2. Management API

Base: `https://auth.terrascale.tech/v1/{project}`. JSON bodies use snake_case.

Customer backends can send `Authorization: Bearer <project secret API key>`.
The key must belong to this project and have no database restriction. GET requires `auth:read`, except admin-only GET routes require `auth:write`.
Changes require `auth:write` (write does not imply read).
Both the key and project IP allowlists apply. Organization keys and exchanged
API-key JWTs are refused. A revoked or expired secret key answers 401 on the
next request: management does not cache keys. Every mutation commits an organization
audit event named `<operation_id>.succeeded` in the same atomic save as its change,
with the key ULID as actor and the route as detail. Audit failure rolls back the
change. Failed attempts record `<operation_id>.failed` with the HTTP status.
Step-up operations answer 403 `person_required` to keys. Provider PUT is also
person-only because it replaces external sign-in credentials and can redirect
identity trust to a different OAuth application.
Only project admins can issue, rotate or change allowlists on keys with
`auth:write`; members can issue `auth:read` keys. Viewers cannot issue auth keys.
Authentication scopes are never included in TerraBase token-exchange grants.

The five SDKs use their existing credential pattern:

| SDK | Project management credential |
| --- | --- |
| JavaScript | `createTsAuthClient({apiBaseUrl, instanceSlug: "terrascale"}).projects.disableUser(secretKey, projectId, userId)` |
| Go | `client.WithToken(secretKey).DisableProjectUser(ctx, projectId, userId)` |
| Rust | `client.projects()?.disable_user(secret_key, project_id, user_id).await` |
| Python | `TerraScale(api_key=secret_key, project=project_id).disable_user(user_id)` (also `AsyncTerraScale`) |
| .NET | `client.Projects.DisableUserAsync(secretKey, projectId, userId)` |

Keep secret keys in the backend. Person credentials remain required for step-up.

**Authentication.** `Authorization: Bearer <the caller's own terrascale
sign-in access token>`, as for `/v1/terrascale`. ts-auth reads the caller's
effective project role (`platform-contract.md` section 3) on every request:

- `viewer` reads settings, users, sessions and OAuth clients;
- `admin` changes resources and reads admin-only routes;
- an unknown project, or one the caller cannot see, answers `404`; a role too
  low answers `403 forbidden`.

| Method and path | Role | Key scope | Response |
| --- | --- | --- | --- |
| `GET /settings` | viewer | `auth:read` | `Settings` |
| `PUT /settings` | admin | `auth:write` | `Settings`; body `{methods?, branding?, configuration?, attack_protection?}`, omitted parts unchanged |
| `GET /users?q=&limit=&after=` | viewer | `auth:read` | `{items: [User], next}`; `q` matches an email substring, `limit` 1 to 100 (default 50) |
| `GET /users/{user}` | viewer | `auth:read` | `User` |
| `POST /users/{user}/disable` | admin | `auth:write` | `User`; ends every session and token of the user |
| `POST /users/{user}/enable` | admin | `auth:write` | `User` |
| `DELETE /users/{user}` | admin | `auth:write` | `204` |
| `GET /sessions?user=&limit=&after=` | viewer | `auth:read` | `{items: [Session], next}`; live sessions only |
| `DELETE /sessions/{session}` | admin | `auth:write` | `204` |
| `GET /clients` | viewer | `auth:read` | `{items: [Client], next: null}` |
| `POST /clients` | admin | Person only (step-up) | `201 Client` with `client_secret` once for a confidential client |
| `DELETE /clients/{client_id}` | admin | `auth:write` | `204` |
| `GET /users/export?format=&limit=&after=` | admin | `auth:write` | Audited directory page (JSON or CSV) |
| `PUT /providers/{provider}` | admin | Person only (external sign-in credentials) | `Settings` |
| `DELETE /providers/{provider}` | admin | `auth:write` | `Settings` |
| `PUT /clients/{client_id}` | admin | Person only (step-up) | `Client` |
| `POST /clients/{client_id}/secret/rotate` | admin | Person only (step-up) | `ClientSecret` |
| `GET /webhook-endpoints` | admin | `auth:write` | `{items: [WebhookEndpoint], next: null}` without secrets |
| `POST /webhook-endpoints` | admin | Person only (step-up) | `201 WebhookEndpoint` with `secret` once; needs a `create_webhook_endpoint` step-up proof |
| `DELETE /webhook-endpoints/{id}` | admin | `auth:write` | `204` |
| `POST /webhook-endpoints/{id}/rotate-secret` | admin | Person only (step-up) | `WebhookSecret` with `secret` once; needs a `rotate_webhook_secret` step-up proof |

```text
Settings = {
  project_id, issuer, discovery_url, sign_in_url,
  methods: { passcode: bool, password: bool, sign_up: bool },
  branding: { name: string|null, logo_url: string|null, theme_color: string|null },
  counts: { users: int, clients: int }
}
User    = { id, email, created_at, disabled: bool }
Session = { id, user_id, email, created_at, expires_at }
Client  = { client_id, name, type: "public"|"confidential", redirect_uris: [string],
            created_at, client_secret?: string }
POST /clients body = { name, type, redirect_uris }
WebhookEndpoint = { id, url, event_types: [string], created_at, secret?: string }
WebhookSecret   = { id, secret, previous_secret_expires_at }
POST /webhook-endpoints body = { url, event_types }
```

Webhook endpoints are described in [webhooks.md](webhooks.md).

- `methods`: at least one of `passcode` and `password` stays on (`400
  invalid_request` otherwise). `sign_up: false` hides self sign-up. A new project
  has every method on.
- `branding.logo_url` is an `https` URL; `theme_color` is `#rrggbb`; `name` is 1
  to 64 characters. `null` clears a value; the hosted pages then show the
  project's name and the default theme.
- Redirect URIs are absolute `https` URLs, or `http` on `localhost` and loopback
  addresses, without fragments; 1 to 10 per client.
- Errors use the platform API's error body `{error: {code, message}}`.

Project security settings and the account-unlock contract are described in [attack protection](attack-protection.md).
