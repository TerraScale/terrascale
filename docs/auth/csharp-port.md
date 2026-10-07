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
`signing_key_rings` (TS-488), `oidc_applications`, `oidc_authorizations`
and `oidc_tokens` (TS-491), `rate_limits` (TS-488, required since TS-582) and
`email_configs` and `email_config_versions` (TS-583), in an already existing
database; repeated setup accepts an existing collection. Later feature streams
must provision their own collections. Readiness requires the same list
(`AuthStore.RequiredCollections`), so a database provisioned before TS-583
reports 503 until `ProvisionAsync` runs again (or the missing collections are
created). This is not production project/database provisioning or a
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

Per-user limits live on the passcode document. The hosted sign-in routes have
no per-client or per-route limit (the TS-488 `AttemptLimiter` limits only the
TS-582 headless API); they do give generic responses and send email after the
response. Rust
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
  id. The private `ts_auth_epoch` claim rides in codes and refresh tokens and,
  since TS-598, in access tokens, where the platform management API compares it
  with the user's current epoch.
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

Not implemented: introspection, device authorization, forced reauthentication for `max_age`, and the
OpenIddict validation stack. No ts-auth API other than `userinfo` accepts
access tokens, and the server stack validates those.

### Hosted sign-in

The page uses the TS-490 email passcodes: six digits, 10 minutes, 5 wrong
guesses, 5 codes per account per hour. It signs in existing accounts only;
accounts are created by the [headless sign-up](#headless-api-ts-582). The page is the same whether or not the address has an account, and
the email is sent after the response, so its timing does not reveal it either.
`SignInMail`, a hosted service, queues at most 256 codes and sends them one at
a time, so a slow relay holds at most one SMTP connection. Codes still queued
when the host stops are sent within its shutdown timeout; a full queue drops
a code and logs that (the user can request another). The code email is
rendered by `AuthEmails` (TS-583) as HTML with a plain-text alternative, in
English or, when `Accept-Language` prefers Portuguese, Brazilian Portuguese. Delivery uses `SmtpEmailSender` when `TS_AUTH_SMTP_*` is set; otherwise
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

- No rate limit on the code and refresh grants, and none on the hosted sign-in
  routes beyond the per-account passcode limits (the TS-582 headless grants are
  limited). A single per-client bucket would serialize every dashboard refresh
  through one document.
- Expired tokens and authorizations are not pruned.
- Per-instance issuers wait for the instance registry.

The fast suite drives a browser-style passcode sign-in through `authorize`
and redeems the PKCE code at `token` over real HTTP against the real TerraBase
binary. It checks discovery, JWKS and both tokens with IdentityModel rather
than OpenIddict, calls `userinfo`, and checks that an identity token is
rejected there. No interoperability with a third-party client library or
production validation is claimed.

## Headless API (TS-582)

TS-582 lets ts-dashboard sign users in on its own pages, the way apps use the
WorkOS User Management API or the Clerk Backend API: the dashboard's Go backend
collects the email, password or emailed code and calls ts-auth
server-to-server. SSO and social sign-in keep the redirect flow. The code is
in `src/TerraScale.Auth/Features/Headless/`.

### Contract

Every call authenticates the confidential client with HTTP Basic
(`client_secret_basic`, as at the token endpoint). Only clients holding the
headless grant permissions may call it, and only `ts-dashboard` is registered
with them; other slugs answer 404. `ip_address` is the end user's IP address as
the dashboard saw it; rate limits key on it, and it is trusted only because the
caller is an authenticated first-party client.

Tokens come from the standard token endpoint, so they are exactly the redirect
flow's (`at+jwt` access token with the configured audiences and email claims,
identity token for the client, rotating refresh token with reuse revocation):

| Grant (`POST /terrascale/oauth/token`, form) | Parameters |
| --- | --- |
| `urn:terrascale:params:oauth:grant-type:password` | `email`, `password`, `ip_address`, `scope` |
| `urn:terrascale:params:oauth:grant-type:email-code` | `email`, `code`, `ip_address`, `scope`, and `pending_token` for a sign-up code: a sign-in code, or a sign-up code, which creates the account |
| `refresh_token` | unchanged |

Every wrong email, password or code, an unknown account and an account
without a password answer `400 invalid_grant` with one body. The other
steps are JSON (`{"error":{"code","message"}}` on failure):

| Route (`POST /terrascale/headless/v1/…`) | Body | Success |
| --- | --- | --- |
| `email-code/start` | `email`, `ip_address`, optional `pending_token` | 202; resends the token-bound pending sign-up verification code, or mails a sign-in code if the account exists |
| `sign-up` | `email`, `password`, `ip_address` | 202 `{"pending_token"}`; mails a verification code, or a sign-in code if the address has an account |
| `password-reset/start` | `email`, `ip_address` | 202; mails a reset code if the account exists |
| `password-reset/complete` | `email`, `code`, `new_password`, `ip_address` | 204; password set, user signed out everywhere |

Failures: 400 `invalid_request` (malformed email, IP or body), 400
`invalid_password` (not 8 to 1024 UTF-8 bytes of well-formed text), 400
`invalid_code`, 401 `invalid_client`, 429 `rate_limited` with `Retry-After`,
503 `temporarily_unavailable`. Sign-out is `POST /terrascale/oauth/revoke`
(RFC 7009); revoking a refresh or access token revokes its whole sign-in
(`RevokeTheSignIn`), so every refresh token of it is refused and `userinfo`
refuses its access tokens; TerraBase validates access tokens offline, so they
live out their hour.

### Accounts, codes and passwords

- Sign-up never creates an unverified account. The pending sign-up is a code
  document in `passcodes` keyed by the normalized email, holding the Argon2id
  hash of the chosen password and the SHA-256 of a random `pending_token` that
  only the requester receives; redeeming its code needs both the code (the
  inbox) and the token (the requester), creates the account with the
  create-only email claim, and clears the password hash from the consumed
  record. So nobody can pre-register someone else's address or attach their
  password to the owner's sign-up (a later sign-up of the address replaces the
  earlier one's code and token), and `email_verified` stays true for every
  account. A wrong token is refused before any guess is charged. A sign-up for
  an address that has an account mails that account a sign-in code, discards
  the password and answers a random token, so the answer is the same either
  way.
- Sign-in, reset and sign-up codes are the TS-490 passcodes: six digits, 10
  minutes, single use, 5 wrong guesses, 5 codes per account (or pending
  sign-up) per hour. A user has one current code whatever its purpose; the
  record's `purpose` binds it, so a reset code never signs in and a sign-in
  code never resets. Records written before TS-582 read as sign-in codes.
- TS-631 resends use the original `pending_token` at `email-code/start` and
  preserve the pending sign-up's password hash and token hash. For addresses
  without an account, a wrong token or missing pending record still answers
  202 and sends nothing; existing accounts receive sign-in codes. The resend
  replaces only the version read, so it cannot revive a consumed signup or
  overwrite a concurrent replacement. It invalidates the previous code and
  charges the existing hourly issue budget. Expired or exhausted codes can be
  renewed while their unconsumed pending record remains stored.
- Users gain an optional `passwordHash` (Argon2id PHC, the TS-488 parameters).
  Accounts created before TS-582 have none: they sign in with codes, or set a
  password through the reset. A password reset sets the hash and advances
  `authEpoch` in one conditional write, only while the epoch is still the one
  the code was issued under, so hosted sessions, outstanding codes and refresh
  tokens of the user all die.
- Passwords are hashed and verified with at most four Argon2id runs per
  replica (`PasswordHasher`, about 19 MiB each); a request that waits more
  than 5 seconds answers 503. An unknown account or one without a password is
  verified against a fixed hash, so timing does not tell them apart. Sign-up
  hashes the password even when the address has an account. The new password
  of a reset is hashed before its code is consumed, so a busy replica never
  burns a code.

### Limits

`AttemptLimiter` (shared by every replica through conditional writes on
`rate_limits`) now takes a window per purpose and folds attacker-chosen text
into 65,536 keyed buckets per purpose (`Fold`, as Rust did for guest
mailboxes), so storage stays bounded and nobody can target a victim's bucket.

| Budget | Limit | Purpose |
| --- | --- | --- |
| Every headless call per IP (IPv6 per /64) | 30 per minute | `headless-ip` |
| Every headless call per IPv6 /48 | 120 per minute | `headless-ip-site` |
| Mail-sending calls per IP (IPv6 per /64) | 10 per 15 minutes | `headless-ip-mail` |
| Mail-sending calls per IPv6 /48 | 40 per 15 minutes | `headless-ip-site-mail` |
| Mail-start calls per normalized email, with or without an account | One per 60 elapsed seconds, shared by email-code/start, sign-up and password-reset/start across all clients and replicas | `headless-mail-email` |
| Password attempts per email, with or without an account | 10 per 15 minutes | `headless-password-email` |

Every attempt counts, successful ones included. An email whose password budget
is spent still signs in with an emailed code; someone who knows an address can
spend its password budget, which delays only password sign-in for that address.
`Retry-After` is the window's full length, an upper bound. Since TS-582
`rate_limits` is one of `AuthStore.RequiredCollections`: `/readyz` answers 503
while it is missing, and the limiter fails closed with 503 on any storage
failure.

### Security decisions

- **Password grant.** The repository rules forbid the resource-owner password
  grant for interactive client authorization. TS-582 is an explicit product
  decision (2026-10-06) for a WorkOS/Clerk-style first-party API, and the
  password grant here is that: an extension grant permitted only to the
  confidential first-party `ts-dashboard`, never to service clients or any
  other registration, behind client authentication and the limits above. The
  authorization code flow stays the only flow for every other client and for
  SSO and social sign-in.
- **No enumeration.** Starts answer 202 and leave issuing and mail to the
  `SignInMail` worker, grants answer one `invalid_grant`, and limits apply to
  unknown emails the same way. The Argon2id timing equalizer is hashed at
  startup, so the first unknown account costs no more than later ones.
- **Errors at the token endpoint** stay OAuth errors: a busy Argon2id pool or
  a storage failure answers `503 temporarily_unavailable` in RFC 6749 form;
  the JSON routes answer the same code in the `{"error":{…}}` envelope.
- **Not applicable:** CSRF (no cookies; server-to-server only) and CORS.
- **Audit events** are structured log lines (`Audit: headless …`) with the
  method, the outcome and the user id when known, never the email, password,
  code or IP. Persisted, signed audit events wait for the TS-439 audit log.

### Limitations

- No MFA step: MFA is not ported (TS-434), so no account can have it. When it
  lands, the two grants answer an MFA challenge instead of tokens.
- Users have no name; `profile` adds no claims.
- Code records (pending sign-ups included) are deleted by `SessionPruner`
  once their issue window and every code issued in it have ended (70 minutes
  after the window starts). Attempt buckets are bounded by folding and never
  pruned.
- Guessing ceiling: one code record per account (or pending sign-up) gives
  at most 5 codes × 5 guesses per hour, about 0.06% per day against a six-digit
  code, without a long-term lockout. Anyone who knows an address can supersede
  its live code or spend its 5 codes an hour, delaying (not taking over)
  that account's code sign-in.
- Timing: the three starts only charge limits and queue the work: the mail
  worker issues the code (or finds no account) and mails it after the
  response, so their timing does not depend on the account. The code grants
  and the reset completion still make two or three more storage round trips
  for an existing account than for a stranger, as the hosted page does.
- One delivery queue per replica (256 codes) serves sign-in, reset and sign-up
  mail; a flood beyond the per-IP budgets that outpaces the relay drops codes,
  and the user asks for another.
- The hosted page still signs in existing accounts by email code only.
- Rolling out: replicas of an older release ignore `purpose` and would accept
  a reset code on the hosted sign-in page (which needs the inbox anyway); they
  never write users, so they cannot drop a password hash.

## Platform management API (TS-598)

ts-auth owns the platform's organizations and projects (epic TS-597). The
contract is [`platform-contract.md`](platform-contract.md) with
[`platform-api.openapi.yaml`](platform-api.openapi.yaml); a fast test checks
that every served `/v1/terrascale/…` operation appears there with the same
path, method and operationId. The code is in
`src/TerraScale.Auth/Features/Platform/`. This slice serves organizations,
organization members, projects and explicit project roles; invitations,
ownership transfer, audit and API keys follow.

### Authentication

`PlatformAuthentication` accepts only the caller's own sign-in access token
of the platform tenant: an ES256 `at+jwt` validated against the tenant's key
ring with `Jwt.ValidateAsync` (issuer, `aud` including the issuer, expiry, no
clock skew). Sign-in tokens now carry the issuer as an extra audience
(`OidcIssuer.SignInAudiences`); service-client tokens do not, so they are
refused, as are identity tokens (`typ` `JWT`). Every request reads the user
and, from the TerraBase stores rather than OpenIddict's managers and their
entity cache, the authorization and token entries the token names, so a revoked
sign-in (RFC 7009, refresh-token reuse) is refused at once on every replica.
Sign-in access tokens carry the user's authentication epoch (`ts_auth_epoch`),
so after sign-out everywhere or a password reset the API refuses every token
issued before. A refused call is `401 unauthorized` with
`WWW-Authenticate: Bearer`.

### Storage and consistency

| Collection | Address | Holds |
| --- | --- | --- |
| `organizations` | id | name, creator, and the owner and project counts |
| `memberships` | `{organization}/{user}` | the organization role; none for a collaborator |
| `projects` | id | organization, name, creator |
| `project_members` | `{project}/{user}` | an explicit project role |

Project names are reserved in `uniques` (kind `project-name`, key
`{organization}/{name in invariant uppercase}`, which matches an ordinal
ignore-case comparison, so `ς` and `σ` are one name). Ids are UUIDv7.

Each change is one TerraBase transaction (`AuthStore.TransactAsync`), and
every write in it is create-only or conditional on the version read:

- The transaction asserts the role that authorized the caller (its membership
  or project role document, filtered on `role`) at the version its check
  read, so a caller demoted or removed meanwhile cannot finish. TerraBase
  refuses two operations on one document, so when the transaction also writes
  that document, `AuthStore.TransactAsync` leaves the assertion out only if
  the write names the same version, and otherwise fails the attempt. A
  caller changing its own records writes the versions its check read.
- Owner changes and project creation or deletion rewrite the organization
  with its counts, so the last owner is never lost and an organization with
  projects is never deleted, whatever runs concurrently.
- Every user with any access to an organization has a membership; a
  collaborator's has no role. Every grant rewrites (or creates) it, removing
  a collaborator's last project role deletes it, and removing a member or
  collaborator deletes it at the version it read with every project role it
  found. So a grant and a removal racing each other never leave the removed
  user a project role.
- A transaction that lost a race is read and checked again, at most four
  times, then answers 503.

Deleting an organization or project first deletes it in a transaction, then
deletes its memberships or project roles one by one. Every check reads the
organization and project first, so what a failed clean-up leaves grants
nothing; the failure is logged.

### Limits

- Lists page inside TerraBase: a filtered query sorted by the list's key,
  `limit` items at a time, and `next` is TerraBase's own cursor, bound to that
  list (any other answers 400). TerraBase has no field indexes yet, so the
  filter still scans the collection inside SQLite, and a field sort admits at
  most 10,000 matching documents (one organization's members or projects,
  one user's organizations). A user's grants in one organization are read
  whole, since there is at most one per project.
- Accounts are never deleted today; a member whose account is missing is left
  out of member lists.
- The management API has no per-caller rate limit yet: every call needs a
  signed-in user, and the edge limits volume.
- `/readyz` requires the four collections: create them in the live `auth`
  database (`AuthStore.ProvisionAsync`) before rolling out.

## API keys and organization and project tokens

The token grants of the [platform contract](platform-contract.md#6-tokens):
an API key's `client_credentials`, and a signed-in user's sign-in access token
exchanged (RFC 8693, `AllowTokenExchangeFlow`), each for a token that acts in
one organization or project. Both are in
`src/TerraScale.Auth/Features/Platform/` (`ApiKeys`, `PlatformGrants`).

- **Keys** live in `api_keys`, addressed by id (`tsk_` and 20 base32
  characters). The key string adds `_` and 52 base32 characters (260 random
  bits); only SHA-256 digests are stored. Creating, listing, rotating and
  revoking use the management API's authentication and its role assertions;
  revoking deletes the record, and deleting a project or organization deletes
  its keys after it (every exchange reads the organization and project first,
  so a key left behind issues nothing).
- **Client authentication.** `OidcApplicationStore` answers a `tsk_` client id
  with a client built from the key: confidential, only the token endpoint and
  the client-credentials grant, every TerraBase scope permission, and a stored
  secret of `tsk-sha256:` and the digests the key string may match (the current
  one, and the previous one during a rotation's grace).
  `ApiKeyApplicationManager` compares those in constant time; every other
  client keeps OpenIddict's PBKDF2. OpenIddict then checks the grant
  permission as for any client.
- **Grant.** `PlatformGrants.ApiKeyAsync` reads the key again, charges
  `api-key-token` (1,000 per key per minute, 429 `rate_limited` beyond),
  checks `project` (a project of the key's organization; a project key's own)
  and `scope` (within the key's), and signs a 15-minute token with `org_id`,
  `project_id`, `database` and the scopes. `invalid_target` and `invalid_scope`
  answer anything else.
- **Token exchange.** OpenIddict validates the subject token (signature,
  expiry, sign-in revocation); `ExchangeAsync` then requires a sign-in token
  (its audiences include the issuer) issued to the exchanging client whose
  `ts_auth_epoch` is still the user's (so not after sign-out everywhere or a
  password reset), reads the user's role in the organization or project, and
  issues its scopes for at most 15 minutes and never beyond the subject token's
  expiry. Unknown, unreachable and role-less targets all answer
  `invalid_target`. Only `ts-dashboard` holds the token-exchange permission
  and the TerraBase scope permissions so far; `OidcResults.SignIn` refuses
  (`invalid_scope`) any scope beyond `openid email profile offline_access`, so
  no sign-in, refresh or headless grant can carry them.
- **Fresh key check.** `ApiKeyAsync` re-reads the key and compares the
  presented key string with its current digests itself, whatever OpenIddict
  authenticated, so a key revoked or rotated on another replica issues nothing.
- **No token entries.** `SkipShortLivedTokenEntries` stores no OpenIddict token
  entry for organization and project tokens and for service clients' tokens:
  they are validated offline and never revoked one by one, so an entry would
  only grow `oidc_tokens`.
- `TS_AUTH_ACCESS_TOKEN_AUDIENCES` may not name the platform issuer
  (`{public origin}/terrascale`, compared as a URI), so an organization or
  project token can never carry the audience the management API requires; it
  compares audiences exactly, so other paths of the origin grant nothing there.

`api_keys` joins `AuthStore.RequiredCollections`; create it in the live `auth`
database before rolling out.

## Invitations, ownership transfer and audit

The rest of the platform management API (contract section 7), in
`Features/Platform/Invitations.cs` and `PlatformDirectory`.

- **Invitations** (`invitations`, addressed by UUIDv7 id) go to an email
  address for an organization role or, with `project_id`, a project role. The
  token is `tsi_` and 52 base32 characters; only its SHA-256 is stored, and
  `uniques` maps it to the invitation (kind `invitation-token`) and holds one
  pending invitation per email and target (kind `invitation-pending`). An
  expired invitation stays in the history, and a new one takes its claim over
  with a conditional write. Accepting, declining and revoking write the
  decision at the version read, release the pending claim and, for accepting,
  create the membership or project role in the same transaction, so an
  invitation works once even on two replicas at once. Accepting needs a
  sign-in token whose email is the invitation's; a project invitation makes a
  collaborator of a user who is not a member.
- **Email.** With `TS_AUTH_INVITATION_URL` set, the invitation is mailed after
  the response through the existing delivery queue (`SignInMail`, now also
  carrying invitations), rendered with the `invitation` email kind and the
  instance's branding (TS-583), linking to `TS_AUTH_INVITATION_URL?token=…`.
  The create call also answers the token once.
- **Ownership transfer** is a field of the organization record, written with
  a conditional write. It lapses when it expires (7 days), its starter is no
  longer an owner or its nominee no longer a non-owner member. Confirming
  makes the nominee an owner and the starter an admin in one transaction.
- **Audit events** (`audit_events`, UUIDv7 ids) are written in the same
  transaction as every change they record, including API key changes, and
  listed newest first (a descending TerraBase sort on the id). Deleting an
  organization deletes its events and invitations afterwards. Events are not
  pruned yet.

`invitations` and `audit_events` join `AuthStore.RequiredCollections`.

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
