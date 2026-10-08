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
to `a4ff285c051e63e72cb01c4a94173c91da1dd69a` (SDK merge #28). This adds the
TS-754 NUlid generator and canonical validator while preserving the Documents
and Management APIs. It is the helper foundation: auth's persisted IDs and
storage protocol have not changed. The ULID wire cutover awaits TerraBase's
landed storage contract and compatible release. That revision's
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
a time. Lockout notices use a separate bounded queue and worker, so a slow
relay holds at most two SMTP connections and notices cannot displace codes. Codes still queued
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
read from the region's OCI Vault (each replica reuses a read for at most one
minute), and tokens signed before a rotation keep validating until the key retires
(see [bootstrap and automatic rotation](#bootstrap-and-automatic-rotation-ts-703)).
A ring out of strict creation order, empty or over the bound fails closed. No
route rotates the ring; the scheduled rotation does.
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

- TS-704 now limits every token request by peer IP before client authentication,
  and hosted sign-in shares the headless IP and normalized-email budgets.
  Account-budget refusals now queue deduplicated lockout notices. Explicit
  trusted-proxy support is opt-in; production ingress verification remains a follow-up.
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
| Every hosted/headless sign-in call per IP (IPv6 per /64) | 30 per minute | `headless-ip` |
| Every hosted/headless sign-in call per IPv6 /48 | 120 per minute | `headless-ip-site` |
| Mail-sending calls per IP (IPv6 per /64) | 10 per 15 minutes | `headless-ip-mail` |
| Mail-sending calls per IPv6 /48 | 40 per 15 minutes | `headless-ip-site-mail` |
| Mail-start calls per normalized email, with or without an account | One per 60 elapsed seconds, shared by email-code/start, sign-up and password-reset/start across all clients and replicas | `headless-mail-email` |
| Password attempts per email, with or without an account | 10 per 15 minutes | `headless-password-email` |
| Hosted/headless sign-in code attempts per email | 5 per 15 minutes | `sign-in-code-email` |
| Password-reset code attempts per email | 5 per 15 minutes | `reset-code-email` |
| Code requests per email, across all mail starts | 5 per UTC clock hour | `code-mail-email` |
| Every token request per ordinary peer IP (IPv6 per /64) | `TS_AUTH_RATE_LIMIT` per minute (default 1,000) | `token-peer-ip` |
| Every token request per ordinary peer IPv6 /48 | Four times the peer-IP limit | `token-peer-ip-site` |
| Every token request per trusted proxy IP (IPv6 per /64) | Four times `TS_AUTH_RATE_LIMIT` per minute | `token-proxy-ip` |
| Every token request per trusted proxy IPv6 /48 | Four times the proxy-IP limit | `token-proxy-ip-site` |
| Token requests through an explicitly trusted proxy per resolved client IP (IPv6 per /64) | `TS_AUTH_RATE_LIMIT` per minute | `token-client-ip` |
| Token requests through an explicitly trusted proxy per resolved client IPv6 /48 | Four times the client-IP limit | `token-client-ip-site` |

The normalized-email request budget and the passcode issued-code budget both
reset on UTC clock-hour boundaries. Stored issue timestamps identify their
containing UTC hour; this changes no record format.

Every attempt counts, successful ones included. An email whose password budget
is spent still signs in with an emailed code; someone who knows an address can
spend its password budget, which delays only password sign-in for that address.
Unknown emails spend the same budgets as existing accounts, and replacing a
code never renews its email budget. Reset redemption has its own budget, so a
sign-in throttle does not consume recovery attempts. `Retry-After` rounds up to
whole seconds until the persisted window or cooldown ends, including state
advanced by a replica whose clock is ahead. A headless mail start admitted by
the cooldown but rejected by the hourly budget reports at least 60 seconds,
covering both constraints even just before an hour boundary. Cooldown refusals
do not spend the hourly allowance. Hosted and general token guards use the socket
peer address by default, sharing a proxy's budget. Explicit trusted peers can
supply client addresses under the policy below. End-user headless limits still
trust `ip_address` only after authenticating the first-party client. Since TS-582
`rate_limits` is one of `AuthStore.RequiredCollections`: `/readyz` answers 503
while it is missing, and the limiter fails closed with 503 on any storage
failure.

On a password, sign-in-code or reset-code account-budget refusal, the existing
notice worker looks up the normalized address and sends its real account owner
a branded lockout notice. Unknown addresses enqueue the same lookup but send
no email. IP refusals, mail cooldowns and hourly issuance caps do not notify.
Each method is paused only until the original fixed window ends; every
attempt counts, and another method's budget remains available. The English
and Portuguese notice states the absolute UTC deadline without implying a
successful sign-in. Hosted verification uses its `Accept-Language`; headless
notices use English, as headless code emails do.

The limiter preserves the exact persisted deadline alongside `Retry-After`.
The worker conditionally creates or replaces one `AttemptBucket` per actual
user id and method in `rate_limits`, under a separate keyed notice purpose;
its `window` holds the latest claimed deadline in Unix seconds and `used` is
one. Notice ownership never uses an email fold, so colliding budgets do not
merge recipients. Equal or older deadlines are suppressed across replicas,
including a slower clock, and later windows can notify again. This adds no
collection or migration. Claims recheck the clock after storage operations,
and the sender suppresses notices that expire while configuration or rendering
is awaited. A separate 256-entry notice queue and worker keeps refusals from
filling the sign-in and recovery queue or holding up its sender. A full notice
queue drops work before a claim is made;
later refused attempts can retry. The claim prevents duplicate send attempts,
but the existing worker and SMTP sender remain best-effort: process failure
after the claim or SMTP failure can lose a notice. There is no durable delivery
outbox or promise of eventual delivery. These failures never admit a blocked
authentication attempt or change its public response.

### Trusted proxy ingress

`TS_AUTH_TRUSTED_PROXIES` (or `--trusted-proxies`) enables ASP.NET Core's native
forwarded-header middleware for at most eight distinct exact numeric socket
peer addresses, separated by semicolons. IPv4-mapped IPv6 normalizes to IPv4.
DNS names, networks, unspecified addresses, scoped IPv6 and ambiguous IPv4
forms are rejected at startup. There is no implicit loopback or network trust;
the unset default ignores forwarding headers.
Equivalent numeric IPv6 spellings are allowed; duplicate normalized addresses
are rejected. Startup errors distinguish malformed, unspecified, duplicate and
over-capacity proxy lists.
The application disables ASP.NET's automatic forwarding startup path, including
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`, so only this explicitly ordered policy applies.

Only `X-Forwarded-For` is processed, one hop from the right. Earlier chain
entries remain untrusted and are never followed, even if they name another
configured proxy. Empty final entries, including blank repeated wire headers and
empty quoted values, suppress forwarding before native parsing can discard them.
Missing or malformed rightmost entries leave the socket
address unchanged, sharing that proxy's client budget. `Forwarded`, `X-Real-IP`,
`CF-Connecting-IP`, forwarded host and forwarded protocol are not used.
Issuer URLs remain bound to `TS_AUTH_PUBLIC_ORIGIN`.

Token requests first charge the original socket peer before forwarding. An
exact configured proxy gets four times the normal aggregate capacity (capped
at `Int32.MaxValue`); after forwarding its request also charges the separate
client budget before OpenIddict checks client credentials. Direct and untrusted
peers keep their existing socket-only budget. Trusted-proxy aggregate purposes
are separate from ordinary peer purposes, so sharing an IPv6 /64 or /48 does
not mix their different capacities. Both levels retain IPv4-mapped
normalization and IPv6 /64 and /48 limits, persisted through the existing
TerraBase limiter. Hosted sign-in request and verification use the resolved
address; headless submitted IP handling is unchanged.

Production trust is **not enabled or claimed verified by this change**. Before
configuring it through ts-orchestrator, verify the intended region and service:

1. Establish the exact immediate load-balancer socket addresses and enforce
   that only the intended ingress can reach the auth listener.
2. Verify that the rightmost X-Forwarded-For entry is the real browser/client
   address for both regional and Atlas routes, including spoofed incoming headers.
3. If that entry is another intermediary, arrange a canonical client header
   at the trusted ingress; this one-hop policy cannot traverse a proxy chain.
4. Configure only the verified immediate peer addresses, then check separate
   client throttles, aggregate proxy throttles, 429/Retry-After and fixed issuer URLs.

Source inspection shows Atlas replaces X-Forwarded-For with CF-Connecting-IP,
but does not establish the final OCI load-balancer header order, actual backend
peers or current network enforcement. No deployment values are inferred from it.

### Security decisions

- **Password grant.** The repository rules forbid the resource-owner password
  grant for interactive client authorization. TS-582 is an explicit product
  decision (2026-10-06) for a WorkOS/Clerk-style first-party API, and the
  password grant here is that: an extension grant permitted only to the
  confidential first-party `ts-dashboard`, never to service clients or any
  other registration, behind client authentication and the limits above. The
  authorization code flow stays the only flow for every other client and for
  SSO and social sign-in.
- **No enumeration.** Starts within their budgets answer 202 and leave issuing and mail to the
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

- No MFA step yet: TS-707's internal TOTP layer refuses primary-only user
  authentication for active factors on both grants and every other online
  user authentication path. Public enrollment and proof-bearing sign-in
  remain later slices; see [MFA](mfa.md).
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
- One code delivery queue per replica (256 codes) serves sign-in, reset and sign-up
  mail; a flood beyond the per-IP budgets that outpaces the relay drops codes,
  and the user asks for another. Lockout work uses a separate queue and worker.
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
- **Rotation overlap (TS-703).** A second rotation answers `409 rotation_pending`
  while the previous secret's grace is active, even if the request asks for
  no grace. The check runs inside the version-conditional transaction retry,
  so competing replicas cannot replace an unexpired previous digest. At the
  grace expiry another rotation is allowed; revocation still deletes both
  digests immediately. No storage migration is needed. Signing-key rollover
  is described under [bootstrap and automatic rotation](#bootstrap-and-automatic-rotation-ts-703).
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

Signing keys live in one OCI Vault secret per instance (TS-703; see
[bootstrap and automatic rotation](#bootstrap-and-automatic-rotation-ts-703)),
so a rotation is one conditional new secret version. Each key's d ‖ X ‖ Y is sealed under `TS_AUTH_MASTER_KEY`
with `signing-key:<kid>:<instance>` as associated data, so a key copied into
another instance's ring fails to open. The ring also stores its `instanceId`
and each key's canonical public P-256 `x` and `y` coordinates. Each record's
`publicMetadataMac` authenticates the instance, key ID, algorithm, creation
and activation timestamps and coordinates using HMAC-SHA256. Its key is derived from the
deployment master key with HKDF-SHA256 and the domain
`ts-auth signing public metadata v1`; the message is the JSON array of those
seven strings, with the timestamps in invariant decimal form. The MAC is stored
as lowercase hexadecimal and compared in constant time before importing keys.
`v1` names this scheme and is bound into the ring's `masterKeyId` too, so a
future change of scheme makes stored rings fail closed as sealed under another
master key rather than be silently replaced. Each read is parsed, and its public
metadata verified, once per replica; every lookup still imports its own keys.
Replacing a stored public point, rebinding its tenant or changing its key ID
cannot introduce a new authentication trust anchor without the master key.

JWKS, OpenID Connect validation and platform authentication authenticate and
import only public metadata; they never decrypt a private-key box. The private
box is excluded from the MAC, so its failure does not invalidate authentic
public metadata. The configured master key must remain available. Unavailable private
material therefore stops issuance and rotation while existing tokens remain
verifiable and JWKS stays available, provided the vault remains available.
Public key objects cannot sign or export private material.

Every key in the ring is published and validates; the newest key whose
`activatesAt` has passed signs (if none has, the oldest). Both public and
private loading validate the owner, key identities, count, valid Unix
timestamps and strict oldest-first creation order. Private loading also checks
that each decrypted key matches its published coordinates; rotation restores
every original key before retirement, the capacity check or any write.
Invalid public metadata at runtime fails closed without rewriting storage.

### Bootstrap and automatic rotation (TS-703)

The ring is one secret in the region's OCI Vault (`gru-ts-auth-signing-key-ring`
in sa-saopaulo-1, `sfo-ts-auth-signing-key-ring` in us-sanjose-1), pre-created
by the operator with `{}`; TerraBase holds no key material. `OciSigningKeyVault`
reads the current version through the Secrets API, looking the secret up by
name on every read (`GetSecretBundleByName`, a POST with its parameters in the
query), so a recreated secret is followed at once although OCI may keep its
predecessor until the deletion date. It writes each new version, to the secret
it read,
through the Vault API's `UpdateSecret` with the secret's ETag as `If-Match`, so
of replicas writing on one version exactly one succeeds (412 for the others;
409 while a write is still being applied); a lost write backs off with jitter
(0.05 s doubling to 2 s) over at most eight attempts. A version read while the
secret is not `ACTIVE` is never written over. Requests are signed with a dedicated OCI API
key (`TS_AUTH_OCI_*`) whose policy allows only reading and versioning that
secret: the ts-auth process cannot use an instance principal, because the host
blocks instance metadata for its user. The OCI .NET SDK is not used: its region
table needs named cultures and ts-auth runs with invariant globalization; the
REST calls follow OCI's documented request signing (RSA-SHA256). Every
OCI failure answers 503. Each replica reuses its last read for one minute
(`KeyRingRefresh`, measured on the monotonic clock from when the read was sent,
so an answer that carried a superseded ring expires within a minute of that
ring being replaced, however late it arrived), and concurrent lookups
share one read of the current bundle (one Secrets API request), which ends when
the vault answers rather than when a waiter gives up; a read never replaces a
cached version of the same secret with a lower version number. A recreated
secret's versions start again, so across secrets the read this replica started
later wins: a delayed read of, or a write to, the replaced secret never
displaces the replacement, and a lookup whose read lost answers with the ring
that superseded it. After a failed read, lookups
answer 503 for five seconds (`FailedReadBackoff`) without asking the vault. A
token naming a key the cached ring lacks rereads the ring first (at most one
attempt every ten seconds, `UnknownKeyRefresh`, successful or not), as TerraBase refetches its JWKS for an unknown `kid`, so
a key another replica just added verifies even while OCI is still making its
version current. Bootstrap and rotation
always read the secret's ETag and the version it names. Each hourly tick first,
and independently of rotation, schedules deletion of the secret's deprecated
versions across every listing page (never the current or previous one; a
version another replica scheduled first, 409, is skipped),
because OCI allows 30 versions of a secret in active use; a bootstrap that
fails also prunes before its next attempt, so a full secret can still receive a
fresh ring. A scheduled version leaves active use at once but counts against
OCI's separate quota of 30 versions pending deletion until it is deleted; while
both quotas are full, the bootstrap keeps retrying and the replica stays not
ready. `TS_AUTH_OCI_ENDPOINT` accepts only a
loopback origin, for local stand-ins of the APIs. Endpoints are derived for the
commercial realm only, so a vault outside `oc1` is refused at startup. Development without the vault
settings answers 503 for every key operation.

`SigningKeyRotation` runs on every replica. At startup it bootstraps the
serving issuer's ring (`KeyRing.EnsureAsync`): when the secret holds `{}`, a
ring that does not parse, or a ring of this master key that belongs to another
instance or whose keys cannot all be authenticated and opened, it writes a
fresh one-key ring as a new version. Each ring records `masterKeyId`, an
HKDF-derived identifier of the master key that sealed it, read before anything
else in the ring. A ring naming another master key is never replaced, even when
its key records are damaged: a misconfigured replica (or a changed
master key) keeps failing its bootstrap and stays not ready, and the healthy
replicas keep serving. The explicit recovery is an operator writing `{}` as a
new version of the secret; the next bootstrap attempt then writes a fresh ring. Every token signed by a replaced key stops
validating and users sign in again; reference refresh tokens and sessions do
not depend on the ring. Vault failures leave the secret unchanged and the
bootstrap retries every five seconds; `/readyz` stays 503 until this replica's
bootstrap has completed and while the secret holds no ring. Readiness checks
the ring as the vault holds it now (one shared Secrets API read per readiness
observation, which is cached for a second), not the one-minute lookup cache. The bootstrap only
runs at startup: a ring that becomes invalid while serving fails closed until a
restart. The earlier
TerraBase ring was not migrated (closed beta); its legacy `signing_key_rings`
document is removed once the Vault ring is verified live.

After the bootstrap, and then hourly, each replica runs the scheduled
rotation (`KeyRing.RotateIfDueAsync`). Once the newest key is 30 days old
(`RotationAge`), it adds a key that is published at once but signs only after
`Overlap` (two days), so verifiers that cache the JWKS (TerraBase refreshes
every ten minutes) hold it before any token uses it. Every rotation also
retires the oldest keys whose successor has been active for at least
`Overlap`, which exceeds `Jwt.MaxLifetime` (24 hours), so a retired key has no
live token. Scheduled rotation alone keeps one or two keys; an unscheduled
`RotateAsync` within the overlap can add more. Its key is published at once
but first scheduled like any other, to sign after the overlap. The rotation
then reads the secret by name, as verifiers do, until the vault serves the key.
It then moves the key's activation, with a second conditional write, to one
refresh interval plus `ActivationMargin` (five seconds, because OCI's `Date`
header has whole-second resolution and answers spend time in transit) after
that answer. Every replica's cached ring read before publication has expired
by then, however long the write or OCI's publication took. If publication is
not seen within the attempts, or the secret was replaced, the key keeps its
scheduled activation. Replicas without a refresh interval (every lookup
rereads) sign with it at once. Activation is rounded up to the second. Concurrent replicas
resolve through the conditional replace: the loser rereads, finds the new key
and adds none. `RotateAsync` answers 409 `signing_key_limit` only if 16 keys are still
within the overlap.

Releases before this one ignore `activatesAt` and sign with the newest key, so
rolling back after a scheduled rotation (day 30 or later) signs with the
pre-published key at once; verifiers that cached the JWKS then refetch it for
the unknown key.

Nothing configures the refresh interval in production, so every replica uses
the same minute; the test override is refused beyond `KeyRingRefresh.Max`, the
overlap minus `Jwt.MaxLifetime`, `MaxClockSkew` and `ActivationMargin`, so a
replica always learns of a scheduled key long before it signs.

Activation, retirement and new keys' timestamps use the vault's clock, not the
replica's: every vault answer carries OCI's `Date`, anchored when the answer
arrives and extrapolated with the replica's monotonic clock (at most a minute,
the refresh interval). A replica whose wall clock is days off, or is stepped
after a read, therefore neither retires a key early nor starts signing with a
pre-published one early. Tokens, however, carry the replica's wall-clock
timestamps (OpenIddict's `TimeProvider`), so a replica signs only while its
wall clock is within `KeyRing.MaxClockSkew` (five minutes, the usual leeway of
JWT validators) of the vault's; beyond it, signing answers 503 and `/readyz`
fails. A key's last token is signed before its successor activates and lives at
most `Jwt.MaxLifetime` (24 hours) plus that bound, inside the two-day overlap
after which the key retires. Each private key stays sealed under the
master key inside the Vault secret, so the secret alone cannot sign. Fast regressions run the real
OCI client against a local stand-in of the two APIs (`FakeOciVault`, which
verifies each request signature, requires `If-Match` and applies writes
asynchronously like OCI) and cover
bootstrap of a legacy and an unauthenticated ring, a replica with another
master key, an emptied ring, replica clocks days apart, a wall-clock step after
a read, a stale read answered after a rotation, an unscheduled rotation across
cached replicas, an unknown key while OCI delays the current version, a
cancelled lookup sharing a read, unknown keys and lookups during a vault
outage, a recreated secret, version pruning under the version limit, before a
rotation on a full secret and during a bootstrap on a full secret, the per-replica
refresh, vault outages, scheduled pre-publication, concurrent
scheduled rotation, retirement after the overlap, capacity,
malformed/oversized rings, timestamp limits, public verification during
private-key failure and a corrupted oldest inactive key.

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
