# Project tenant management

Each customer project is a ts-auth tenant (`platform-contract.md` section 1):
its own end users, sessions, OAuth clients and signing keys, under the issuer
`https://auth.terrascale.tech/{project id}`. This document specifies what a
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

**Authentication.** `Authorization: Bearer <the caller's own terrascale
sign-in access token>`, as for `/v1/terrascale`. ts-auth reads the caller's
effective project role (`platform-contract.md` section 3) on every request:

- `viewer` reads every resource;
- `admin` changes them;
- an unknown project, or one the caller cannot see, answers `404`; a role too
  low answers `403 forbidden`.

| Method and path | Role | Response |
| --- | --- | --- |
| `GET /settings` | viewer | `Settings` |
| `PUT /settings` | admin | `Settings`; body `{methods?, branding?}`, omitted parts unchanged |
| `GET /users?q=&limit=&after=` | viewer | `{items: [User], next}`; `q` matches an email substring, `limit` 1 to 100 (default 50) |
| `GET /users/{user}` | viewer | `User` |
| `POST /users/{user}/disable` | admin | `User`; ends every session and token of the user |
| `POST /users/{user}/enable` | admin | `User` |
| `DELETE /users/{user}` | admin | `204` |
| `GET /sessions?user=&limit=&after=` | viewer | `{items: [Session], next}`; live sessions only |
| `DELETE /sessions/{session}` | admin | `204` |
| `GET /clients` | viewer | `{items: [Client], next: null}` |
| `POST /clients` | admin | `201 Client` with `client_secret` once for a confidential client |
| `DELETE /clients/{client_id}` | admin | `204` |

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
```

- `methods`: at least one of `passcode` and `password` stays on (`400
  invalid_request` otherwise). `sign_up: false` hides self sign-up. A new project
  has every method on.
- `branding.logo_url` is an `https` URL; `theme_color` is `#rrggbb`; `name` is 1
  to 64 characters. `null` clears a value; the hosted pages then show the
  project's name and the default theme.
- Redirect URIs are absolute `https` URLs, or `http` on `localhost` and loopback
  addresses, without fragments; 1 to 10 per client.
- Errors use the platform API's error body `{error, error_description}`.
