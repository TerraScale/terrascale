# ts-auth

A hosted identity provider in the Clerk / Auth0 / WorkOS category, built on
TerraBase. The service is written in C# on .NET 10: ASP.NET Core with every
API declared through `TerraScale.Endpoints` (ts-libs), TerraBase Documents v1
storage through `ts-dotnet`, RazorSlices and Datastar for hosted pages, and
TUnit and Playwright integration tests.

> **Port status (epic TS-424).** ts-auth was rewritten from Rust to C#, and the
> Rust workspace has been removed (TS-442). The foundation is in place:
> configuration and production guards, the error envelope, health and
> readiness, OpenAPI and Scalar, the hosted-page shell, telemetry, the test
> harness and CI. TS-489 adds the Documents v1 storage boundary and real
> storage readiness, validated locally against the pinned binary. Live production
> activation remains gated (see provisioning below). TS-490 adds users,
> server-side sessions and email passcodes as internal services on that store.
> TS-491 adds an OpenID Connect provider on OpenIddict 7.7.1 for one issuer,
> `{TS_AUTH_PUBLIC_ORIGIN}/terrascale`, so ts-dashboard can sign in: the
> authorization code flow with S256 PKCE, rotating refresh tokens, discovery,
> JWKS, userinfo, RP-initiated logout and a hosted email-passcode sign-in page
> for existing accounts. Device flow, guest accounts, CORS and SAML are
> follow-up TS-495. The other identity features below are being ported stream
> by stream. Until a stream lands, its routes are not served.
> [`docs/parity.md`](docs/parity.md) tracks each capability, and
> [`docs/csharp-port.md`](docs/csharp-port.md) describes the layout, commands
> and intentional changes. The Rust implementation remains in Git history
> (before TS-442 / `8dd2874`) as the behavioural reference, including the
> original TS-428 storage design.

ts-auth owns identity and access for the whole platform (epic TS-597). The
contract for organizations, projects, roles, invitations, API keys, the token
claims and the tenant/issuer scheme is
[`docs/platform-contract.md`](docs/platform-contract.md), with
[`docs/platform-api.openapi.yaml`](docs/platform-api.openapi.yaml).

The consumer-app competitive review and remaining release gates are in
[`docs/competitive-review.md`](docs/competitive-review.md). The consumer
integration contract (guest accounts, browser origins, device authorization,
organization permission checks) is in
[`docs/consumer-integration.md`](docs/consumer-integration.md).

## Authentication rate limits (TS-704)

All token requests have a TerraBase-backed peer-IP admission limit before
client authentication (`TS_AUTH_RATE_LIMIT`, default 1,000 per minute).
Hosted and headless sign-in share IP budgets and normalized-email code
budgets; password reset has a separate redemption budget. Exhaustion returns
429 and `Retry-After`; unavailable storage returns 503. Unknown emails spend
the same budget as accounts, and replacing a code does not reset it. Code
request and issuance budgets both reset on UTC clock-hour boundaries. Socket
peer addresses are used for hosted/general token limits by default. Optional
`TS_AUTH_TRUSTED_PROXIES` is a semicolon-separated list of at most eight exact
numeric proxy IPs; only these peers can supply the rightmost `X-Forwarded-For`
hop. Hosted guards use that client IP; token requests retain an aggregate
socket budget (four times the normal limit for configured proxies) and also
charge a separate client budget before authentication. Missing or malformed
rightmost addresses share the proxy's client budget. Other forwarding headers
cannot change identity, origin or scheme.
An account-budget refusal temporarily blocks that method until its window ends
and queues one lockout email notice per account, method and stored deadline.
Other sign-in and recovery budgets remain separate. Notice claims live in
TerraBase; delivery uses a separate bounded notice queue and worker, so notice
floods cannot displace sign-in or recovery mail. Delivery remains best-effort,
and notices whose lockout has expired before sending are suppressed.
See [the budget table](docs/csharp-port.md#limits). Production forwarding remains
disabled until its actual socket peers and rightmost client-IP header are
verified; see [the ingress checklist](docs/csharp-port.md#trusted-proxy-ingress).

The internal TOTP and recovery-code credential layer, atomic authorization
composition, and primary-only authentication guards (TS-707), deployment
prerequisite, and remaining dashboard MFA slices are documented in
[`docs/mfa.md`](docs/mfa.md). Active MFA accounts are refused by existing user
sign-in and online authorization paths; public enrollment and MFA sign-in
endpoints are not served yet.

## Target feature inventory

| Area | Target capability |
| --- | --- |
| Users | Email/password, usernames, profile + public/private metadata, blocking, soft delete, impersonation |
| Sessions | Server-side sessions, httpOnly cookie, device list, revoke one/all, 14-day TTL |
| Consumer guest accounts | Opt-in anonymous OAuth sign-in, verified email conversion preserving user ID/data, credential invalidation and quota accounting |
| Browser clients | Exact-origin OAuth CORS, editable client origins/redirects and guest admission settings |
| Passkeys | Full WebAuthn: registration (none + packed attestation), discoverable-credential sign-in, sign-count clone detection |
| OAuth 2.0 / OIDC | Authorization code + PKCE (S256), refresh rotation with reuse detection, client credentials, RFC 8628 device authorization with Datastar approval, discovery, JWKS, userinfo, introspection, revocation, RP-initiated logout; ES256 id/access tokens |
| MFA | TOTP (RFC 6238) enrollment + verification, recovery codes, step-up during sign-in |
| Email flows | Verification, password reset, magic links, email passcodes (pluggable email sender; logs by default) |
| Social SSO | Upstream OIDC connections (Microsoft, Google, any issuer) with JWKS-verified ID tokens; GitHub OAuth with verified email lookup |
| Enterprise SSO | SAML 2.0 connections (HTTP-Redirect AuthnRequest, XML-DSig-verified assertions) |
| Directory sync | SCIM 2.0 `/scim/{dir}` — users, groups, deprovisioning |
| Organizations and projects | The platform's organizations → projects, organization and project roles, invitations, ownership transfer, audit, and API keys exchanged for short-lived JWTs (TS-598, [`docs/platform-contract.md`](docs/platform-contract.md)); one issuer per customer project for its end users |
| Admin portal | Dashboard-styled hosted page for org admins to configure GitHub, Microsoft, Google, custom OIDC and SAML connections, and view directories |
| Management API | Full developer REST API with `sk_...` keys, per-family key scopes, pagination, rate limiting |
| JWT templates | Custom-claim tokens (`{{user.email}}` substitution) minted on demand |
| Webhooks | HMAC-signed outbound events with retries, durable delivery status, dead-letter listing, and operator replay |
| Audit logs | Signed audit events in production, with query and verification APIs |


Target multi-instance design: one ts-auth deployment hosts many customer
instances; every instance gets its own users, keys, orgs, and OAuth clients,
isolated by instance-scoped keys. The public issuer and hosted UI use one global origin;
customers do not choose an authentication region. All auth replicas must use
one configured TerraBase authority. The pinned server does not implement
replication or regional failover; production activation remains gated on its
durability/recovery limits described below. Project and customer database
placement remain separate choices.


Scalar is served at `/scalar` and the JSON contract at `/openapi/v1.json`.
Both are generated from the endpoints the running service actually maps.

## Layout

```text
src/TerraScale.Auth/            # ASP.NET Core host; Features/<Area>/ endpoints, services and Razor slices; Assets/
src/TerraScale.Auth.Contracts/  # persisted models and the {"error":{…}} envelope
src/TerraScale.Auth.Security/   # Argon2id passwords, the master-key secret box, ES256 keys and JWTs, tokens and digests
src/TerraScale.Auth.Store/      # Documents v1 persistence/readiness on ts-dotnet (TS-489)
tests/TerraScale.Auth.Fast/     # blocking integration suite
tests/TerraScale.Auth.Slow/     # non-blocking integration suite
tests/Shared/                   # harness source linked into both suites
scripts/                        # dev loop, OpenAPI export, CI, provisioning and real-stack smoke checks
```

Persistent uploaded files, if a feature needs them, belong in RustFS through
its S3 API. Packaged fonts and static assets are embedded in the executable.

## Running

The build needs the .NET 10 SDK (see `global.json`) and the sibling `ts-dotnet`
and `ts-libs` checkouts at the revisions pinned in `.forgejo/workflows/ci.yml`.
They are looked up at `../ts-dotnet` and `../ts-libs`, or under
`~/Projects/TerraScale/`; pass `-p:TerraScaleRoot=<dir>` or set the
`TerraScaleRoot` environment variable to use another location. Both workflows
pin ts-dotnet to SDK merge #24, `bdea1e8f4926e46367b751d8f633dcec04d760e9`.
Storage integration tests require the real SDK-pinned Linux x64 TerraBase
binary locally and in CI; unavailable prerequisites fail instead of skipping.
See [the port guide](docs/csharp-port.md) for the binary tag, SHA-256 and
download/cache settings. Run local commands at low CPU priority:

```sh
nice -n 19 make check      # format, build (analyzers as errors), NuGet audit, OpenAPI lint, fast suite
nice -n 19 make test-slow  # slow suite
nice -n 19 make dev        # dotnet watch, published over HTTPS on the tailnet for this worktree
```

Signing keys live in one OCI Vault secret per region and rotate
automatically: each replica bootstraps the serving ring at startup (replacing
one it cannot verify, which signs everyone out) and publishes a new key every
30 days, two days before it signs. See
[bootstrap and automatic rotation](docs/csharp-port.md#bootstrap-and-automatic-rotation-ts-703).

The service needs an explicitly configured TerraBase HTTPS origin, an
organization-bound Documents API key and an existing database. Resolve the
`ts-auth` project UUID through Management before deployment; this example expects
that UUID in `AUTH_PROJECT_ID` and uses synthetic credential placeholders:

```sh
TS_AUTH_PUBLIC_ORIGIN=https://auth.terrascale.tech \
TS_AUTH_TERRASCALE_API_URL="${AUTH_STORAGE_ORIGIN:?set the resolved HTTPS authority}" \
TS_AUTH_TERRASCALE_API_KEY=tb_... \
TS_AUTH_DATABASE="${AUTH_PROJECT_ID:?set the ts-auth project UUID}/auth" \
TS_AUTH_MASTER_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_AUDIT_SIGNING_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_OPERATOR_KEY=$(head -c 32 /dev/urandom | base64) \
nice -n 19 dotnet run --project src/TerraScale.Auth
```

| Variable | Meaning |
| --- | --- |
| `TS_AUTH_PUBLIC_ORIGIN` | One global public origin shared by every replica (issuer URLs, email links, WebAuthn RP, OpenAPI server). Must be an origin without a path |
| `TS_AUTH_TERRASCALE_API_URL` | Required HTTPS origin, no default; no credentials, path, query or fragment. Host must be `api.terrascale.tech` or under `.api.terrascale.tech`; loopback HTTPS is permitted only in Development. Explicit static endpoint override |
| `TS_AUTH_TERRASCALE_API_ENDPOINTS` | Optional private TerraBase member origins, semicolon-separated, most preferred first: at most 8 distinct HTTPS origins without credentials, path, query or fragment, for example `https://10.251.0.21:9465;https://10.251.0.22:9465`. Storage then uses them instead of `TS_AUTH_TERRASCALE_API_URL`. One member pins storage to it: storage fails closed while it is down. Several members need `TS_AUTH_TERRASCALE_API_FAILOVER=true`; every replica then sends to the first ready member, so all replicas use the same one. Each member's `/readyz` is probed every 2 s (ready below HTTP 500); a request that cannot resolve, connect (connect timeout 2 s) or complete TLS marks its member down at once, and the next request goes to the next ready member. A failed request is never resent. Requires `TS_AUTH_TERRASCALE_API_CA` |
| `TS_AUTH_TERRASCALE_API_CA` | The only roots trusted for `TS_AUTH_TERRASCALE_API_ENDPOINTS`: 1 to 4 PEM certificates and nothing else, for example the cell's TerraBase member CA. The member certificate must still name the endpoint's host (an IP SAN for an IP endpoint) and carry serverAuth; revocation is not checked. Requires `TS_AUTH_TERRASCALE_API_ENDPOINTS` |
| `TS_AUTH_TERRASCALE_API_FAILOVER` | `false` (default) or `true`. **Documented security exception** to the single storage authority (see the [activation gate](docs/csharp-port.md#production-activation-gate)): `true` lets storage move to the next of several `TS_AUTH_TERRASCALE_API_ENDPOINTS` while the preferred member is down, and back once it is ready. The member that takes over may not have replicated the latest writes yet, so within the members' replication lag a read can miss them and a conditional write there can succeed against older state (a consumed challenge, a uniqueness claim or a refresh-token rotation). Set it only for members that replicate, accepting that window, instead of an outage while the preferred member is down |
| `TS_AUTH_TERRASCALE_API_KEY` | Organization-bound `tb_…` credential with `documents:read` and `documents:write`, restricted to the ts-auth project/database where appropriate. A leading `Bearer` scheme is stripped; runtime uses no management credential |
| `TS_AUTH_DATABASE` | Required `project-uuid/database`, e.g. `<ts-auth-project-uuid>/auth`; lowercase hyphenated UUID and lowercase database name (at most 63 characters). Project labels are not accepted |
| `TS_AUTH_MASTER_KEY` | Base64, 32 bytes; encrypts recoverable secrets (signing keys included) with AES-256-GCM. There is no re-encryption yet: changing it makes every sealed secret unreadable |
| `TS_AUTH_SIGNING_KEY_VAULT` / `TS_AUTH_SIGNING_KEY_SECRET` | The region-local OCI Vault OCID and the pre-created secret holding the signing key ring, e.g. `gru-ts-auth-signing-key-ring` (initial content `{}`). Every bootstrap and rotation writes a new secret version. Required outside Development, together with `TS_AUTH_OCI_*` |
| `TS_AUTH_OCI_REGION` / `_TENANCY` / `_USER` / `_FINGERPRINT` / `_PRIVATE_KEY` | The OCI API key that reads and versions only that secret: region (must be the vault's), tenancy and user OCIDs, key fingerprint and the RSA private key PEM (a Vault-delivered secret). `TS_AUTH_OCI_ENDPOINT` overrides the API origin for local test doubles only |
| `TS_AUTH_AUDIT_SIGNING_KEY` | Base64 32-byte HMAC key for audit-event seals (required outside Development) |
| `TS_AUTH_AUDIT_VERIFY_KEYS` | Optional semicolon-separated previous audit keys (at most 5), kept during rotation |
| `TS_AUTH_OPERATOR_KEY` | Bearer key that can create instances (`POST /v1/instances`) |
| `TS_AUTH_LISTEN` | Plain-HTTP listen address with an explicit port (default `http://0.0.0.0:8080`) |
| `TS_AUTH_ENVIRONMENT` | `Development` waives the audit-signing key requirement and permits a loopback HTTPS TerraBase API origin |
| `TS_AUTH_RATE_LIMIT` | Management API requests per key and token requests per peer IP per minute, with separate budgets (default 1000) |
| `TS_AUTH_TRUSTED_PROXIES` | Optional semicolon-separated list of at most eight exact numeric immediate proxy IPs. Only their rightmost `X-Forwarded-For` entry establishes a client address; requires the documented ingress verification before activation |
| `TS_AUTH_SMTP_HOST` / `_PORT` / `_USERNAME` / `_PASSWORD` / `_FROM_ADDRESS` | SMTP delivery; host, username, password and from address are all or nothing, port defaults to 587 |
| `TS_AUTH_SMTP_CHECK_REVOCATION` | `true` (default) or `false`: whether the relay certificate's revocation is checked online; see [Email delivery](#email-delivery-oci) |
| `TS_AUTH_DASHBOARD_CLIENT_SECRET` | Optional secret of the confidential OIDC client `ts-dashboard`, 32 to 512 characters without whitespace. Required together with the redirect URIs; only OpenIddict's PBKDF2 hash is stored |
| `TS_AUTH_DASHBOARD_REDIRECT_URIS` | Semicolon-separated exact callback URIs for `ts-dashboard` (at most 10), e.g. `https://dash.terrascale.tech/auth/callback;http://localhost:5173/auth/callback`. HTTPS, or HTTP only for `localhost` or a loopback IP; no credentials or fragment |
| `TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS` | Optional semicolon-separated exact post-logout URIs for `ts-dashboard`, with the same rules |
| `TS_AUTH_ACCESS_TOKEN_AUDIENCES` | Optional semicolon-separated `aud` values put in every access token, kept exactly as written (e.g. `https://gru.api.terrascale.tech`, TerraBase's `auth-audience`); at most 10 absolute HTTPS URIs without credentials, query or fragment |
| `TS_AUTH_SERVICE_CLIENTS` | Optional semicolon-separated `id:secret` service clients allowed only the client-credentials grant; ids are 3 to 64 lowercase letters, digits or `-`, start with a letter, are not UUIDs or `ts-dashboard`; secrets 32 to 512 characters. Registered at startup like `ts-dashboard` |
| `TS_AUTH_INVITATION_URL` | Optional ts-dashboard page that accepts platform invitations (TS-598), e.g. `https://dash.terrascale.tech/invitations/accept`; invitation emails link to it with `?token=`. HTTPS (HTTP only for loopback in Development), no credentials, query or fragment. Unset, no invitation email is sent and the caller delivers the token |
| `TS_AUTH_SENTRY_DSN` | GlitchTip/Sentry DSN for error reporting; a deployment secret, never committed. Off when unset |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (or the per-signal `OTEL_EXPORTER_OTLP_*_ENDPOINT`), `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_HEADERS` | Standard OpenTelemetry settings; traces, metrics and logs are pushed over OTLP only when an endpoint is set |

The intended production dataset is `ts-auth/auth` in organization
`terrascale`: project label `ts-auth`, database name `auth`. Documents
routes require the project's UUID, so runtime configuration must use
`<ts-auth-project-uuid>/auth`. The credential selects the organization; separate
project/tenant selector options have been removed. `App.Build` injects
`ITerraBaseEndpointResolver` with a `TerraBaseStaticEndpointResolver` for the
configured origin. No GRU endpoint is hardcoded in client routing. Atlas
discovery in ts-dotnet is TS-507; its resolver can replace this static override.

`GET /metrics` and `TS_AUTH_METRICS_KEY` from the Rust service are gone:
metrics are pushed over OTLP. Telemetry never carries exception messages,
request header values, query strings, bodies or credentials. See
`docs/csharp-port.md` for the exact allowlists.

Every setting can also be passed as `--name=value` (for example
`--public-origin=https://…`). Configuration errors name the setting, never its
value, and stop startup with exit code 2. Before serving, the process checks
Argon2id against a reference vector; if the bundled libsodium cannot load,
startup stops with exit code 3 (if it cannot even be extracted, see
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` below, the .NET host stops first). Outside
Development, a missing `TS_AUTH_SMTP_*` configuration logs a startup warning.

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
SAML sign-in accepts exactly one verified signature, on the response or its
sole assertion. By default the assertion itself must be signed. An IdP that
signs only the enclosing response needs an explicit per-connection opt-in:
`allow_unsigned_assertions: true` on `POST` or `PATCH
/v1/{slug}/connections[/{id}]` (SAML connections only). The response signature
is still required then. Connections stored before this setting existed read as
`false`, so they require signed assertions. Starting another sign-in tab for the
same connection replaces the first flow's correlation cookie; complete the
latest flow. Validated SAML assertions also create durable replay claims, so a
duplicate assertion fails closed. Each claim records its tenant and an expiry:
the earliest `NotOnOrAfter` bound of the assertion plus a five-minute clock-skew
margin. After each successful SAML sign-in, the service deletes up to 32 expired
claims for that instance, within a 1.5-second budget that never delays the
sign-in further. Each delete is version-conditional. Claims created
before expiries were recorded have no expiry, so cleanup never removes them.

The paragraphs above describe the target admin portal and federation surface,
which is not ported yet.

The TS-491 OAuth provider accepts `openid`, `email`, `profile` and
`offline_access`; each client's scope permissions bound what it may request.
`profile` adds no claims yet. Access tokens follow TerraBase decision 0007
(TS-519): signed, unencrypted ES256 JWTs with `typ` `at+jwt`, `aud` holding
every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value, and a user's `email` and
`email_verified` whatever the scopes. Identity tokens keep `typ` `JWT` and
`aud` = the client id, and carry the email claims only with the `email` scope,
as userinfo does. The user-facing client is the confidential `ts-dashboard`,
registered from `TS_AUTH_DASHBOARD_*`. It authenticates at the token endpoint
with `client_secret_basic` or `client_secret_post`, must use PKCE with S256
(`plain` is disabled for every client), and gets implicit consent. It is also the
only client allowed the TS-582 headless API: its own sign-up, password,
email-code and password-reset pages call ts-auth server-to-server and receive
the same tokens ([port guide](docs/csharp-port.md#headless-api-ts-582)). Service
clients from `TS_AUTH_SERVICE_CLIENTS` may use only the client-credentials
grant: their access token has `sub` = the client id, the same `typ` and `aud`,
no scopes and no email, and they get no identity or refresh token. Authorization requests must exactly match a registered redirect URI;
the configured URIs use HTTPS except `localhost` or a loopback IP over HTTP,
without a fragment or credentials. Public clients and self-service client
registration are target behaviour.
`prompt=none` uses only an active session and returns `login_required` when none
exists. Forced reauthentication, consent, and account selection are unsupported:
`prompt=login`, `prompt=consent`, and `prompt=select_account` return
`login_required`, `consent_required`, and `account_selection_required`. A
`max_age` that the session has exceeded returns `login_required` instead of
forcing a new sign-in. OpenIddict validates the `prompt` parameter first; the
endpoint then checks `login`, `consent` and `select_account` in that order, so
`prompt=login consent` returns `login_required`. The Rust service answered it
with `interaction_required`; that is not ported.

Target behaviour: upstream OIDC connections request `openid email profile` by
default. A custom
OIDC scope list must contain `openid` and `email`; valid configured scopes are
stored on the connection and sent to the provider.

The TS-489 storage boundary provides create-only uniqueness claims and
owner-checked CAS release. Addresses are deterministic UUIDv8 values from
SHA-256 over length-framed instance/kind/key inputs; new document IDs must be
UUIDs. Email keys use trim and invariant lowercase. Generic feature writes
are create-only or version-conditional and reject infrastructure collection
names. Single-use challenges persist only hashes, check instance/purpose/
subject/expiry, and retain their CAS-consumed record to reject replay and
reissue. These primitives do not implement the identity flows above. See
`docs/parity.md` for capability status.

An ambiguous TerraBase write can leave a uniqueness claim reserved. A timeout
or failed immediate read does not prove that a delayed write did not commit.
Keep the claim reserved; never compensate by releasing it from the failed
request. Any manual repair must first quiesce the relevant writers, verify
the claim and its expected owner, and use an owner-checked conditional release.
If the state is uncertain, retain the claim. The SDK does not automatically
retry writes.

Management mutations and audit writes are separate operations. User
create/update/delete, instance update, and API-key revocation return success
after the mutation commits even if audit persistence then fails; that failure
is logged, and the mutation and audit event are not atomic. API-key creation
and impersonation fail closed on audit errors: the new key or token is withheld
and cleanup is attempted, though a second storage failure can prevent cleanup
from being confirmed.

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
authority. Directory DELETE can return `503` while child cleanup is changing;
retry it to rerun group and link sweeps, even after the directory is `Deleted`,
so late in-flight writes can be cleaned up. SCIM User DELETE denies
authentication until a fresh SCIM User POST restores the link.
Organization-scoped directories affect membership only, and an admin ban
remains an independent denial. Reactivation can make an existing, unexpired
session usable again; deactivation does not itself revoke sessions.

### Email delivery (OCI)

Without `TS_AUTH_SMTP_*` configuration ts-auth uses the logging sender: nothing
is delivered, and each message logs a warning with its sizes only, never the
recipient or the content (the dev/test default). With SMTP configured, STARTTLS
and a valid server certificate (chain and host name) are mandatory; a relay that
offers no TLS never receives the credentials or the message. The certificate's
revocation is checked online by default. A deployment that cannot reach its
CA's OCSP and CRL servers sets `TS_AUTH_SMTP_CHECK_REVOCATION=false` as a
documented security exception: a revoked relay certificate that still chains
to a trusted CA and matches the host is then accepted. TerraScale's gru-1 cell
reaches OCI's relay through the OCI service gateway without internet egress
and sets it (ts-orchestrator `deploy/oci-region/ts-auth-gru-1-profile.json`). Delivery failures are logged and never
fail the request that sent the mail. For real delivery via OCI
Email Delivery, set the five variables above; fetch the credentials from the
ts-infra state:

```sh
cd ts-infra && export TF_VAR_state_passphrase=...
tofu output -raw smtp_username   # full string incl. the @oci suffix
tofu output -raw smtp_password
```

Every email is a `multipart/alternative` message: a plain-text part and a
responsive HTML part (`Features/Email/AuthEmails.cs` and
`AuthEmailLayout.cshtml`) that follows the reader's light or dark mode, keeps
the code large and selectable, states the expiry and ends with a "didn't
request this?" note. Copy exists for the sign-in code, magic link, email
verification, password reset, invitation, MFA code and new-device alert, in
English and Brazilian Portuguese; the hosted sign-in page picks the locale from
`Accept-Language`. The copy takes `{{ name }}` placeholders only (no logic), and
the layout HTML-encodes every value. The logos come from
`/admin/email-logo.png` and `/admin/email-logo-dark.png` on the public origin,
so mail clients must be able to reach it.

### Email branding and templates API (TS-583)

`/v1/{slug}/email/*` lets ts-dashboard change the branding and copy of every
auth email; Scalar documents each operation. Only the `ts-dashboard` client
may call it, with HTTP Basic credentials (`ts-dashboard` and
`TS_AUTH_DASHBOARD_CLIENT_SECRET`, compared in constant time); the dashboard
authorizes its own user first and reports them as `actor`. Only the
`terrascale` instance exists; per-organization branding waits for
organizations (TS-437).

- Branding: name, logo (light and dark, with its size for Outlook) or icon,
  primary and accent colours, theme (`auto`, `light`, `dark`), button style,
  footer text, support address or page, website, sender display name and
  Reply-To. The From address stays `TS_AUTH_SMTP_FROM_ADDRESS`. Button text
  and link colours are chosen to keep 4.5:1 contrast.
- Copy: subject, preview line, heading, body, button and footnote per kind and
  locale. Fields are plain text with the kind's `{{ name }}` placeholders and
  nothing else; everything is HTML-encoded. The code, button, expiry line and
  footer always come from the layout.
- Every save of branding or a template writes a new version, conditional on
  the version the caller read (409 otherwise), together with its immutable
  history entry in one TerraBase transaction (`email_configs`,
  `email_config_versions`). A restore saves an earlier version again as the
  newest; history is never rewritten.
- Preview renders drafts with synthetic values; a test send mails one sample
  with a `[Test]` subject, at most 10 per instance per minute (`rate_limits`).
- Every email reads the configuration when it is sent, without a cache, so a
  change applies to the next email on every replica. If it cannot be read,
  the sign-in code still goes out with the defaults and a warning is logged.

DNS prerequisites (terrascale.tech is hosted at Purelymail — not editable from
this repo):

- SPF: the domain TXT record must include `include:rp.oracleemaildelivery.com`
  (Americas) or deliverability suffers.
- DKIM: the OCI managed-DKIM records are output by ts-infra once applied;
  publish the CNAMEs in the zone. DKIM stays `PENDING` until they resolve.

### Local development against a real TerraBase member

Point `TS_AUTH_TERRASCALE_API_URL` at an already running compatible local
member's HTTPS origin and set `TS_AUTH_ENVIRONMENT=Development`, a valid
`tb_…` key and `TS_AUTH_DATABASE=<project-uuid>/<database-name>`. The application
does not start or embed TerraBase; the integration-test fixture starts the
SDK-pinned binary. `/readyz` checks the existing `uniques`,
`verification_tokens`, `storage_health`, `users`, `sessions`, `passcodes`,
`signing_key_rings`, `oidc_applications`, `oidc_authorizations`,
`oidc_tokens`, `rate_limits`, `email_configs`, `email_config_versions`,
`organizations`, `memberships`, `projects`, `project_members`, `api_keys`,
`invitations`, `audit_events` and `mfa_credentials` collections, then creates, reads
and conditionally deletes a synthetic document in `storage_health`. It returns
200 only on success, otherwise 503, without provisioning, repair or in-memory
fallback. Observations coalesce and are cached for one second after
completion, with a two-second deadline; unknown `region` is omitted.

The broader `scripts/smoke-real-stack.sh` still targets Rust identity flows
that later streams have not implemented; it is not evidence of TS-489 or
TS-491 acceptance.

## Provisioning the global auth dataset

The application and readiness probe never provision or repair state, and
there is no migration of Rust documents. `scripts/provision.sh` prints only
the provisioning plan; live production execution belongs to TS-470:

1. Resolve organization `terrascale` and project label `ts-auth` to their
   IDs through Management before deployment; keep management credentials out
   of the auth runtime.
2. Provision the `auth` database and a `tb_…` credential restricted to the
   auth project/database with `documents:read` and `documents:write`.
3. Explicitly call `AuthStore.ProvisionAsync` with the pinned SDK against that
   existing database. It creates `uniques`, `verification_tokens` and
   `storage_health`, the TS-490 `users`, `sessions` and `passcodes`, the
   TS-488 `signing_key_rings` and `rate_limits`, the TS-491 `oidc_applications`,
   `oidc_authorizations` and `oidc_tokens`, the TS-583 `email_configs` and
   `email_config_versions`, and the TS-598 `organizations`, `memberships`,
   `projects`, `project_members`, `api_keys`, `invitations`, `audit_events`
   and the TS-707 `mfa_credentials`
   collections; later feature streams own their other collections. `/readyz`
   requires all twenty-one, so a
   database provisioned before a release that adds one (TS-583, TS-598, TS-707)
   reports 503 until this step is repeated. Run it before rolling out a release
   that adds collections; a configured dashboard or service client also stops
   startup while its collection is missing.
4. Configure every replica with `<ts-auth-project-uuid>/auth` and the same
   explicitly resolved HTTPS storage authority and shared auth settings.

**Live production activation remains gated.** The pinned server uses SQLite
WAL with `synchronous=NORMAL` (TerraBase decision 0003): a process crash alone
preserves committed writes, but an OS crash or power loss can lose acknowledged
PUT/DELETE transactions since the last WAL sync. That can roll back uniqueness
claims and consumed challenges. Decision 0005 states that replication `Sync`
is an unsupported bootstrap stub, so no regional replication or failover is
available. All replicas must use one authority, with no automatic retries or
failover to another copy. Production activation requires an approved
durability/recovery design and TS-470 verification; `/readyz` and local tests
do not prove production durability. [The port guide](docs/csharp-port.md#production-activation-gate)
links the exact pinned decisions. No live production validation is claimed.

## API surface

Currently served: `GET /healthz`, `GET /readyz`, `GET /openapi/v1.json`,
`GET /scalar`, `GET /admin/{asset}` and `GET /fonts/{asset}`, plus the TS-491
OpenID Connect routes for the slug `terrascale` (other slugs answer 404):

- `GET /terrascale/.well-known/openid-configuration` and
  `GET /terrascale/.well-known/jwks.json`
- `GET /terrascale/oauth/authorize`, `POST /terrascale/oauth/token`,
  `GET /terrascale/oauth/userinfo`, `GET /terrascale/oauth/logout`
  (RP-initiated logout) and `POST /terrascale/oauth/revoke` (RFC 7009, TS-582)
- `GET` and `POST /ui/terrascale/sign-in`, `POST /ui/terrascale/sign-in/verify`
  (hosted email-passcode sign-in)
- The TS-582 headless API for ts-dashboard: `POST
  /terrascale/headless/v1/email-code/start`, `/sign-up`,
  `/password-reset/start` and `/password-reset/complete`, plus the token
  endpoint's headless grants (see [the port guide](docs/csharp-port.md#headless-api-ts-582))

TS-631: send the original sign-up `pending_token` to `email-code/start` to
resend its verification code without choosing another password or token. All
headless mail-start routes share a persisted 60-second cooldown per normalized
email, including unknown addresses; earlier repeats answer 429 with
`Retry-After` for the remaining cooldown. The five-code hourly request budget
is shared with hosted sign-in and applies to unknown emails too.

All of them appear in `/openapi/v1.json` under the `OAuth`, `Sign-in` and
`Headless` tags. Introspection and device authorization are not served. The
target surface below is the contract the
port streams restore (from the Rust service); each stream keeps its paths,
methods and operation ids. [The port guide](docs/csharp-port.md#openid-connect-ts-491)
describes the TS-491 behaviour and its limits.


- Hosted UI: `/ui/S/sign-in`, `/sign-up`, `/account`, `/account/sessions`,
  `/forgot-password`, `/reset-password`, `/verify-email`, `/magic-link`,
  `/passcode`, `/mfa/*`, `/passkeys/*`, `/connections/*`, `/admin`,
  `/device`, `/invitations/{id}/accept`
- OAuth/OIDC: `/S/.well-known/openid-configuration`, `/S/.well-known/jwks.json`,
  `/S/oauth/{authorize,token,userinfo,revoke,introspect,logout,device_authorization}`,
  `/S/oauth/anonymous`, `/S/oauth/anonymous/upgrade{,/complete}`
- SCIM: `/scim/{directoryId}/Users`, `/Groups`, `/ServiceProviderConfig`,
  `/ResourceTypes`, `/Schemas`
- Management (Bearer `sk_...`): `/v1/S/users*`, `/v1/S/sessions*`,
  `/v1/S/api-keys*`, `/v1/S/oauth-clients*`, `/v1/S/connections*`,
  `/v1/S/directories*`, `/v1/S/organizations*` (incl. org `domains*`),
  `/v1/S/domains/{domain}/enroll`, `/v1/S/jwt-templates*`, `/v1/S/webhooks*`,
  `/v1/S/audit-events`, `/v1/S/audit-events/verify`, `/v1/S/roles*`,
  `/v1/S/authorization/check`, `/v1/S/instance`
- Platform management (the platform's organizations, projects, roles,
  invitations and API keys, with the user's own sign-in token):
  `/v1/terrascale/…`, specified in [`docs/platform-contract.md`](docs/platform-contract.md)

Billing, plans and seat quotas are not ts-auth's: ts-dashboard owns them
(TS-597).

Outbound webhook signing secrets are envelope-encrypted with `TS_AUTH_MASTER_KEY` at rest and are returned only once when an endpoint is created.
Webhook delivery checkpoints are stored durably per instance, so a process restart resumes from the last successful tick and does not silently skip queued audit events.
Each endpoint/event delivery also has a durable outcome (`succeeded`, `pending`, or
`dead`); dead deliveries are visible at `/v1/{slug}/webhooks/dead-letter` and can
be replayed with the corresponding operator endpoint after remediation. Dispatch
scans each tenant's pending audit events and pending deliveries, so work grows
with that tenant's pending backlog. Due-time filtering happens after the scoped
query because the current query contract cannot select only due deliveries.
Audit events and dead letters use tenant-scoped cursor pages: list and
dead-letter pages return at most 100 rows, while an audit-verification page
checks at most 1,000 events. Cursors are opaque; audit list order follows the
storage provider and is not guaranteed chronological. Verification covers only
the events in the returned page and does not establish global completeness or
detect deleted or omitted events. Audit exports include integrity digests and
authenticity seals so downstream retention systems can verify them; keep active
and retained rotation keys available to verify historical exports after key
rotation.
- Operator: `POST /v1/instances` (Bearer `TS_AUTH_OPERATOR_KEY`) requires an
  `Idempotency-Key` unique to each intentional instance bootstrap. Reuse the
  same key when retrying that request so its first key can be recovered.
- Liveness/readiness: `GET /healthz` is process liveness; `GET /readyz`
  checks the configured database, required collections and synthetic
  create/read/conditional-delete access through Documents v1, this
  replica's startup signing-ring bootstrap and the ring's public metadata. Results are
  coalesced and cached per process for one second after completion, with a
  two-second deadline. Both JSON bodies include the .NET service `version`;
  readiness omits `region` when unknown. Metrics are pushed over OTLP;
  `GET /metrics` is not served.

## CI

`.forgejo/workflows/ci.yml` runs two jobs in parallel.
- **`fast`** is the required gate. It checks out the pinned ts-dotnet and
  ts-libs revisions into a job-owned directory, then runs a locked restore,
  `dotnet format --verify-no-changes`, a Release build with analyzers as
  errors, the NuGet vulnerability audit, the Redocly lint of the served OpenAPI
  document, the image-transfer, binary-publish and arm64-smoke guard tests and the fast integration suite,
  including Chromium and storage/readiness against the real SDK-pinned
  TerraBase binary. Missing binary/download prerequisites fail the suite.
  Fast cases share one TerraBase process and run at most two at a time;
  concurrency tests still create their own parallel requests.
- **`release`** publishes a self-contained single-file linux-x64 executable
  (`scripts/ci/stage-release.sh`), packages it into the chiseled
  `runtime-deps` image (`Containerfile`, pinned by digest), and smoke-checks
  `/healthz` (revision), `/openapi/v1.json`, fail-closed `/readyz` and the
  absence of `/metrics` (`scripts/ci/package-image.sh`). It also publishes
  the linux-arm64 single-file executable, so every pull request proves that
  build. `stage-release.sh` checks the ELF machine of each build, and
  `scripts/ci/smoke-arm64.sh` starts the arm64 executable under qemu-aarch64 on
  the arm64 variant of the pinned runtime-deps image, requiring `/healthz` with
  this revision and a fail-closed `/readyz` (its startup Argon2id self-test
  proves the bundled libsodium loads on arm64).

On pushes to `main`, `release` stages the smoke-checked image in
`git.terrascale.tech/<owner>/ts-auth-ci` and passes its registry manifest
digest to `publish` (`scripts/ci/transfer-image.sh`). Once both jobs pass,
`publish` tags that exact image as `git.terrascale.tech/<owner>/ts-auth:<commit>`.
The daily `registry-retention.yml` workflow removes staging candidates older
than 30 days.

On pushes to `main`, once `fast` and `release` pass, `publish-binary`
(`scripts/ci/publish-binary.sh`) attaches `ts-auth-linux-arm64` and
`ts-auth-linux-arm64.sha256` to the Forgejo release `v<version>-dev.<commit>`,
which ts-orchestrator downloads to run ts-auth natively. The exact bytes are
smoke-checked under emulation before any upload. New uploads succeed only
once both downloads match the smoke-checked executable and its checksum byte for byte;
a rerun leaves an existing internally verified release
untouched and replaces both assets of one an interrupted run left incomplete
(`scripts/check-binary-publish.sh` covers these paths in `fast`). Download
both assets into one directory and verify with
`sha256sum -c ts-auth-linux-arm64.sha256`; the checksum detects a corrupt or
mismatched download, it is not a signature.

Then `deploy` (TS-610, `scripts/ci/deploy.sh`) hands that release to
ts-orchestrator while the repository variable `TS_AUTO_DEPLOY_CELLS` names the
cells, canary first. `ts-orch release service-deploy` (the ts-orchestrator CI
build pinned by `TS_ORCH_RELEASE` and its `TS_ORCH_SHA256`) signs a release claim
with the scoped release key it reads from OCI Vault, publishes it with the
binary in each region's head bucket and waits until that region's
orchestrator reports the replicas serving it; the orchestrator gates the
rollout on `/healthz` and `/readyz` and rolls a release that never serves
back, which fails the job before later regions see it. No operator steps
remain. See ts-orchestrator `docs/continuous-delivery.md`, "Automatic service
releases". `scripts/ci/check-deploy.sh` (in `fast`) runs the script against a
stub API and ts-orch.

The executable extracts its bundled native library (libsodium, used for
Argon2id) at startup into `DOTNET_BUNDLE_EXTRACT_BASE_DIR` (default
`$HOME/.net`). Point it at a private directory writable only by the service
user, for example a systemd `StateDirectory`: files planted in a shared
extraction directory would be loaded.

`.forgejo/workflows/slow.yml` runs the slow suite after pushes, nightly and on
demand. It never blocks merges or releases. A failure files or updates the
open "Slow test suite failed" Forgejo issue with `fj`. The issue lists the failing test names and links the run; it never contains log text, which stays in the run behind the repository's permissions.
