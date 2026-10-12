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
| `StateApiKey` | non-empty | Documents API key for that project: database PUT, collection create/read, document CRUD and transactions | TerraBase |
| `AuthInstanceSlug` | non-empty; must be `terrascale` | ts-auth tenant: issuer and endpoint paths `/{slug}/...` | fixed value |
| `AuthClientId` | non-empty | ts-auth OAuth client of the dashboard (see below) | ts-auth |
| `AuthClientSecret` | non-empty | `client_secret_basic` for that client | ts-auth |
| `AuthManagementKey` | starts with `sk_` | ts-auth management API key for the `terrascale` tenant; the app uses it only to update a signed-in user's password | ts-auth |
| `ManagementCredential` | non-empty | Required by validation; no call uses it today | none yet (owner decision) |
| `SessionEncryptionKey` | base64 of exactly 32 bytes | Encrypts browser sessions and stored refresh credentials | generated once, randomly |
| `Stripe.SecretKey` | starts with `sk_` | Stripe API (Stripe.net) | Stripe (vendor) |
| `Stripe.WebhookSecret` | starts with `whsec_` | Verifies `POST /api/v1/billing/stripe/webhook` (also `/api/auth/stripe/webhook`) on `https://dashboard.terrascale.tech` | Stripe (vendor) |

## ts-auth client registration (nonsecret values)

- Tenant: `terrascale`. Confidential client, `client_secret_basic`.
- Redirect URI: `https://dashboard.terrascale.tech/login/callback`. The app
  builds it from the request's host, so the app's Fly host
  `https://ts-dashboard.fly.dev/login/callback` is needed as well while DNS is
  missing.
- Post-logout redirect URI: `https://dashboard.terrascale.tech/login`, and
  `https://ts-dashboard.fly.dev/login` while DNS is missing.
- Authorization code with PKCE `S256`, `nonce`, and optional `prompt=login`.
- Scopes: `openid email profile offline_access`. Token exchange also requests
  `databases:read`.
- Grant types at `/{slug}/oauth/token`: `authorization_code`, `refresh_token`,
  `urn:terrascale:params:oauth:grant-type:password`,
  `urn:terrascale:params:oauth:grant-type:email-code` (both carry
  `ip_address`), and `urn:ietf:params:oauth:grant-type:token-exchange`
  (subject access token, `organization`, `project`, scope `databases:read`).
- Also: token revocation, UserInfo, end-session, and headless
  `/{slug}/headless/v1/{sign-up, email-code/start, password-reset/start,
  password-reset/complete}`.
- The CLI device flow (`urn:ietf:params:oauth:grant-type:device_code`) is
  served by the dashboard itself, not by ts-auth.

## Delivery path (proposal, needs approval)

ts-auth already receives its production secrets as Fly secrets (its
zero-configuration contract). The smallest compatible path for the dashboard
uses the same mechanism and no new service:

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
issue the dashboard's state project and API key. Setting the secret is a
secure operator step handled outside this repository.

## Code changes still required in the dashboard

These are separate from delivery:

- **ts-auth origin.** The app finds ts-auth through
  `https://atlas.terrascale.tech/api/v1/auth/discovery` and accepts only
  `https://<region>.auth.terrascale.tech` origins
  (`Features/Auth/TsAuthGateway.cs`). Current ts-auth source serves one
  global issuer `https://auth.terrascale.tech/{tenant}` in every region, and
  Atlas is NXDOMAIN. The app must move to the global issuer.
- **TerraBase origin.** `Platform/Persistence/TerraBaseDiscovery.cs` reads
  Atlas `/v1/discovery`, which does not exist. The direction is the SDK
  `Region` contract (ts-dotnet PR #5) once a reviewed public 0.0.2 exists.
