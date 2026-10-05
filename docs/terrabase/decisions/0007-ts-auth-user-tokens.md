# 0007: The Management API authenticates ts-auth access tokens

- Status: accepted (human decision to authenticate with ts-auth tokens, 2026-10-05, TS-519). COS confirmed on
  2026-10-05: no organization claim is required; the service credential stays, restricted to the loopback admin
  listener; the token contract for TS-491 below.
- Scope: `src/TerraScale.TerraBase/Features/UserAuth`, `Features/Management`, `Features/Health/ReadyzEndpoint.cs`,
  `Configuration/TerraBaseOptions.cs`

## Context

Until now the Management API trusted one caller, the ts-dashboard backend. It presented a shared
service credential (`dashboard-credential-file`) and named the acting user and organization in
`X-TerraBase-User-Id` and `X-TerraBase-Organization-Id`. Whoever held that secret could act as any
user. Users sign in with ts-auth, which issues OIDC access tokens (JWT) and publishes its signing keys
through OIDC discovery and JWKS. Data-plane API keys (`tb_…`) are not part of this decision and are
unchanged.

## Decision

- **Tokens.** Every Management API request presents `Authorization: Bearer <ts-auth access token>`.
  TerraBase validates it locally against ts-auth's published keys. The token's `sub` is the acting
  user: the id stored in memberships, `createdBy` and audit `actor`.
- **Settings.** `auth-issuer` is the exact `iss`: an absolute `https` URL without whitespace, user information,
  query or fragment. ts-auth's issuer is `{public origin}/{instance slug}`, e.g.
  `https://auth.terrascale.tech/terrascale`. `auth-audience` is a comma-separated list of accepted
  `aud` values; a token must name at least one. Both are set together and require `client-port`.
  Unset, the Management API answers `401`.
- **Validation.** Header `typ` must be `at+jwt` or `application/at+jwt` (RFC 9068), so ID tokens and
  other JWTs from the same issuer are refused. Accepted algorithms: ES256/384/512, RS256/384/512 and
  PS256/384/512; `none` and HS* are refused. The `kid` must name a cached key. `iss` must equal
  `auth-issuer` exactly, `aud` must include one of `auth-audience` (exact strings: a trailing `/` makes another audience), and `exp` is required. `exp` and
  `nbf` allow 30 seconds of clock skew. `sub` must be 1 to 256 characters without control characters.
  A guest token (`is_anonymous` `true`, which ts-auth issues without sign-up) is refused: guests must
  not own organizations.
  Any failure answers `401` with no body, and the recorded reason is a failure type, never the token.
- **No organization claim.** The organization comes from the route, and TerraBase membership
  ([0005](0005-multi-tenancy.md)) decides access as before: a non-member gets `404`, a role too low
  gets `403`. ts-auth issues no organization claim TerraBase could use: its organizations are
  per-instance end-user organizations (`/v1/{slug}/organizations`), not TerraScale's platform
  organizations, which TerraBase owns. So no organization or tenant claim is read or required. The
  ts-auth instance is pinned by the exact `iss`.
- **Listeners.** The Management API is served on the client listener, which accepts only ts-auth
  access tokens, and on the loopback admin listener, which accepts only the management service
  credential. Each scheme refuses the other listener, so a token on the admin listener or the
  credential on the client listener gets `401`.
- **Management service credential, admin listener only (COS, 2026-10-05).** The TS-529 bootstrap creates
  the `terrascale` organization (projects `ts-auth`, `ts-dashboard`) before ts-auth exists, because
  ts-auth itself persists there, so a path that needs no ts-auth token must remain.
  - It replaces `dashboard-credential-file`, which is now an unknown setting (exit code `2`). Nothing
    used it: ts-dashboard, ts-deploy and ts-infra were checked on 2026-10-05.
  - `management-credential-file` holds a secret of at least 32 characters; only its SHA-256 is kept, and
    presented secrets are compared in constant time. Unset, the credential authenticates no one.
  - It names the acting user in `X-TerraBase-User-Id`, trusted only with the credential. The
    `X-TerraBase-Organization-Id` header is no longer read.
  - It works only on the loopback admin listener, so the shared secret that can name any user never
    crosses the network, and public Management calls always need a ts-auth token.
  - Every use is logged with the user, method and path, and every rejected secret with the method and
    path; the secret never is.
- **Service-to-service on the public listener.** ts-dashboard calls with the signed-in user's access
  token. A ts-auth client-credentials token (`sub` = client id) for an accepted audience acts as a user
  whose id is the client id. It reaches only organizations that id is a member of.
- **Invitation acceptance.** `POST /v1/management/invitations/accept` takes `{token}` only. The email
  is the access token's `email` claim, used only when `email_verified` is `true`. No verified email
  answers `403`; an email that differs from the invitation answers `403`. The body is never trusted
  for the email, because the caller may be the user itself.
- **Keys.** TerraBase reads `{auth-issuer without a trailing /}/.well-known/openid-configuration`,
  then its `jwks_uri`; both must be `https`, redirects are not followed, and each must answer `2xx`. A discovery document whose `issuer` is not exactly
  `auth-issuer` is not used (OIDC Discovery 1.0 §4.3), and its `jwks_uri` is never requested. Only RSA and EC keys are kept; symmetric
  (`oct`) keys are ignored. A document over 1 MiB fails. One 10-second deadline covers discovery and JWKS
  together, and shutdown cancels a fetch in progress.
- **Cache, refresh and readiness.** Keys are cached in memory.
  - Startup waits up to 10 seconds for the first fetch.
  - Until a fetch succeeds, every token gets `401` and `/readyz` answers `503`. A background loop
    retries every 2 seconds.
  - After the first success, the loop refreshes every 10 minutes. A failed refresh keeps the cached
    keys, so ts-auth being down after startup does not stop validation.
  - Requests never fetch keys, except that a token whose `kid` is not cached triggers one refresh.
    Concurrent requests share it, it runs at most once per 30 seconds, and the token is validated
    once more if the keys changed.
- **Observability.** Counter `terrabase.auth.key_fetches`, label `outcome` (`success` or
  `failure`). TerraBase's own log messages never contain tokens, the issuer URL or the audiences;
  fetch failures name the setting (`auth-issuer`) and the failure type only.

## Requirements on ts-auth

ts-auth's OIDC provider (TS-491) is not built yet. Production use of the Management API needs it, and
it must:

- issue access tokens as signed, not encrypted, JWTs with `typ` `at+jwt`. OpenIddict encrypts access
  tokens by default, so encryption must be disabled for this resource;
- put TerraBase's configured audience in `aud`, for example through RFC 8707 resource indicators;
- include `email` and `email_verified` in the access token, for invitation acceptance;
- publish discovery and JWKS at the paths above;
- sign with ES256 (the current ts-auth plan) or another accepted algorithm;
- publish a new key in the JWKS before signing with it. Otherwise the first tokens with the new
  `kid` work only through the rate-limited refresh.

## Consequences

- Renaming `dashboard-credential-file` to `management-credential-file`, admin listener only, is breaking for
  any deployment that sets the old name: it now stops the process with exit code `2`. No deployment set it.
- The `accept` body loses `email`. Callers must send a token whose `email` is verified.
- With `auth-issuer` set, a node whose first key fetch has not succeeded answers `503` on `/readyz`,
  so a load balancer probing it sends no traffic until the node can authenticate users.
- A key removed from ts-auth's JWKS stays trusted until the next successful refresh, at most about
  10 minutes later while ts-auth is reachable, and longer while it is not.
- Tokens are not checked for revocation: one stays valid until its `exp`, so signing a user out of
  ts-auth everywhere ends Management access only when the token expires (ts-auth's removed Rust
  provider issued one-hour access tokens). Checking ts-auth's `auth_epoch` or introspection is a
  follow-up if that window is too long.
- ts-auth's removed Rust provider issued access tokens with `typ` `JWT`, `aud` set to the client id,
  `token_use` `access` and no email. This decision does not accept those; it defines the contract
  TS-491 must meet (above). If TS-491 keeps the old shape instead, the `typ` rule and the
  invitation email source must change together: accepting `typ` `JWT` alone would also accept ID
  tokens, which carry the same issuer, audience and key.
- Platform-admin authorization is still not designed (see `docs/management-api.md`).
