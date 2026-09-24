# ts-auth

A hosted identity provider in the Clerk / Auth0 / WorkOS category. It is a
first-class TerraScale application: ts-auth uses `ts-rust` as its database
contract for user, session, passkey, and OAuth grant documents.

The service is written in Rust. `docs/parity.md` tracks its capabilities and
the evidence still needed for live integrations.

## Target feature inventory

| Area | Capability to verify in Rust |
| --- | --- |
| Users | Email/password, usernames, profile + public/private metadata, blocking, soft delete, impersonation |
| Sessions | Server-side sessions, httpOnly cookie, device list, revoke one/all, 14-day TTL |
| Passkeys | Full WebAuthn: registration (none + packed attestation), discoverable-credential sign-in, sign-count clone detection |
| OAuth 2.1 / OIDC | Authorization code + PKCE (S256), refresh rotation with reuse detection, client credentials, discovery, JWKS, userinfo, introspection, revocation, RP-initiated logout; ES256 id/access tokens |
| MFA | TOTP (RFC 6238) enrollment + verification, recovery codes, step-up during sign-in |
| Email flows | Verification, password reset, magic links, email passcodes (pluggable email sender; logs by default) |
| Social SSO | Upstream OIDC connections (Microsoft, Google, any issuer) with JWKS-verified ID tokens; GitHub OAuth with verified email lookup |
| Enterprise SSO | SAML 2.0 connections (HTTP-Redirect AuthnRequest, XML-DSig-verified assertions) |
| Directory sync | SCIM 2.0 `/scim/{dir}` — users, groups, deprovisioning |
| Organizations | Orgs, memberships, roles, invitations, enrollment domains |
| Admin portal | Dashboard-styled hosted page for org admins to configure GitHub, Microsoft, Google, custom OIDC and SAML connections, and view directories |
| Management API | Full developer REST API with `sk_...` keys, per-family key scopes, pagination, rate limiting |
| JWT templates | Custom-claim tokens (`{{user.email}}` substitution) minted on demand |
| Webhooks | HMAC-signed outbound events with retries, durable delivery status, dead-letter listing, and operator replay |
| Audit logs | Every significant action, queryable API |
| Billing | Stripe subscriptions: Checkout, customer portal, signature-verified webhooks, plan quotas |

Multi-instance: one ts-auth deployment hosts many customer instances; every
instance gets its own users, keys, orgs, and OAuth clients, isolated by
instance-scoped keys. The public issuer and hosted UI use one global origin;
customers do not choose an authentication region. Regional ts-auth processes
share one database, and that database is replicated to the admitted launch
regions. Project and customer database placement remain separate choices.

The HTTP app composes feature routers and OpenAPI documents from each Rust
feature. Scalar is served at `/scalar`; the JSON contract is at
`/openapi/v1.json`.

## Layout

```text
crates/auth-app/          # Axum startup, configuration, middleware, feature routers, Datastar UI
crates/auth-contracts/    # shared request, response, and persisted document shapes
crates/auth-security/     # password, tokens, JWT, WebAuthn, TOTP, and secret handling
crates/auth-store/        # TerraBase SDK-backed storage and query adapter
scripts/                  # provisioning and real-stack smoke checks
```

The previous .NET service, tests, and development helper are retained in Git
history; the current source tree builds only the Rust service.

The service uses `ts-rust` as its TerraBase document and query contract. The
live database implementation is still being completed, so current Rust
verification uses local contract fixtures and explicit backend-unavailable
responses. Persistent uploaded files, if a feature needs them, belong in
RustFS through its S3 API; packaged fonts and static assets are embedded in
the service binary.

## Running

The build requires the sibling `../ts-rust` SDK checkout at commit
`c590cd41eabf05bbd5982a1580c1bb01880c346a`, as pinned in CI. Install the
Rust 1.98.1 toolchain from `.mise.toml` with `mise install`. The service itself
needs a TerraScale API endpoint, project API key, and database:

```sh
TS_AUTH_PUBLIC_ORIGIN=https://auth.terrascale.tech \
TS_AUTH_TERRASCALE_API_URL=https://api.terrascale.tech \
TS_AUTH_TERRASCALE_API_KEY=sk_... \
TS_AUTH_DATABASE=ts-auth \
TS_AUTH_MASTER_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_AUDIT_SIGNING_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_OPERATOR_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_METRICS_KEY=$(head -c 32 /dev/urandom | base64) \
mise exec -- cargo run --locked -p auth-app
```

| Variable | Meaning |
| --- | --- |
| `TS_AUTH_PUBLIC_ORIGIN` | One global public origin shared by every replica (issuer URLs, email links, WebAuthn RP) |
| `TS_AUTH_TERRASCALE_API_URL` | TerraScale API origin (default `https://api.terrascale.tech`); production auth routes all storage operations strictly to the GRU authority |
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

### Self-service authentication setup

An organization admin can open `/ui/{slug}/admin`, choose a provider, and enter
its client ID and secret. The provider form loads through a Datastar endpoint.
After saving, copy the connection's callback URL from the connections list into
the provider's app registration. Microsoft requires a single-tenant directory
ID; Google and custom OpenID Connect providers use their discovery documents.
GitHub uses its OAuth app flow and accepts only verified email addresses. The
client secret is encrypted at rest and never shown again. Connections are
scoped to the selected organization; only its admins can enable or disable
them. The hosted sign-in choices are at `/ui/{slug}/connections/list`.
SAML sign-in accepts one verified signature on the response or its sole
assertion under the configured profile. Starting another sign-in tab for the
same connection replaces the first flow's correlation cookie; complete the
latest flow.

Uniqueness of user-chosen identifiers (emails, usernames, org slugs,
credentials) is enforced with atomic claim documents: CreateOnly writes the
engine rejects on collision — race-safe without transactions. See
`docs/parity.md` for the full Clerk/WorkOS/Auth0 feature matrix.
An ambiguous TerraBase write can leave a uniqueness or quota claim reserved,
or commit an API key before its plaintext is returned. The service fails
closed; a timeout or compensation attempt does not prove that a delayed write
did not commit. Operators may need to reconcile orphaned claims and list or
revoke uncertain keys before retrying an operation.

New SCIM-created identities stay hidden as provisional, deleted users until
their directory link and required organization membership are durable. The
service then activates the user with a versioned write. A crash or ambiguous
activation response can leave a provisional user and reserved claims; repair
requires checking the owning directory, link, membership, and User version
before a conditional cleanup or activation. Do not clear the provisional marker
or release its claims based only on a failed request.

Instance-wide SCIM directories also control authentication: every associated
active directory must have an active link to the user. A missing or `Deleting`
directory denies authentication; a durably `Deleted` directory releases its
authority. SCIM User DELETE denies authentication until a fresh SCIM User POST
restores the link. Organization-scoped directories affect membership only, and
an admin ban remains an independent denial. Reactivation can make an existing,
unexpired session usable again; deactivation does not itself revoke sessions.

### Repairing a stranded SCIM userName claim

A directory-scoped SCIM rename or delete that fails after reserving a new
`userName` can leave the old or new name unavailable. Repair the claim through
the GRU storage authority only after placing that directory behind an
exclusive write fence on every auth replica and draining in-flight SCIM
requests. Pausing one upstream sync client is insufficient if another caller
can still write. A directory already in `Deleting` or `Deleted` status cannot
accept new SCIM writes, but do not start directory deletion solely to repair a
name. If an exclusive fence is unavailable, retain the claim.

For the affected directory and name, compute the claim key as
`{directory.id}:{trimmed-lowercase-userName}` (the service's
`users::normalize_email` rule). Its document ID in `uniques` is
`scim-user-name:` followed by the lowercase SHA-256 hex digest of that key.
Read the claim with its ETag and check `ownerType == "scim-user-name"`. Its
`ownerId` must be the expected `scim_user_links` ID,
`{directory.id}:{user.id}`. Inspect the current link and all links in that
directory: retain the claim if any link still uses that normalized name,
including an inactive link. For a link whose profile has not been initialized,
check the linked user's email as the effective `userName` before deciding.
Retain it as well if ownership, link state, or an in-flight operation cannot
be established unambiguously. Only for a proven unused claim with the expected
owner, delete that exact `uniques` document using the ETag as an `If-Match`
condition (the store's `compare_and_delete` operation). A failed conditional
delete means the state changed; reread and reassess rather than retrying an
unconditional delete. Verify the final claim and links before lifting the
write fence.

### Repairing a stranded SCIM cleanup owner

If SCIM User DELETE or directory deletion fails after taking a cleanup owner,
the `scim_user_links` row can remain with `deleting: true`,
`cleanupComplete: false`, and a non-null `cleanupOwner`. A retry returns a
conflict while that owner is present. These are the serialized
`ScimUserLink.cleanup_owner` and `cleanup_complete` fields; `incarnationId`
below is `incarnation_id` in Rust. Fence the affected directory against
writes on every auth replica, drain in-flight SCIM requests, and confirm the
logged `cleanupOwner` request has stopped. Read the link and its ETag from the
GRU authority. Verify its `instanceId`, `directoryId`, `userId`, and
`incarnationId` against the failed operation; a new incarnation or a completed
link must never be taken over. Reconcile the owning directory's membership
claim and membership, group members, and local `userName` claim against the
current link and any other active directory authority. Retain each child whose
ownership or write outcome is uncertain.

Only after the fence and child checks, clear `cleanupOwner` with a conditional
write against that exact link ETag, preserving `deleting: true`,
`cleanupComplete: false`, and `incarnationId`. If the conditional write loses,
reread and reassess; do not force the owner to null. Retry the original SCIM
User DELETE or directory deletion so the service finishes cleanup and marks
the retained link `cleanupComplete: true`. Verify that terminal link and its
children before lifting the fence. If the original owner may still write or
the incarnation cannot be proven, leave the owner in place and escalate
instead of starting another finisher.

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

The Rust service uses TerraBase's authenticated document API. Point
`TS_AUTH_TERRASCALE_API_URL` at an already running compatible local development
member with the document API and project-key gateway enabled, then set
`TS_AUTH_ENVIRONMENT=Development`. This repository does not start or embed a
TerraBase member.

Rust unit and router tests run with `mise exec -- cargo test --workspace
--all-targets --locked`.
`scripts/smoke-real-stack.sh` is a future real-backend check for a built Rust
service and separately running compatible member. It exits with a precondition
error while no member is reachable.

## Provisioning the global auth dataset

The Rust service starts with a newly provisioned dataset; it does not migrate
legacy identity or SCIM link documents.

The platform operator provisions ts-auth through the same database contract
used by customer projects — see `scripts/provision.sh`:

1. Create a platform `ts-auth` project with `GRU,SFO,FRA` placement and issue
   its project API key. Set `TS_AUTH_TERRASCALE_PROJECT_ID` and
   `TS_AUTH_TERRASCALE_API_KEY` for the provisioning process.
2. Create the `ts-auth` database with placement and residency lock
   `GRU,SFO,FRA`, using the GRU regional data endpoint. Provisioning is an internal
   operator action; the customer never selects a region for authentication.
3. Deploy the same image in each admitted region behind
   `auth.terrascale.tech`, using the same database, public origin, master key,
   audit signing and verification keys, and operator key on every replica.

All auth storage reads and writes use the GRU data-plane authority. This is
required for unique email and seat claims, session revocation, and token
rotation: TerraBase's cross-region replication is causal and does not make
conditional writes globally atomic. An unavailable GRU authority makes auth
storage unavailable until an explicitly fenced recovery promotes a new
authority; routing a replica to a stale regional copy is unsafe. The global
edge endpoint may route requests to any healthy auth process, but every such
process still uses that one storage authority. `scripts/provision.sh` records
the global placement and creates the database through the regional data API.

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
- Quota repair: `POST /v1/{slug}/billing/quota/reconcile` removes expired pending seat claims whose user is absent, then recounts durable owners and unresolved admissions. Active claims remain reserved. It returns examined/removed/retained claim counts and requires the instance's authenticated management key.

Outbound webhook signing secrets are envelope-encrypted with `TS_AUTH_MASTER_KEY` at rest and are returned only once when an endpoint is created.
Webhook delivery checkpoints are stored durably per instance, so a process restart resumes from the last successful tick and does not silently skip queued audit events.
Each endpoint/event delivery also has a durable outcome (`succeeded`, `pending`, or `dead`); dead deliveries are visible at `/v1/{slug}/webhooks/dead-letter` and can be replayed with the corresponding operator endpoint after remediation. Audit exports include their integrity digest and authenticity seal so downstream retention systems can verify them.
Audit exports carry integrity digests and authenticity seals. Keep the active and retained rotation keys available to verify historical exports after key rotation.
- Operator: `POST /v1/instances` (Bearer `TS_AUTH_OPERATOR_KEY`) requires an
  `Idempotency-Key` unique to each intentional instance bootstrap. Reuse the
  same key when retrying that request so its first key can be recovered.
- Liveness/readiness: `GET /healthz` and `GET /readyz` (`/readyz` checks TerraBase member health).
- Private metrics: `GET /metrics` (`X-TerraScale-Metrics-Key`), with bounded request, uptime, build, and readiness metrics.

## CI

`.forgejo/workflows/ci.yml` checks out a pinned `ts-rust` revision, checks
formatting, runs Cargo tests and a locked dependency advisory audit, and builds the Rust release binary into the
distroless `Containerfile`. Registry publishing is held until the Rust
feature inventory and real caller acceptance checks are complete.

The locked dependency audit ignores only `RUSTSEC-2023-0071` for `rsa`:
ts-auth currently uses RSA public-key signature verification for JWT,
WebAuthn, and SAML, while the advisory concerns private-key operations. The
advisory has no fixed release. Reevaluate and remove this exception before
adding any RSA private-key signing or decryption path; every other vulnerability
advisory remains a CI failure.
