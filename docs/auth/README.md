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
> server-side sessions and email passcodes as internal services on that store;
> no page or route serves them yet. OIDC (TS-491) is not implemented. The
> identity features below are being ported stream by stream. Until a stream
> lands, its routes are not served.
> [`docs/parity.md`](docs/parity.md) tracks each capability, and
> [`docs/csharp-port.md`](docs/csharp-port.md) describes the layout, commands
> and intentional changes. The Rust implementation remains in Git history
> (before TS-442 / `8dd2874`) as the behavioural reference, including the
> original TS-428 storage design.

The consumer-app competitive review and remaining release gates are in
[`docs/competitive-review.md`](docs/competitive-review.md). The consumer
integration contract (guest accounts, browser origins, device authorization,
organization permission checks) is in
[`docs/consumer-integration.md`](docs/consumer-integration.md).

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
| Organizations | Orgs, memberships, invitations, enrollment domains, versioned role permission definitions and current-state authorization checks |
| Admin portal | Dashboard-styled hosted page for org admins to configure GitHub, Microsoft, Google, custom OIDC and SAML connections, and view directories |
| Management API | Full developer REST API with `sk_...` keys, per-family key scopes, pagination, rate limiting |
| JWT templates | Custom-claim tokens (`{{user.email}}` substitution) minted on demand |
| Webhooks | HMAC-signed outbound events with retries, durable delivery status, dead-letter listing, and operator replay |
| Audit logs | Signed audit events in production, with query and verification APIs |
| Billing | Stripe subscriptions: Checkout, customer portal, signature-verified webhooks, plan quotas |


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

The service needs an explicitly configured TerraBase HTTPS origin, an
organization-bound Documents API key and an existing database. Resolve the
`auth` project UUID through Management before deployment; this example expects
that UUID in `AUTH_PROJECT_ID` and uses synthetic credential placeholders:

```sh
TS_AUTH_PUBLIC_ORIGIN=https://auth.terrascale.tech \
TS_AUTH_TERRASCALE_API_URL="${AUTH_STORAGE_ORIGIN:?set the resolved HTTPS authority}" \
TS_AUTH_TERRASCALE_API_KEY=tb_... \
TS_AUTH_DATABASE="${AUTH_PROJECT_ID:?set the auth project UUID}/auth" \
TS_AUTH_MASTER_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_AUDIT_SIGNING_KEY=$(head -c 32 /dev/urandom | base64) \
TS_AUTH_OPERATOR_KEY=$(head -c 32 /dev/urandom | base64) \
nice -n 19 dotnet run --project src/TerraScale.Auth
```

| Variable | Meaning |
| --- | --- |
| `TS_AUTH_PUBLIC_ORIGIN` | One global public origin shared by every replica (issuer URLs, email links, WebAuthn RP, OpenAPI server). Must be an origin without a path |
| `TS_AUTH_TERRASCALE_API_URL` | Required HTTPS origin, no default; no credentials, path, query or fragment. Host must be `api.terrascale.tech` or under `.api.terrascale.tech`; loopback HTTPS is permitted only in Development. Explicit static endpoint override |
| `TS_AUTH_TERRASCALE_API_KEY` | Organization-bound `tb_…` credential with `documents:read` and `documents:write`, restricted to the auth project/database where appropriate. A leading `Bearer` scheme is stripped; runtime uses no management credential |
| `TS_AUTH_DATABASE` | Required `project-uuid/database`, e.g. `<auth-project-uuid>/auth`; lowercase hyphenated UUID and lowercase database name (at most 63 characters). Project labels are not accepted |
| `TS_AUTH_MASTER_KEY` | Base64, 32 bytes; encrypts recoverable secrets (signing keys included) with AES-256-GCM. There is no re-encryption yet: changing it makes every sealed secret unreadable |
| `TS_AUTH_AUDIT_SIGNING_KEY` | Base64 32-byte HMAC key for audit-event seals (required outside Development) |
| `TS_AUTH_AUDIT_VERIFY_KEYS` | Optional semicolon-separated previous audit keys (at most 5), kept during rotation |
| `TS_AUTH_OPERATOR_KEY` | Bearer key that can create instances (`POST /v1/instances`) |
| `TS_AUTH_LISTEN` | Plain-HTTP listen address with an explicit port (default `http://0.0.0.0:8080`) |
| `TS_AUTH_ENVIRONMENT` | `Development` waives the audit-signing key requirement and permits a loopback HTTPS TerraBase API origin |
| `TS_AUTH_RATE_LIMIT` | Management API requests per key per window (default 1000) |
| `TS_AUTH_STRIPE_SECRET_KEY` / `TS_AUTH_STRIPE_WEBHOOK_SECRET` | Stripe credentials |
| `TS_AUTH_PRICE_PRO` / `TS_AUTH_PRICE_ENTERPRISE` | Stripe price ids for the plans |
| `TS_AUTH_STRIPE_API_URL` | Stripe API origin (default `https://api.stripe.com`; test override) |
| `TS_AUTH_SMTP_HOST` / `_PORT` / `_USERNAME` / `_PASSWORD` / `_FROM_ADDRESS` | SMTP delivery; host, username, password and from address are all or nothing, port defaults to 587 |
| `TS_AUTH_SENTRY_DSN` | GlitchTip/Sentry DSN for error reporting; a deployment secret, never committed. Off when unset |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (or the per-signal `OTEL_EXPORTER_OTLP_*_ENDPOINT`), `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_HEADERS` | Standard OpenTelemetry settings; traces, metrics and logs are pushed over OTLP only when an endpoint is set |

The intended production dataset is `auth/auth` in organization
`terrascale-internal`: project label `auth`, database name `auth`. Documents
routes require the project's UUID, so runtime configuration must use
`<auth-project-uuid>/auth`. The credential selects the organization; separate
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

The OAuth provider advertises and accepts `openid`, `profile`, and `email`.
OAuth clients register `none`, `client_secret_basic`, or
`client_secret_post`; endpoints enforce the registered method. Method `none`
makes a client public, and public clients cannot register either secret-based
method. Redirect URIs must use HTTPS except `localhost` or a loopback IP over
HTTP, and authorization requests must exactly match a registered URI without a
fragment or credentials.
`prompt=none` uses only an active session and returns `login_required` when none
exists. Forced reauthentication, consent, and account selection are unsupported:
`prompt=login`, `prompt=consent`, and `prompt=select_account` return
`login_required`, `consent_required`, and `account_selection_required`. The
combined `prompt=login consent` returns `interaction_required`. Empty, duplicate,
unknown, or `none` combined with another prompt value returns `invalid_request`.

Upstream OIDC connections request `openid email profile` by default. A custom
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
and a valid server certificate are mandatory; a relay that offers no TLS never
receives the credentials or the message. Delivery failures are logged and never
fail the request that sent the mail. For real delivery via OCI
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

Point `TS_AUTH_TERRASCALE_API_URL` at an already running compatible local
member's HTTPS origin and set `TS_AUTH_ENVIRONMENT=Development`, a valid
`tb_…` key and `TS_AUTH_DATABASE=<project-uuid>/<database-name>`. The application
does not start or embed TerraBase; the integration-test fixture starts the
SDK-pinned binary. `/readyz` checks the existing `uniques`,
`verification_tokens`, `storage_health`, `users`, `sessions` and `passcodes`
collections, then creates, reads
and conditionally deletes a synthetic document in `storage_health`. It returns
200 only on success, otherwise 503, without provisioning, repair or in-memory
fallback. Observations coalesce and are cached for one second after
completion, with a two-second deadline; unknown `region` is omitted.

The broader `scripts/smoke-real-stack.sh` still targets identity flows that
TS-490/TS-491 and later streams have not implemented; it is not evidence of
TS-489 acceptance.

## Provisioning the global auth dataset

The application and readiness probe never provision or repair state, and
there is no migration of Rust documents. `scripts/provision.sh` prints only
the provisioning plan; live production execution belongs to TS-470:

1. Resolve organization `terrascale-internal` and project label `auth` to their
   IDs through Management before deployment; keep management credentials out
   of the auth runtime.
2. Provision the `auth` database and a `tb_…` credential restricted to the
   auth project/database with `documents:read` and `documents:write`.
3. Explicitly call `AuthStore.ProvisionAsync` with the pinned SDK against that
   existing database. It creates `uniques`, `verification_tokens` and
   `storage_health`, plus the TS-490 `users`, `sessions` and `passcodes`
   collections; later feature streams own their other collections. `/readyz`
   requires all six, so a database provisioned before TS-490 reports 503 until
   this step is repeated. Run it before rolling out TS-490.
4. Configure every replica with `<auth-project-uuid>/auth` and the same
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
`GET /scalar`, `GET /admin/{asset}` and `GET /fonts/{asset}`. The target
surface below is the contract the port streams restore (from the Rust service);
each stream keeps its paths, methods and operation ids.


- Hosted UI: `/ui/S/sign-in`, `/sign-up`, `/account`, `/account/sessions`,
  `/forgot-password`, `/reset-password`, `/verify-email`, `/magic-link`,
  `/passcode`, `/mfa/*`, `/passkeys/*`, `/connections/*`, `/admin`,
  `/billing`, `/device`, `/invitations/{id}/accept`
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
  `/v1/S/authorization/check`, `/v1/S/billing*`, `/v1/S/instance`
- Billing webhook: `POST /v1/billing/webhook` (Stripe signature)
- Quota repair: `POST /v1/{slug}/billing/quota/reconcile` removes expired pending seat claims whose user is absent, then recounts durable owners and unresolved admissions. Active claims remain reserved. It returns examined/removed/retained claim counts and requires the instance's authenticated management key.

Seat assignment currently probes create-only claims up to the plan cap to
preserve race-safe ownership. Dense allocation can require work proportional to
that cap; an indexed or atomic allocator contract remains future work.

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
  create/read/conditional-delete access through Documents v1. Results are
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

The executable extracts its bundled native library (libsodium, used for
Argon2id) at startup into `DOTNET_BUNDLE_EXTRACT_BASE_DIR` (default
`$HOME/.net`). Point it at a private directory writable only by the service
user, for example a systemd `StateDirectory`: files planted in a shared
extraction directory would be loaded.

`.forgejo/workflows/slow.yml` runs the slow suite after pushes, nightly and on
demand. It never blocks merges or releases. A failure files or updates the
open "Slow test suite failed" Forgejo issue with `fj`. The issue lists the failing test names and links the run; it never contains log text, which stays in the run behind the repository's permissions.
