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
| `AuthInstanceSlug` | non-empty | ts-auth tenant: issuer and endpoint paths `/{slug}/...`. Must be `terrascale`: other slugs answer 404 or lack the dashboard client, so sign-in fails | fixed value |
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

Settled against ts-auth source at terrascale `e30ffdc2` (fail-first tests
use these producer shapes):

1. ts-auth answers `prompt=login`, `consent` and `select_account` with
   `login_required`, `consent_required` and `account_selection_required`
   (`OidcEndpoints.cs`, `AuthorizeEndpoint`). The dashboard sends no `prompt`;
   sign-out ends the ts-auth session through its end-session endpoint.
   "Switch account" on an invitation signs out of the dashboard only, so a
   live ts-auth session can sign the same account back in through single
   sign-on; password and email-code sign-in are unaffected.
2. Sign-in access tokens carry `aud = [issuer, https://api.terrascale.tech]`
   (`OidcIssuer.SignInAudiences`). Organization and project tokens carry
   `aud = https://api.terrascale.tech` in every region
   (`AuthOptions.TerraBaseAudience`, `PlatformGrants.Identity`), `client_id`,
   `org_id`, `project_id`, `scope`, and live at most 15 minutes. The
   dashboard checks that audience; it no longer expects a regional origin.
3. The token-exchange response has no `scope` when the granted scopes equal
   the requested ones (OpenIddict 7.7.1, `OpenIddictServerHandlers.cs`
   lines 5670-5678). The dashboard reads the token's `scope` claim and
   checks the response `scope` only when present.
4. Every token request comes from the dashboard's own IP, which shares
   ts-auth's per-peer token budget (1000/min by default).
5. The deployed `55979ea3` predates #96 and `platform_role` in sign-in
   access tokens. Platform administrators are not recognized until ts-auth
   is redeployed from `6bb896bb` or later.
6. Signed-in password change calls the ts-auth management API
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

## TerraBase origin and regions

The dashboard calls ts-auth only at `https://auth.terrascale.tech/terrascale`
and TerraBase at `https://api.terrascale.tech/`, which Fly answers in a
serving region (`Platform/Persistence/TerraBaseDiscovery.cs`). Atlas
`/v1/discovery` was never served and is not called.

No producer publishes a region catalog yet. The dashboard lists one region:
the one named by both the `region` field of `GET https://api.terrascale.tech/healthz`
and its `X-TerraBase-Region` header (`DatabaseBackend.RegionsAsync`). That
region is only the one that answered, not necessarily the nearest, a
database's home, or every serving region. A missing, malformed or
disagreeing region, a 429 or a 503 lists nothing and provisions nothing.
Adding replica regions shows as unavailable until TerraBase publishes a
catalog; that catalog is an upstream dependency.

### Existing home and replica regions

A stored home or replica region is never re-resolved through the catalog
alone. `DatabaseRegion.Resolve` takes the catalog's entry, else the region
its own id names: a TerraBase region id (lowercase letter, then lowercase
letters, digits or `-`, at most 63 characters, not ending in `-`) addresses
`https://<id>.api.terrascale.tech/`, which serves only that region and fails
closed elsewhere (`region-identity.md`). So:

- A database keeps its recorded home whichever region answers
  `api.terrascale.tech`; its management calls go to its own regional host.
  The dashboard's direct HTTP calls also send `X-TerraScale-Region-Pin: <home>`;
  calls through the vendored SDK, which predates the pin, rely on the
  regional host failing closed.
- An id that is not a region id derives no origin; the call fails closed
  instead of moving to another region.
- The placement form lists the database's own regions next to the catalog,
  so saving never drops a replica the catalog does not name.
- New databases and projects can still choose only catalog regions.

### 421 wrong_region

TerraBase's `DatabaseRegions.Misdirected` (terrascale `e30ffdc2`) answers a
region that does not manage the database with:

- status `421`, `Cache-Control: no-store`;
- `X-TerraBase-Region`: the answering region (on every TerraBase answer);
- `X-TerraBase-Database-Home`: the database's home region;
- `X-TerraBase-Database-Regions`: the home, then every replica region,
  comma-separated;
- body `{"error":{"code":"wrong_region","message":"the database is served in <home>"}}`;
- `fly-replay: region=<home>` behind Fly Proxy only.

The dashboard reads these as ts-dotnet 0.0.2's `TerraBaseWrongRegionException`
does (`DatabaseWrongRegionException`): region names that are not region ids
are dropped. It sends the request once, never retries or redirects it,
changes no stored record, and answers the user `409` with "The database is
managed in <home>. Nothing was changed." (a browser treats `421` as a
connection retry signal, so it is not passed through).
