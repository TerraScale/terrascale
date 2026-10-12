# Dashboard identity descriptor and delivery

This is the consumer contract the application already implements
(`src/TerraScale.Dashboard/Platform/Persistence/DashboardIdentity.cs`). It
lists field names, formats and owners only. It contains no credential values,
and none may be added here, to an issue, a chat or a command line.

Status (2026-10-12): nothing is delivered. The Fly app `ts-dashboard` has no
secrets and no machines, and the image exits at startup without the file.

## File

- Path: `/run/secrets/terrascale/dashboard.json`. The path is fixed in code;
  there is no environment variable or fallback.
- JSON, at most 64 KiB, readable by the image's non-root user (uid 1654).
- Read at startup and again on each use, so a replaced file takes effect
  without a restart. Startup and readiness fail closed when it is missing or
  invalid.

## Fields

| Field | Format checked by the app | Used for | Issued by |
| --- | --- | --- | --- |
| `StateProjectId` | TerraScale ID | Project of the state database `dashboard`; the app creates it and its 20 collections at startup | TerraBase (project owned by the dashboard service) |
| `StateApiKey` | non-empty | Documents API key for that project: database PUT, collection create/read, document get/put/delete | TerraBase |
| `AuthInstanceSlug` | non-empty | ts-auth tenant: issuer and endpoint paths `/{slug}/...`. Must be `terrascale`: other tenants serve only discovery and JWKS, so sign-in fails | fixed value |
| `AuthClientId` | non-empty | ts-auth OAuth client of the dashboard (see below) | ts-auth |
| `AuthClientSecret` | non-empty | `client_secret_basic` for that client | ts-auth |
| `AuthManagementKey` | starts with `sk_` | ts-auth management API key for the `terrascale` tenant; the app uses it to update a signed-in user's password and revoke that user's sessions | ts-auth |
| `ManagementCredential` | non-empty | Required by validation; no call uses it today | none yet (owner decision) |
| `SessionEncryptionKey` | base64 of exactly 32 bytes | Encrypts browser sessions, refresh credentials, pending OIDC and sign-up flows, and stored per-project document credentials. Replacing it makes all of those unreadable | generated once, randomly |
| `Stripe.SecretKey` | starts with `sk_` | Stripe API (Stripe.net) | Stripe (vendor) |
| `Stripe.WebhookSecret` | starts with `whsec_` | Verifies `POST /api/v1/billing/stripe/webhook` (also `/api/auth/stripe/webhook`) on `https://dashboard.terrascale.tech` | Stripe (vendor) |

## ts-auth client registration (nonsecret values)

Checked against ts-auth source at terrascale `6bb896bb` (current `main`,
includes #96). The deployed build is `55979ea3`, an ancestor with the same
issuer and paths (other differences are listed below).

- Issuer: `https://auth.terrascale.tech/terrascale`, with no trailing slash.
  Every endpoint sits under it: `/oauth/authorize`, `/oauth/token`,
  `/oauth/userinfo`, `/oauth/revoke`, `/oauth/logout`, and
  `/.well-known/jwks.json`.
- The dashboard client is seeded by ts-auth at every start from its own
  environment, not through an API. Its client_id is fixed in ts-auth code:
  `01M4EQ2FRJ8S8B29E8TQVWNW4J`. ts-auth reads three settings:
  - `TS_AUTH_DASHBOARD_CLIENT_SECRET`: 32-512 characters, no whitespace or `;`.
    It must equal the dashboard's `AuthClientSecret`.
  - `TS_AUTH_DASHBOARD_REDIRECT_URIS`: `;`-separated, matched exactly.
  - `TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS`: optional; an unregistered
    URI gets 400.
- Values the dashboard needs registered:
  - Redirect URIs: `https://dashboard.terrascale.tech/login/callback`, plus
    `https://ts-dashboard.fly.dev/login/callback` while DNS is missing (the
    app builds the URI from the request host).
  - Post-logout URIs: `https://dashboard.terrascale.tech/login`, plus
    `https://ts-dashboard.fly.dev/login` while DNS is missing.
- Grants the client holds: `authorization_code` with PKCE `S256`,
  `refresh_token`, `urn:terrascale:params:oauth:grant-type:password`,
  `urn:terrascale:params:oauth:grant-type:email-code` (with `email`, `password`
  or `code`, and `ip_address`), and `urn:ietf:params:oauth:grant-type:token-exchange`
  (`organization` required, `project`, scope `databases:read`).
- Scopes: `openid email profile offline_access`.
- Headless `/terrascale/headless/v1/{sign-up, email-code/start,
  password-reset/start, password-reset/complete}` use HTTP Basic and JSON.
- The CLI device flow (`urn:ietf:params:oauth:grant-type:device_code`) is
  served by the dashboard itself, not by ts-auth.

Contract differences to settle with the ts-auth owner before live sign-in:

1. ts-auth answers `prompt=login` with `error=login_required` and no code.
   The dashboard sends `prompt=login` after sign-out.
2. Project tokens: the dashboard requires `client_id` and an `aud` equal to
   the regional TerraBase origin it called (`https://<region>.api.terrascale.tech`).
   ts-auth sets `aud` only from `TS_AUTH_ACCESS_TOKEN_AUDIENCES`, and open
   PR #134 makes `https://api.terrascale.tech` the audience in every region.
   After #134 the dashboard would refuse every project token until its
   expected audience changes. Whether ts-auth sets `client_id` on project
   tokens is unverified.
3. Every token request comes from the dashboard's own IP, which shares
   ts-auth's per-peer token budget (1000/min by default).
4. The deployed `55979ea3` predates #96 and `platform_role` in sign-in
   access tokens. Platform administrators are not recognized until ts-auth
   is redeployed from `6bb896bb` or later.
5. Signed-in password change calls the ts-auth management API
   (`PATCH /v1/terrascale/users/{id}` and revoke-all-sessions with
   `AuthManagementKey`). ts-auth at `6bb896bb` serves no `/v1/{slug}/users`
   routes, so live password change fails with a 503 page until ts-auth
   serves them or the dashboard moves to the headless password reset.

## Delivery path (proposal, needs approval)

ts-auth already receives its production secrets as Fly secrets (its
zero-configuration contract). This repository's `AGENTS.md` requires OCI Vault
to create and deliver every first-party secret with no hand copying. The path
below therefore needs either Vault automation as the producer of the Fly
secret, or an explicit owner exception to that rule. It uses no new service:

1. One Fly secret on app `ts-dashboard`, proposed name `DASHBOARD_IDENTITY`,
   holding the base64-encoded JSON above.
2. A `fly.toml` entry that materializes it at the fixed path:

   ```toml
   [[files]]
     guest_path = "/run/secrets/terrascale/dashboard.json"
     secret_name = "DASHBOARD_IDENTITY"
   ```

   It is not committed yet: Fly would refuse to start a machine while the
   secret is absent. On the first machine, check that uid 1654 can read the
   file.

The values exist only after ts-auth is ready: its first signing key, the
`terrascale` client above and a management key. TerraBase must also be able to
issue the dashboard's state project and API key. Producing and setting the
secret happens outside this repository.

## Code changes still required in the dashboard

These are separate from delivery. The dashboard already calls ts-auth at its
global issuer `https://auth.terrascale.tech/terrascale`.

- **Project-token audience.** `Features/Auth/ProjectAccessTokens.cs` expects
  the regional TerraBase origin as `aud` and only accepts resource hosts that
  end in `.api.terrascale.tech`. Both must follow the audience the ts-auth
  owner settles (#134 proposes `https://api.terrascale.tech`, which fails that
  host check too).
- **TerraBase origin.** `Platform/Persistence/TerraBaseDiscovery.cs` reads
  Atlas `/v1/discovery`, which does not exist. The direction is the SDK
  `Region` contract (ts-dotnet PR #5) once a reviewed public 0.0.2 exists.
