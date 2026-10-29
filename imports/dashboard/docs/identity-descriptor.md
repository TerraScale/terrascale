# Dashboard secrets and state identity

This is the consumer contract the application implements
(`src/TerraScale.Dashboard/Platform/Persistence/DashboardIdentity.cs` and
`TerraBaseDashboardState.cs`). It lists names, formats and owners only. It
contains no credential values, and none may be added here, to an issue, a chat
or a command line.

## OCI Vault delivery

ts-orch reads production secrets from OCI Vault in gru using instance principal
and delivers protected files named by `<NAME>_FILE`. The app supports those files
and direct environment values for test hosts. There is no default; startup fails
closed on missing or invalid secrets, and values are never logged.
CI secrets remain in GitHub Actions secrets. This document provisions nothing.
All five secrets are required in Development too; test-only hosts supply synthetic identities.

| Environment variable | Format checked by the app | Used for | Issued by |
| --- | --- | --- | --- |
| `TS_AUTH_DASHBOARD_CLIENT_SECRET` | 32-512 characters, no whitespace, control character or `;` | `client_secret_basic` for the ts-auth dashboard client. ts-auth reads the same-named secret on its own app to seed that client, so both apps hold the same value | generated once |
| `TS_DASHBOARD_SESSION_ENCRYPTION_KEY` | base64 of exactly 32 bytes | AES-GCM for browser sessions, refresh credentials and pending OIDC and sign-up flows. Replacing it makes all of those unreadable | generated once |
| `STRIPE_SECRET_KEY` | `sk_test_`, `sk_live_`, `rk_test_` or `rk_live_` prefix | Stripe API (Stripe.net) | Stripe (vendor) |
| `STRIPE_WEBHOOK_SECRET` | starts with `whsec_` | Verifies the Stripe webhook routes | Stripe (vendor) |
| `TERRABASE_PEER_SECRET` | exactly 64 hexadecimal characters | `X-TerraBase-Peer-Credential` on state requests to TerraBase's internal listener. `ts-terrabase` and `ts-auth` hold the same value (terrascale `docs/terrabase/peer-authentication.md`) | generated once, shared |

Fixed in code, not delivered: the ts-auth tenant `terrascale`, its issuer
`https://auth.terrascale.tech/terrascale` and the dashboard client id
`01M4EQ2FRJ8S8B29E8TQVWNW4J` (terrascale `OidcServer.cs:38`). There is no
state project, state API key, management credential or ts-auth management key:
ts-auth serves no `/v1/{slug}/users` management routes. Sign-in, sign-up,
password reset, password change and MFA happen only on ts-auth's hosted pages;
the profile links to `https://auth.terrascale.tech/ui/terrascale/account`.

## ts-auth client registration (nonsecret values, ts-auth's own secrets)

ts-auth seeds the dashboard client at every start from its environment
(terrascale `AuthOptions.cs:76,208,309-333`):

- `TS_AUTH_DASHBOARD_CLIENT_SECRET`: the value above.
- `TS_AUTH_DASHBOARD_REDIRECT_URIS`: `;`-separated, matched exactly. The
  dashboard needs `https://dash.terrascale.tech/login/callback` (the app builds
  the URI from the request host).
- `TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS`: `https://dash.terrascale.tech/login`.

Grants the dashboard uses: `authorization_code` with PKCE `S256`, state and
nonce (`GET /login` to `{issuer}/oauth/authorize`, `GET /login/callback`),
`refresh_token` and `urn:ietf:params:oauth:grant-type:token-exchange`. Sign-out
redirects to `{issuer}/oauth/logout` with `id_token_hint`,
`post_logout_redirect_uri` and `client_id`. The dashboard requests the
exact project scopes an action needs (`databases:read|write`,
`documents:read|write`); ts-auth grants them only within the user's role.
The internal-billing commands sign in as ts-auth's public CLI client `ts-cli`
with the device authorization grant (`{issuer}/oauth/device`).

Settled against ts-auth source at terrascale `e30ffdc2`:

1. ts-auth answers `prompt=login`, `consent` and `select_account` with
   `login_required`, `consent_required` and `account_selection_required`. The
   dashboard sends no `prompt`; sign-out ends the ts-auth session through its
   end-session endpoint.
2. Sign-in access tokens carry `aud = [issuer, https://api.terrascale.tech]`.
   Project tokens carry `aud = https://api.terrascale.tech` in every region,
   `client_id`, `org_id`, `project_id`, `scope`, and live at most 60 seconds.
3. The token-exchange response has no `scope` when the granted scopes equal
   the requested ones; the dashboard checks the token's `scope` claim.
4. Organizations, projects, members, invitations, ownership transfer and audit
   events are ts-auth's `/v1/terrascale` platform API, called with the user's
   own sign-in token. ts-auth creates no Personal organization; the dashboard
   creates one named "Personal" on first sign-in under a lease in its state.

## Plan member allowance

A plan's member allowance (Starter: 5, pending invitations included) limits
the invitations the dashboard sends. ts-auth does not enforce it.

1. Each dashboard invitation takes a reservation in the organization's state
   document, in the same compare-and-set that reads ts-auth's current members
   and invitations. Occupied slots are distinct emails across members,
   invitations ts-auth lists as pending or expired, and reservations. ts-auth
   derives "expired" from its clock without storing it, so an expired
   invitation can still be accepted and keeps its slot until the owner revokes
   or resends it (both store a revoke). No replica clock decides anything.
2. ts-auth has no idempotency key or cancellation for invitation creation, so
   a request with an unknown outcome (timeout, lost response, 5xx) may still
   commit later. Its reservation ends only when ts-auth lists that request's
   invitation with a stored decision: accepted, declined or revoked. Expired,
   elapsed time, clocks and an absent entry never end it.
3. Cost and recovery: an unknown outcome that never commits keeps its email's
   slot counted, with no time limit or manual release. The email can still be
   invited: the retry uses the same slot, and ts-auth holds one pending
   invitation per email and target, so the retry and the original never both
   hold one. Unknown outcomes for many emails, for example during a ts-auth
   outage, can fill the allowance; a larger plan raises it.
4. Limitation: any organization admin can invite through ts-auth's API (SDK,
   ts-cli) without the dashboard, and project admins can invite to their
   projects. Those invitations take no reservation, so the allowance is not a
   global guarantee. A hard allowance needs ts-auth to hold it and check it in
   its own invitation and membership transaction; that producer change is not
   part of this release.
5. Resending an invitation revokes the original, pending or expired, then
   copies ts-auth's project and role exactly. A project, role or invitation the
   dashboard cannot confirm refuses the resend before the original is revoked.

## State database

The dashboard keeps its own state in the `ts-dashboard` database of the
ts-dashboard project `00000000000000000000000005`, which TerraBase seeds on every
start (terrascale `PlatformDatabases.cs`). It reaches TerraBase's private
listener at `TS_DASHBOARD_TERRABASE_URL` when set (any environment, an http(s)
origin such as `http://127.0.0.1:8081`), defaulting to that loopback origin
only in Development; otherwise it refuses to
start. Only the `terrabase-state` HTTP client sends
`X-TerraBase-Peer-Credential`, and only to that origin: its handler
(`TerraBasePeerCredential`) refuses any other destination and its primary
handler follows no redirects. The shared `platform` client (ts-auth, the public
TerraBase API, Stripe, status) never carries it. gru stays the single home until cross-region conditional writes are
proven. Startup creates missing collections (`409 name_taken` counts as
created) and never creates or replaces the database. The platform project has
no customer members or keys; no customer token reaches this database.

## TerraBase origin and regions

The dashboard calls ts-auth only at `https://auth.terrascale.tech/terrascale`
and TerraBase at `https://api.terrascale.tech/`, the public OCI API
origin (`Platform/Persistence/TerraBaseDiscovery.cs`). Atlas
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
- body `{"error":{"code":"wrong_region","message":"the database is served in <home>"}}`.

The dashboard reads these as ts-dotnet 0.0.2's `TerraBaseWrongRegionException`
does (`DatabaseWrongRegionException`): region names that are not region ids
are dropped. It sends the request once, never retries or redirects it,
changes no stored record, and answers the user `409` with "The database is
managed in <home>. Nothing was changed." (a browser treats `421` as a
connection retry signal, so it is not passed through).
