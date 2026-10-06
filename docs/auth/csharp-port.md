# C# port (TS-424)

ts-auth is a C# service on .NET 10. Epic TS-424 (ku.mariogk.com) tracks the
port. The Rust workspace was removed in TS-442; its source in Git history
before `8dd2874` is the behavioural reference for each port stream, including
the original TS-428 storage design. TS-489 implements the Documents v1 storage
boundary. TS-490 adds users, sessions and email passcodes on that store.
TS-491 adds an OpenID Connect provider for ts-dashboard, with a hosted
email-passcode sign-in page that serves those TS-490 services.
`docs/parity.md` tracks what has landed.

## Layout

| Path | Contents |
|---|---|
| `src/TerraScale.Auth/` | ASP.NET Core host. Features live in `Features/<Area>/`; each endpoint is one TerraScale.Endpoints class. |
| `src/TerraScale.Auth.Contracts/` | Persisted models and the `{"error":{"code","message"}}` envelope (`ApiException`). |
| `src/TerraScale.Auth.Security/` | Cryptographic primitives (port of `auth-security`): Argon2id, the secret box, ES256 keys and JWTs, tokens. |
| `src/TerraScale.Auth.Store/` | Documents v1 persistence and storage readiness through ts-dotnet (TS-489, from the TS-428 design). |
| `tests/TerraScale.Auth.Fast/` | Blocking integration suite (TUnit + Playwright). |
| `tests/TerraScale.Auth.Slow/` | Non-blocking integration suite. |
| `tests/Shared/` | Harness source linked into both suites: loopback Kestrel app, Chromium launcher, pinned real TerraBase fixture and observability test doubles. |

There are no unit tests. HTTP tests start the real application with
`AuthTestServer.StartAsync`. Storage tests use ts-dotnet against the real
TerraBase binary; `TerraBaseFixture` shares one process per suite and creates
isolated synthetic tenants/databases. Observability doubles test scheduling
and error reporting, not the persistence boundary.

The sibling `ts-dotnet` and `ts-libs` checkouts are project references. Locally
they are found at `../ts-dotnet` and `../ts-libs`, or under
`~/Projects/TerraScale/`; pass `-p:TerraScaleRoot=<dir>` to use another
location. CI checks out the revisions pinned in `.forgejo/workflows/ci.yml`
into a job-owned directory outside the repository.

Both `.forgejo/workflows/ci.yml` and `.forgejo/workflows/slow.yml` pin ts-dotnet
to `bdea1e8f4926e46367b751d8f633dcec04d760e9` (SDK merge #24). That revision's
`src/TerraScale.DotNet.Testing/TerraBaseRelease.cs` pins the Linux x64 server:

| Pin | Value |
|---|---|
| TerraBase commit | `ff256067015433c8c40b59879f1282d2a40480ce` |
| Release tag | `ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258` |
| Binary SHA-256 | `e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13` |

The helper verifies the binary's SHA-256 on every start. Provide a matching
`TERRABASE_BINARY`, a verified cached binary, or `TERRABASE_DOWNLOAD_TOKEN` /
`ORG_READ_TOKEN` to download the private release. `TERRABASE_CACHE_DIRECTORY`
overrides the cache location. The ts-auth fixture fails when the binary is
unavailable, including locally; it never skips these tests. The SDK's
`docs/terrabase-consumers.md` describes the adoption procedure but reflects
ts-auth before TS-489; its older pins and local-skip example do not describe
this integration. Local Release validation passed: formatting, build, locked
restore, NuGet audit, OpenAPI lint, 100 fast tests and 21 slow tests with zero
skips. No production/live acceptance is claimed.

## Commands

Run them at low CPU priority locally:

```sh
nice -n 19 make check       # format, build (analyzers as errors), NuGet audit, OpenAPI lint, fast suite
nice -n 19 make test-slow   # slow suite
nice -n 19 make dev         # dotnet watch, published over HTTPS on the tailnet
```

`make dev` picks a free loopback port and a free tailnet HTTPS port for this
worktree, prints the `https://<machine>.<tailnet>.ts.net:<port>` origin, and
removes only its own `tailscale serve` mapping when it stops. Put local
settings in `.env` (gitignored); without it, throwaway Development values are used.

## Configuration changes from the Rust service

The release image is the self-contained executable on the chiseled
`runtime-deps` base (see README, CI).


Configuration retains the `TS_AUTH_*` convention, with these changes:

- **Storage selectors and credentials:** `TS_AUTH_DATABASE` is required as
  `project-uuid/database`. The intended production dataset is `ts-auth/auth` in
  organization `terrascale`, where `ts-auth` is the project
  label. The pinned server and inspected TerraBase `origin/main` resolve that
  route by project ID, not label: configure `<ts-auth-project-uuid>/auth`.
  `AuthOptions` requires a lowercase hyphenated project UUID and a lowercase
  database name of at most 63 characters. An operator resolves the project
  label to its UUID through Management before deployment; runtime does no
  Management lookup. The API key is an organization-bound `tb_…`
  credential with `documents:read` and `documents:write`, narrowed to the
  project/database where appropriate. Runtime uses no management credential.
  Separate project/tenant selector options have been removed.
- **Storage endpoint:** `TS_AUTH_TERRASCALE_API_URL` is required, with no
  default. It must be an HTTPS origin without credentials, path, query or
  fragment, on `api.terrascale.tech` or a host under `.api.terrascale.tech`;
  loopback HTTPS is allowed only in Development. `App.Build` registers this
  as an explicit `TerraBaseStaticEndpointResolver` override through
  `ITerraBaseEndpointResolver`. The host does not hardcode GRU routing.
  Atlas discovery in ts-dotnet belongs to TS-507; the injectable resolver
  allows that implementation to replace the static override.
- **`GET /metrics` and `TS_AUTH_METRICS_KEY` are removed.** This is an
  intentional breaking change. Metrics, traces and logs are pushed over
  OpenTelemetry OTLP when the standard `OTEL_EXPORTER_OTLP_ENDPOINT` (and
  optionally `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_HEADERS`) is set. A
  collector can expose a Prometheus scrape endpoint if one is needed.
- **`TS_AUTH_SENTRY_DSN`** enables error reporting to GlitchTip through the
  Sentry SDK, for errors only. It is a deployment secret and must never be
  committed. Events are scrubbed before they leave the process: only
  non-sensitive request headers are kept; cookies, query strings, bodies, the
  user and the client IP are dropped; sensitive keys in tags, extras and
  breadcrumbs are masked. The fast suite checks this against a fake collector.
- Framework request logging (`Microsoft.AspNetCore.Hosting.Diagnostics`,
  `System.Net.Http.HttpClient`) is limited to warnings, because those
  categories log full URLs, query strings included.

## Storage and readiness (TS-489)

`AuthStore` uses generic per-feature collections through
`TerraBaseDocumentsClient`, with no unconditional writes or automatic write
retries. Reads expose the opaque ETag version; create uses
`TerraBasePrecondition.Absent` (`If-None-Match: *`), and replacement/deletion
uses `Matches(version)` (`If-Match`). Callers authorize the instance before
entering this internal boundary. Missing databases/collections and outages
propagate; there is no in-memory fallback.
Generic create/update/delete reject `uniques`, `verification_tokens` and
`storage_health`, so feature callers cannot bypass their dedicated operations.

Natural-key addresses are deterministic UUIDv8 values derived from SHA-256
over length-framed instance/kind/key inputs; new TerraBase IDs must be UUIDs.
Email normalization trims and lowercases invariantly; email syntax validation
belongs to the account boundary. Uniqueness uses create-only claim documents,
and explicit release checks the stored instance, kind, owner and version.
Never compensate an ambiguous write by releasing its claim; retain uncertain
ownership until writers are quiesced and an operator can verify it before a
conditional release.

Single-use challenges persist only lowercase SHA-256 secret hashes in
`verification_tokens`, with instance, purpose, subject and expiry bindings.
Consumption rejects invalid bindings, expiry and prior consumption, then
marks the record consumed with CAS and returns `Consumed=true` on success.
The record remains to reject replay and reissue; a write completing after
expiry cannot grant authorization.

`TerraBaseStorageHealth` checks that every collection in
`AuthStore.RequiredCollections` exists (`uniques`, `verification_tokens`,
`storage_health`, and since TS-490 `users`, `sessions` and `passcodes`), then performs a
synthetic create-only write, version/content-checked read and conditional
delete in `storage_health`. `/readyz` returns 200 only on success and 503 on
missing state, permission failure, outage or timeout. Concurrent observations
coalesce; the latest completed result is cached for one second and the
deadline is two seconds. `region` is omitted when unknown, as it is for this
probe.

The application and probe never provision or repair state. An operator/test
can explicitly call `AuthStore.ProvisionAsync` to create those three
infrastructure collections, plus `users`, `sessions` and `passcodes` (TS-490),
`signing_key_rings` (TS-488) and `oidc_applications`, `oidc_authorizations`
and `oidc_tokens` (TS-491), in an already existing database; repeated setup
accepts an existing collection. Later feature streams must provision their own
collections. Readiness requires the same list (`AuthStore.RequiredCollections`),
so a database provisioned before TS-491 reports 503 until `ProvisionAsync`
runs again. This is not production project/database provisioning or a
migration of Rust documents.

## Users, sessions and passcodes (TS-490)

`Users`, `Sessions` and `Passcodes` in `src/TerraScale.Auth/Features/Identity/`
are singletons registered in `App.Build` over `AuthStore`. TS-491's hosted
sign-in page is the only route that serves them, for the single instance
`terrascale` (see below); there is no JSON route or management API for them.
Callers authorize the instance before using them. The fast suite drives
sign-in, rotation and sign-out over real HTTP through test-only routes
(`tests/TerraScale.Auth.Fast/Identity/`).

- **Users** (`users`): creating an account writes the email claim and the user
  document in one TerraBase transaction, both create-only, so a claim never
  outlives a failed creation. A taken email returns null. Emails are trimmed,
  lowercased and checked for plausibility (one `@`, no whitespace or control
  characters, at least two non-empty domain labels).
- **Sessions** (`sessions`): the 48-character token is never stored; its
  SHA-256 is the document address. The lifetime is an absolute 14 days. Each
  resolution reads TerraBase and checks instance, revoked flag, expiry and the
  user's `authEpoch`, so revocation is immediate on every replica. Rotation
  revokes the old session with a conditional write before issuing a successor
  with the same expiry. Sign-out everywhere advances `authEpoch`. Every ten
  minutes each replica's `SessionPruner` deletes revoked and expired documents
  with version-conditional deletes in batches of 500, continuing while a batch
  comes back full and deletes something; competing replicas share the backlog.
  A dead session grants nothing while it waits. A failed pass logs a warning and
  retries.
- **Cookie**: the existing `tsauth_session` from `BrowserSecurity` (`Secure`,
  `HttpOnly`, `SameSite=Lax`, `Path=/`, browser-session lifetime).
- **Passcodes** (`passcodes`, one document per user): six digits, 10 minutes,
  single use, 5 wrong guesses, 5 issued codes per user per hour. Only an
  HMAC-SHA256 keyed by HKDF-SHA256 of `TS_AUTH_MASTER_KEY` and bound to instance
  and user is stored, so changing the master key invalidates outstanding codes.
  Each wrong guess or redemption is a conditional write that must win, so
  concurrent requests cannot exceed the limit or redeem a code twice. A code is
  consumed before its session is written.

Per-user limits live on the passcode document. The TS-488 `AttemptLimiter` is
not on `main` and not carried by TS-491, so the hosted sign-in routes have no
per-client or per-route limit yet; they do give generic responses and send
email after the response. Rust
stored SHA-256 of the code and throttled resends to one per 30 seconds; C# uses
the keyed HMAC and the hourly issue cap instead. Password sign-in and reset,
magic links, email verification, account pages and the management API remain in
TS-432's later work.

## OpenID Connect (TS-491)

TS-491 is phase 1 of the OAuth/OIDC port: enough for ts-dashboard to sign
users in. Device authorization, guest accounts, browser CORS and SAML are
follow-up TS-495. The code is in `src/TerraScale.Auth/Features/Oidc/` and
`Features/SignIn/`.

### Design

The provider runs on OpenIddict 7.7.1 (`OidcIssuer.AddOidc`). There is no
instance registry yet, so there is one issuer, `{TS_AUTH_PUBLIC_ORIGIN}/terrascale`,
owned by the instance `terrascale`; per-slug issuers wait for that registry,
and other slugs answer 404. The `TrustPublicOrigin` handler resolves OpenIddict's
base and request URIs against `TS_AUTH_PUBLIC_ORIGIN`, so the issuer, every
advertised URL and endpoint matching never use the request's Host header or
scheme. TLS terminates in front of the service.

Routes: discovery, JWKS, `authorize`, `token`, `userinfo` and `logout` under
`/terrascale/`, and the hosted sign-in at `/ui/terrascale/sign-in` and
`/ui/terrascale/sign-in/verify`. Each is a TerraScale.Endpoints class, so it
appears in `/openapi/v1.json` (tags `OAuth` and `Sign-in`); OpenIddict
validates the request first and answers protocol errors itself.

- Only the authorization code flow, with PKCE required for every client and
  `plain` removed, so S256 is the only method. Codes live 1 minute.
- Scopes: `openid`, `email`, `profile` and `offline_access`. There is no scope
  store; each client's scope permissions bound its requests. `profile` adds no
  claims yet. `email` adds `email` and `email_verified` (always true: sign-in
  proves the address).
- Access and identity tokens are ES256 JWTs that live 1 hour. Access tokens
  follow TerraBase decision 0007 (TS-519): not encrypted, `typ` `at+jwt`,
  `aud` = every `TS_AUTH_ACCESS_TOKEN_AUDIENCES` value (set as OpenIddict
  resources, kept as written), and a user's `email` and `email_verified`
  whatever the scopes. Identity tokens keep `typ` `JWT` and `aud` = the client
  id. The private `ts_auth_epoch` claim stays inside codes and refresh tokens.
- Client credentials: service clients from `TS_AUTH_SERVICE_CLIENTS` have only
  the token endpoint and client-credentials permissions. Their access token has
  `sub` = the client id (ids can never be UUIDs, so they cannot collide with a
  user id), the configured audiences, no scopes and no email; `offline_access`
  is refused, so no refresh or identity token is issued. The dashboard client
  cannot use this grant.
- Key rotation: decision 0007 asks ts-auth to publish a new key before signing
  with it. The TS-488 ring signs with a new key at once, and nothing in this
  branch rotates it; the publish delay belongs with the first rotation trigger.
- `userinfo` accepts only an access token from this issuer that is not
  revoked; an identity token is rejected.
- `authorize` signs in the user of a live `tsauth_session` cookie. Without one
  it redirects to the hosted sign-in page with the request in `next`.
  `prompt=none` without a session, and a `max_age` the session has exceeded,
  answer `login_required`. `prompt=login`, `consent` and `select_account`
  answer `login_required`, `consent_required` and `account_selection_required`.
- `logout` is RP-initiated logout: it revokes the hosted session, clears the
  cookie, then returns to a registered `post_logout_redirect_uri` or shows a
  signed-out page.

Not implemented: introspection, the revocation endpoint, device authorization, forced reauthentication for `max_age`, and the
OpenIddict validation stack. No ts-auth API other than `userinfo` accepts
access tokens, and the server stack validates those.

### Hosted sign-in

The page uses the TS-490 email passcodes: six digits, 10 minutes, 5 wrong
guesses, 5 codes per account per hour. It signs in existing accounts only;
there is no sign-up route, and how accounts are created is an open product
decision. The page is the same whether or not the address has an account, and
the email is sent after the response, so its timing does not reveal it either.
`SignInMail`, a hosted service, queues at most 256 codes and sends them one at
a time, so a slow relay holds at most one SMTP connection. Codes still queued
when the host stops are sent within its shutdown timeout; a full queue drops
a code and logs that (the user can request another). Delivery uses `SmtpEmailSender` when `TS_AUTH_SMTP_*` is set; otherwise
`LoggingEmailSender` delivers nothing and logs only message sizes. Both are
copied from TS-488.

Form posts must carry an `Origin` header naming the public origin, which
refuses login CSRF. Scheme, ASCII (punycode) host and effective port are
compared, so `:443` or letter case in `TS_AUTH_PUBLIC_ORIGIN` does not refuse
a browser's serialized origin. Success sets `tsauth_session` (`Secure`, `HttpOnly`,
`SameSite=Lax`). `next` is honoured only when it is a
`/terrascale/oauth/authorize?…` path. The page resumes it with a navigation
(`meta refresh`) rather than a redirect, because CSP `form-action 'self'`
would block the redirect chain from the form post to the client.

### Dashboard client

`TS_AUTH_DASHBOARD_CLIENT_SECRET` and `TS_AUTH_DASHBOARD_REDIRECT_URIS` are
required together; `TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS` is optional.
The secret has 32 to 512 characters. URI lists are semicolon-separated exact
URIs, at most 10, HTTPS or HTTP only for `localhost` or a loopback IP, without
credentials or a fragment. For example
`https://dash.terrascale.tech/auth/callback` plus
`http://localhost:<port>/auth/callback` for development.

When they are set, `DashboardClientRegistration` registers or updates the
confidential client `ts-dashboard` at startup: `client_secret_basic` or
`client_secret_post`, PKCE required, implicit consent, and only OpenIddict's
PBKDF2 hash of the secret stored. Writes are create-only or conditional, so
concurrent replicas converge. If TerraBase is unavailable, startup fails
instead of serving without the client.

### Stores and consistency

OpenIddict's application, authorization and token stores are replaced with
TerraBase Documents v1 stores in `oidc_applications`, `oidc_authorizations`
and `oidc_tokens`. Every write is create-only or names the version it read
(`AuthStore.ReplaceAsync`, `If-Match`). The only cache is OpenIddict's
request-scoped entity cache. LINQ queries, counting, paging and pruning are
not supported.

Codes and refresh tokens are reference tokens. A token entry's reference
digest (SHA-256, computed by OpenIddict) is reserved in `uniques` in the same
transaction that stores or updates the entry (`AuthStore.CreateClaimedAsync`
and `ReplaceClaimedAsync`); lookups by handle go through that claim, and the
handle itself is never stored.

`RedeemExactlyOnce` replaces OpenIddict's redemption step, which ignores a lost
redemption for refresh tokens. A code or refresh token is redeemed with a
conditional write on the version the request validated, so of concurrent
redemptions exactly one succeeds. A loser answers `invalid_grant` and revokes
the sign-in's authorization. OpenIddict checks the authorization whenever it
validates one of its codes, refresh tokens or (at userinfo) access tokens, so
this rejects every token of the sign-in, including the winner's new ones and
any still being created. Token entries are not rewritten: rewriting them broke
the winner's own conditional writes and turned a won race into a 500. A later
replay is rejected and revokes the authorization the same way. Access JWTs
validated offline, as TerraBase does, stay valid until they expire (at most
1 hour). The token endpoint
also refuses a code or refresh token when the user no longer exists or has
signed out everywhere (`authEpoch` advanced).

Lookups by subject or client and the post-logout redirect lookup use
`AuthStore.QueryAsync`, a filtered TerraBase query in pages of 100 that scans
the instance's documents in the collection. No OpenID Connect flow sweeps
token entries.

### Keys

Access and identity tokens are signed with the current key of the TS-488 key
ring for the instance `terrascale`. The JWKS lists every key in the ring,
read from TerraBase on each request, so a rotation is visible to every replica
at once and tokens signed before it keep validating: the ring retains every
key and refuses rotation at 16 keys until a trustworthy-time retirement design
exists (TS-488). A ring out of strict creation order, empty or over the
bound fails closed. No route rotates the ring in this branch.
OpenIddict's startup ECDSA key is a public-only placeholder that never signs
or validates a token.

Codes and refresh tokens are signed and encrypted with keys derived with
HKDF-SHA256 from `TS_AUTH_MASTER_KEY`. A key-ring rotation never invalidates
them; changing the master key invalidates every outstanding code and refresh
token.

### Expiry and revocation

- Refresh tokens (with `offline_access`) are opaque, rotate on every use and
  have no reuse leeway. Sliding expiration is off, so the 14-day lifetime is
  fixed when the first refresh token is issued and rotation never extends it.
  The window starts at code redemption, not at the hosted sign-in, so a token
  family can outlive the session that started it by up to 14 days.
- After sign-out everywhere, refresh is refused, but access tokens already
  issued stay valid until they expire, at most 1 hour.
- Logout revokes the hosted session and clears the cookie. Tokens already
  issued to the client stay valid until they expire or are revoked.

### Limitations and follow-ups

- No rate limit on the token endpoint, and none on the sign-in routes beyond
  the per-account passcode limits. A single per-client bucket would serialize
  every dashboard refresh through one document.
- Expired tokens and authorizations are not pruned.
- No sign-up; accounts must already exist.
- Per-instance issuers wait for the instance registry.

The fast suite drives a browser-style passcode sign-in through `authorize`
and redeems the PKCE code at `token` over real HTTP against the real TerraBase
binary. It checks discovery, JWKS and both tokens with IdentityModel rather
than OpenIddict, calls `userinfo`, and checks that an identity token is
rejected there. No interoperability with a third-party client library or
production validation is claimed.

## Production activation gate

[`scripts/provision.sh`](../scripts/provision.sh) prints only a provisioning
plan. It performs no live setup; live dataset provisioning belongs to TS-529
in ts-orchestrator, with production activation still gated by TS-470.
Every auth replica must use the same single configured storage authority,
without automatic write retries or failover to another copy. Atlas endpoint
discovery (TS-507) does not supply replication or failover guarantees. The one
documented exception is `TS_AUTH_TERRASCALE_API_FAILOVER=true` (TS-577), for
private members that replicate: every replica still uses the first ready
member of one shared order and never resends a request, but while the
preferred member is down storage moves to the next one, which may not hold the
latest writes within the members' replication lag. Without it a deployment
lists one member and fails closed.

The pinned server's [decision 0003](https://git.terrascale.tech/TerraScale/terrabase/src/commit/ff256067015433c8c40b59879f1282d2a40480ce/docs/decisions/0003-sqlite-synchronous-normal.md)
uses SQLite WAL with `synchronous=NORMAL`. A TerraBase process crash alone
preserves committed writes, but an OS crash or power loss can roll back
acknowledged PUT/DELETE transactions since the WAL was last synced. Thus
uniqueness claims and consumed-challenge records can be rolled back.
[Decision 0005](https://git.terrascale.tech/TerraScale/terrabase/src/commit/ff256067015433c8c40b59879f1282d2a40480ce/docs/decisions/0005-multi-tenancy.md)
states that replication `Sync` is rejected by a bootstrap stub and committed
batches do not yet carry database identity. There is no supported replication
or regional failover for this pin.

Storage is implemented, but these limits leave live production activation
gated on an approved durability/recovery design and production verification
under TS-470. `/readyz` establishes current read/conditional-write access,
not crash durability, replication or safe failover. Local integration tests
cannot establish those guarantees.

## Key ring and attempt limits (TS-488)

The key ring and the attempt limiter store their state through
`IDocumentStore` (`src/TerraScale.Auth.Store/DocumentStore.cs`), addressed by
collection, instance and key: read with a version, create-only, and
replace-if-version. Production uses `TerraBaseDocumentStore`, which maps
these onto `AuthStore` natural-key addresses with `If-None-Match: *` and
`If-Match`. Single-use, uniqueness and counter state must be built on those
conditional writes, never on a read followed by an unconditional write or on
process locks. Consumers wrap the store in `FailClosedDocumentStore`, so any
failure other than caller cancellation (an outage, a missing collection, a
rejected key, an unexpected API error) answers 503 with the original
exception kept for server logs. The fast and slow suites run both against the
real SDK-pinned TerraBase; test-only wrappers inject failures and contention
over that real store, and two servers bound to one database stand in for
replicas.

Their collections, `signing_key_rings` and `rate_limits`, are provisioned
with the other collections (`scripts/provision.sh`); while one is missing the
feature answers 503. Since TS-491, `signing_key_rings` is also one of
`AuthStore.RequiredCollections`, because OpenID Connect signs every token with
the ring, so `/readyz` reports 503 while it is missing.

Signing keys live in one document per instance, so a rotation is one
conditional replace. Each key's d ‖ X ‖ Y is sealed under `TS_AUTH_MASTER_KEY`
with `signing-key:<kid>:<instance>` as associated data, so a key copied into
another instance's ring fails to open. Rotation adds a key and keeps the older
ones validating. Lookup and rotation validate the complete original ring's
count, valid Unix timestamps and strict oldest-first creation order before
importing keys; rotation restores every original key before the capacity
check or any write. A malformed or unreadable ring fails closed and stays
unchanged. Creation timestamps are kept strictly increasing across replicas,
but this ordering does not establish supersession age.

Automatic retirement is disabled: the pinned Documents v1 SDK exposes only
document content and an opaque version, without an authoritative update or
supersession time. A key created 72 hours ago can still sign a fresh token;
a replica 49 hours slow can date its successor 49 hours ago, making the old
48-hour pruning rule remove a live token's key on the next accurate rotation.
JWTs live at most 24 hours (`Jwt.MaxLifetime`), but replica wall clocks alone
cannot prove expiry. Rotation retains all keys and answers 409
`signing_key_limit` at 16 keys, regardless of elapsed local time; signing and
validation continue with the unchanged ring. There is no automatic recovery
from that capacity limit. Retirement remains gated on an approved design
with trustworthy time and safe handling of concurrent/in-flight signing;
do not delete keys or weaken the bound to bypass it. The persisted schema is
unchanged. Fast regressions cover the old-active-key/fresh-token slow-then-
accurate rotations, capacity after a clock jump, malformed/oversized rings,
timestamp limits and a corrupted oldest inactive key.

Attempt buckets are keyed by an HMAC of purpose and subject under a key
derived from the master key, within the instance's address space, and one
document is kept per distinct subject.

## OpenAPI

TerraScale.Endpoints serves the document at `/openapi/v1.json` and Scalar at
`/scalar`. Scalar's JavaScript is served from the package, and its default
fonts are turned off, so the page makes no third-party requests. The document
transformers in `Hosting/OpenApiSetup.cs`:

- set the server to the configured public origin, never the request's Host header;
- apply the security scheme each route enforces, and mark public operations `security: [{}]`;
- define only the schemes that are actually used;
- require a description for every tag.

`nice -n 19 make openapi-check` lints the served document with Redocly locally.
