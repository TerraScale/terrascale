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
| Management API | Full developer REST API with `sk_...` keys, pagination, rate limiting |
| JWT templates | Custom-claim tokens (`{{user.email}}` substitution) minted on demand |
| Webhooks | HMAC-signed outbound events with retries, driven by the audit log |
| Audit logs | Every significant action, queryable API |
| Billing | Stripe subscriptions: Checkout, customer portal, signature-verified webhooks, plan quotas |

Multi-instance: one ts-auth deployment hosts many customer instances; every
instance gets its own users, keys, orgs, and OAuth clients, isolated by
instance-scoped keys.

## Layout

```
src/TerraScale.Auth/
  App.cs                  # composition + operator onboarding
  EndpointModules.cs      # feature slice registry
  Configuration/          # TS_AUTH_* env options
  Crypto/                 # BLAKE2b, Argon2id, JWT, TOTP, encodings, PHC hashing
  WebAuthn/               # WebAuthn verifier + COSE keys
  Keys/                   # per-instance ES256 key ring, AES-GCM secret box
  Store/                  # AuthStore over the SDK documents API + atomic uniqueness claims
  Users/ OAuth/ Passkeys/ Mfa/ Organizations/ Management/ Webhooks/     # core feature slices
  Connections/ Scim/ Billing/ Templates/ Admin/                        # SSO, directories, payments, tokens
tests/TerraScale.Auth.Tests/
  AuthServiceFixture.cs   # boots the real service over the real embedded TerraBase engine
  ...E2E tests            # every flow, driven over real HTTP
```

## Running

The service needs a TerraScale API endpoint + project API key + database:

```sh
TS_AUTH_PUBLIC_ORIGIN=https://auth.example.com \
TS_AUTH_TERRASCALE_API_KEY=sk_... \
TS_AUTH_DATABASE=ts-auth \
TS_AUTH_MASTER_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_OPERATOR_KEY=$(head -c 32 /dev/urandom | base64) \
./run
```

| Variable | Meaning |
| --- | --- |
| `TS_AUTH_PUBLIC_ORIGIN` | Public origin (issuer URLs, email links, WebAuthn RP) |
| `TS_AUTH_TERRASCALE_API_URL` | TerraScale API origin (default `https://api.terrascale.tech`) |
| `TS_AUTH_TERRASCALE_API_KEY` | Project API key for TerraBase |
| `TS_AUTH_TERRASCALE_PROJECT_ID` / `TS_AUTH_TERRASCALE_TENANT_ID` | Optional project/tenant selectors |
| `TS_AUTH_DATABASE` | TerraBase database backing ts-auth |
| `TS_AUTH_MASTER_KEY` | base64 32 bytes; encrypts recoverable secrets (TOTP seeds) |
| `TS_AUTH_OPERATOR_KEY` | Bearer key that can create instances (`POST /v1/instances`) |
| `TS_AUTH_LISTEN` | Listen URL (default `http://+:8080`) |
| `TS_AUTH_RATE_LIMIT` | Management API requests per key per 5 min (default 1000) |
| `TS_AUTH_STRIPE_SECRET_KEY` | Stripe secret key (billing routes 503 when unset) |
| `TS_AUTH_STRIPE_WEBHOOK_SECRET` | Stripe webhook signing secret (`whsec_...`) |
| `TS_AUTH_PRICE_PRO` / `TS_AUTH_PRICE_ENTERPRISE` | Stripe price ids for the plans |
| `TS_AUTH_STRIPE_API_URL` | Stripe API origin (test override) |

Uniqueness of user-chosen identifiers (emails, usernames, org slugs,
credentials) is enforced with atomic claim documents: CreateOnly writes the
engine rejects on collision — race-safe without transactions. See
`docs/parity.md` for the full Clerk/WorkOS/Auth0 feature matrix.

### Local development against a real TerraBase member

ts-auth talks to the public database API, so any member works. From a sibling
`terrabase` checkout:

```sh
TERRABASE_HOST_URL=http://127.0.0.1:8420 \
TERRABASE_DATABASE_ROUTE=ts-auth-dev TERRABASE_DATABASE_TENANT=ts-auth \
TERRABASE_DATABASE_ULID=01ARZ3NDEKTSV4RRFFQ69G5FAV TERRABASE_DATABASE_MODE=explicit \
TERRABASE_DATABASE_PATH=/tmp/ts-auth-dev TERRABASE_REGION=GRU \
dotnet run --project terrabase/src/TerraBase.Host
# then point ts-auth at it:
TS_AUTH_TERRASCALE_API_URL=http://127.0.0.1:8420 TS_AUTH_TERRASCALE_API_KEY=dev ...
```

Tests never need this: `AuthServiceFixture` boots the **embedded TerraBase
engine** in-process and hands its transport to the real SDK, so every E2E test
exercises the exact production path (service → SDK → document API → engine).

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
  `/invitations/{id}/accept`
- OAuth/OIDC: `/S/.well-known/openid-configuration`, `/S/.well-known/jwks.json`,
  `/S/oauth/{authorize,token,userinfo,revoke,introspect,logout}`
- SCIM: `/scim/{directoryId}/Users`, `/Groups`, `/ServiceProviderConfig`
- Management (Bearer `sk_...`): `/v1/S/users*`, `/v1/S/sessions*`,
  `/v1/S/api-keys*`, `/v1/S/oauth-clients*`, `/v1/S/connections*`,
  `/v1/S/directories*`, `/v1/S/organizations*`, `/v1/S/domains*`,
  `/v1/S/jwt-templates*`, `/v1/S/webhooks*`, `/v1/S/audit-events`,
  `/v1/S/billing*`, `/v1/S/instance`
- Billing webhook: `POST /v1/billing/webhook` (Stripe signature)
- Operator: `POST /v1/instances` (Bearer `TS_AUTH_OPERATOR_KEY`)

## CI

`.forgejo/workflows/ci.yml` restores, builds, runs the full test suite
(minimum-expected-tests guard), publishes a single-file self-contained binary
into the distroless `Containerfile`, and pushes a SHA-tagged immutable image to
`git.terrascale.tech/terrascale/ts-auth` from protected `main`.
