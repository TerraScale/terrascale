# ts-auth

A hosted identity provider — the Clerk / Auth0 / WorkOS category — built as a
first-class TerraScale application: ts-auth is itself **an end user of the
platform**, persisting every user, session, passkey, and OAuth grant as JSON
documents in TerraBase through the official `TerraScale.DotNet` SDK.

Everything auth-shaped is written from scratch (OpenIddict and ASP.NET
Identity were studied as references, not referenced as packages): password
hashing, JWT, OAuth 2.1/OIDC, WebAuthn, TOTP, webhook signing.

## Feature set

| Area | What ships |
| --- | --- |
| Users | Email/password, usernames, profile + public/private metadata, blocking, soft delete, impersonation |
| Sessions | Server-side sessions, httpOnly cookie, device list, revoke one/all, 14-day TTL |
| Passkeys | Full WebAuthn: registration (none + packed attestation), discoverable-credential sign-in, sign-count clone detection |
| OAuth 2.1 / OIDC | Authorization code + PKCE (S256), refresh rotation with reuse detection, client credentials, discovery, JWKS, userinfo, introspection, revocation, RP-initiated logout; ES256 id/access tokens |
| MFA | TOTP (RFC 6238) enrollment + verification, recovery codes, step-up during sign-in |
| Email flows | Verification, password reset, magic links, email passcodes (pluggable `IEmailSender`; logs by default) |
| Social SSO | Upstream OIDC connections (Google/GitHub/any issuer) with JWKS-verified id_tokens |
| Enterprise SSO | SAML 2.0 connections (HTTP-Redirect AuthnRequest, XML-DSig-verified assertions) |
| Directory sync | SCIM 2.0 `/scim/{dir}` — users, groups, deprovisioning |
| Organizations | Orgs, memberships, roles, invitations, enrollment domains |
| Admin portal | Hosted page for org admins to manage SSO connections and directories |
| Management API | Full developer REST API with `sk_...` keys, per-family key scopes, pagination, rate limiting |
| JWT templates | Custom-claim tokens (`{{user.email}}` substitution) minted on demand |
| Webhooks | HMAC-signed outbound events with retries, durable delivery status, dead-letter listing, and operator replay |
| Audit logs | Every significant action, queryable API |
| Billing | Stripe subscriptions: Checkout, customer portal, signature-verified webhooks, plan quotas |

Multi-instance: one ts-auth deployment hosts many customer instances; every
instance gets its own users, keys, orgs, and OAuth clients, isolated by
instance-scoped keys.

The API surface is split into feature slices. Each route is an `IEndpoint` in its own file; the feature module exposes its endpoint catalog and `EndpointModules` composes those catalogs for `TerraScale.Endpoints`.

## Layout

```
src/TerraScale.Auth/
  App.cs                  # composition + middleware
  EndpointModule.cs       # shared TerraScale.Endpoints catalog
  Configuration/          # TS_AUTH_* env options
  Crypto/                 # BLAKE2b, Argon2id, JWT, TOTP, encodings, PHC hashing
  WebAuthn/               # WebAuthn verifier + COSE keys
  Keys/                   # per-instance ES256 key ring, AES-GCM secret box
  Store/                  # AuthStore over the SDK documents API + atomic uniqueness claims
  Users/ OAuth/ Passkeys/ Mfa/ Organizations/ Management/ Webhooks/     # core feature slices
  Connections/ Scim/ Billing/ Templates/ Admin/ Instances/ Passcodes/   # one IEndpoint class per route
  Observability/                                                      # health and metrics endpoints
tests/TerraScale.Auth.Tests/
  AuthServiceFixture.cs   # boots the real service (real loopback listener) over the real embedded TerraBase engine
  BrowserFixture.cs       # shared headless Chromium for the hosted-UI tests
  ...E2E tests            # every flow: hosted UI in a real browser (Playwright), APIs over real HTTP
```

Browser tests need the Playwright Chromium build that matches the
`Microsoft.Playwright` package (CI installs and caches it):

```sh
~/.nuget/packages/microsoft.playwright/<version>/.playwright/node/linux-x64/node \
  ~/.nuget/packages/microsoft.playwright/<version>/.playwright/package/cli.js install chromium
```

## Running

The build references the sibling `ts-dotnet` SDK checkout (see AGENTS.md);
the service itself needs a TerraScale API endpoint + project API key + database:

```sh
TS_AUTH_PUBLIC_ORIGIN=https://auth.example.com \
TS_AUTH_TERRASCALE_API_KEY=sk_... \
TS_AUTH_DATABASE=ts-auth \
TS_AUTH_MASTER_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_OPERATOR_KEY=$(head -c 32 /dev/urandom | base64) \
dotnet run --project src/TerraScale.Auth
```

| Variable | Meaning |
| --- | --- |
| `TS_AUTH_PUBLIC_ORIGIN` | Public origin (issuer URLs, email links, WebAuthn RP) |
| `TS_AUTH_TERRASCALE_API_URL` | TerraScale API origin (default `https://api.terrascale.tech`) |
| `TS_AUTH_TERRASCALE_API_KEY` | Project API key for TerraBase |
| `TS_AUTH_TERRASCALE_PROJECT_ID` / `TS_AUTH_TERRASCALE_TENANT_ID` | Optional project/tenant selectors |
| `TS_AUTH_DATABASE` | TerraBase database backing ts-auth |
| `TS_AUTH_MASTER_KEY` | base64 32 bytes; encrypts recoverable secrets (TOTP seeds) |
| `TS_AUTH_AUDIT_SIGNING_KEY` | base64 32-byte HMAC key for authentic audit-event seals (required outside Development) |
| `TS_AUTH_AUDIT_VERIFY_KEYS` | optional semicolon-separated previous audit HMAC keys retained during rotation |
| `TS_AUTH_OPERATOR_KEY` | Bearer key that can create instances (`POST /v1/instances`) |
| `TS_AUTH_METRICS_KEY` | Dedicated private `/metrics` scrape key sent as `X-TerraScale-Metrics-Key` (required outside Development) |
| `TS_AUTH_LISTEN` | Listen URL (default `http://+:8080`) |
| `TS_AUTH_RATE_LIMIT` | Management API requests per key per 5 min (default 1000) |
| `TS_AUTH_STRIPE_SECRET_KEY` | Stripe secret key (billing routes 503 when unset) |
| `TS_AUTH_STRIPE_WEBHOOK_SECRET` | Stripe webhook signing secret (`whsec_...`) |
| `TS_AUTH_PRICE_PRO` / `TS_AUTH_PRICE_ENTERPRISE` | Stripe price ids for the plans |
| `TS_AUTH_STRIPE_API_URL` | Stripe API origin (test override) |
| `TS_AUTH_SMTP_HOST` | SMTP host for real email delivery (OCI: `smtp.email.sa-saopaulo-1.oci.oraclecloud.com`); when unset, emails are only logged |
| `TS_AUTH_SMTP_PORT` | SMTP port, default 587 (STARTTLS) |
| `TS_AUTH_SMTP_USERNAME` | SMTP username — the OCI SMTP credential OCID, full string including the `@oci` suffix |
| `TS_AUTH_SMTP_PASSWORD` | SMTP credential password |
| `TS_AUTH_SMTP_FROM_ADDRESS` | Envelope-from; must be an approved sender (OCI: `noreply@terrascale.tech`) |

The four `TS_AUTH_SMTP_HOST` / `_USERNAME` / `_PASSWORD` / `_FROM_ADDRESS` are
all-or-nothing: setting any one without the others fails startup with the list
of missing variables.

Uniqueness of user-chosen identifiers (emails, usernames, org slugs,
credentials) is enforced with atomic claim documents: CreateOnly writes the
engine rejects on collision — race-safe without transactions. See
`docs/parity.md` for the full Clerk/WorkOS/Auth0 feature matrix.

### Email delivery (OCI)

Without `TS_AUTH_SMTP_*` configuration ts-auth uses the logging sender —
emails go to stdout only (the dev/test default). For real delivery via OCI
Email Delivery, set the five variables above; fetch the credentials from the
ts-infra state:

```sh
cd ts-infra && export TF_VAR_state_passphrase=...
tofu output -raw smtp_username   # full string incl. the @oci suffix
tofu output -raw smtp_password
```

DNS prerequisites (terrascale.tech is hosted at Purelymail — not editable from
this repo):

- SPF: the domain TXT record must include `include:rp.oracleemaildelivery.com`
  (Americas) or deliverability suffers.
- DKIM: the OCI managed-DKIM records are output by ts-infra once applied;
  publish the CNAMEs in the zone. DKIM stays `PENDING` until they resolve.

### Local development against a real TerraBase member

ts-auth talks to the public database API. The local development member runs
the real TerraBase engine and Kestrel transport with a development API-key
gateway stand-in. From this checkout:

```sh
TERRABASE_HOST_URL=http://127.0.0.1:8420 \
TERRABASE_DEV_API_KEY=dev \
TERRABASE_DATABASE_PATH=/tmp/ts-auth-dev \
nice -n 19 dotnet run --project tools/TerraBaseDevMember -c Release
# then point ts-auth at it:
TS_AUTH_TERRASCALE_API_URL=http://127.0.0.1:8420 TS_AUTH_TERRASCALE_API_KEY=dev ...
```

The raw `TerraBase.Host` executable stays fail-closed for public document
requests. A deployment must compose the platform's project-key authentication
and tenant-authorization gateway; `TerraBaseDevMember` is only the local stand-in.

Tests never need this: `AuthServiceFixture` boots the **embedded TerraBase
engine** in-process, hands its transport to the real SDK, and serves the real
service on a loopback listener — so every E2E test exercises the exact
production path (browser or HttpClient → service → SDK → document API →
engine).

## Provisioning ts-auth on TerraScale (the end-user flow)

When the platform is live, ts-auth is provisioned exactly like any customer
project — see `scripts/provision.sh`:

1. `POST /api/v1/organizations/{orgId}/projects` — project `ts-auth` in the
   TerraScale organization.
2. `POST /api/v1/projects/{id}/auth/api-keys` — a project API key.
3. Create the `ts-auth` database in a region, then deploy the container with
   the environment above.

## API surface (per instance slug `S`)

- Hosted UI: `/ui/S/sign-in`, `/sign-up`, `/account`, `/account/sessions`,
  `/forgot-password`, `/reset-password`, `/verify-email`, `/magic-link`,
  `/passcode`, `/mfa/*`, `/passkeys/*`, `/connections/*`, `/admin`,
  `/billing`, `/invitations/{id}/accept`
- OAuth/OIDC: `/S/.well-known/openid-configuration`, `/S/.well-known/jwks.json`,
  `/S/oauth/{authorize,token,userinfo,revoke,introspect,logout}`
- SCIM: `/scim/{directoryId}/Users`, `/Groups`, `/ServiceProviderConfig`,
  `/ResourceTypes`, `/Schemas`
- Management (Bearer `sk_...`): `/v1/S/users*`, `/v1/S/sessions*`,
  `/v1/S/api-keys*`, `/v1/S/oauth-clients*`, `/v1/S/connections*`,
  `/v1/S/directories*`, `/v1/S/organizations*` (incl. org `domains*`),
  `/v1/S/domains/{domain}/enroll`, `/v1/S/jwt-templates*`, `/v1/S/webhooks*`,
  `/v1/S/audit-events`,
  `/v1/S/billing*`, `/v1/S/instance`
- Billing webhook: `POST /v1/billing/webhook` (Stripe signature)
- Quota repair: `POST /v1/{slug}/billing/quota/reconcile` removes only orphaned durable seat claims and returns the examined/removed/retained counts. It requires the instance's authenticated management key.

Outbound webhook signing secrets are envelope-encrypted with `TS_AUTH_MASTER_KEY` at rest and are returned only once when an endpoint is created.
Webhook delivery checkpoints are stored durably per instance, so a process restart resumes from the last successful tick and does not silently skip queued audit events.
Each endpoint/event delivery also has a durable outcome (`succeeded`, `pending`, or `dead`); dead deliveries are visible at `/v1/{slug}/webhooks/dead-letter` and can be replayed with the corresponding operator endpoint after remediation. Audit exports include their integrity digest and authenticity seal so downstream retention systems can verify them.
The `TerraScale.Auth.Store.AuditExportVerifier` helper verifies an exported JSON array offline. Supply the active and retained rotation keys to distinguish digest mutation from an invalid authenticity seal; verification returns only counts and event IDs, never event payloads.
- Operator: `POST /v1/instances` (Bearer `TS_AUTH_OPERATOR_KEY`)
- Liveness/readiness: `GET /healthz` and `GET /readyz` (`/readyz` checks TerraBase member health).
- Private metrics: `GET /metrics` (`X-TerraScale-Metrics-Key`), with bounded request, uptime, build, and readiness metrics.

## CI

`.forgejo/workflows/ci.yml` restores, builds, runs the full test suite
(minimum-expected-tests guard), publishes a single-file self-contained binary
into the distroless `Containerfile`, and pushes a SHA-tagged immutable image to
`git.terrascale.tech/terrascale/ts-auth` from protected `main`.
