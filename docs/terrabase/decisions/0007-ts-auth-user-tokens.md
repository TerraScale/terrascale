# 0007: The Management API authenticates ts-auth access tokens

- Status: accepted (human decision to authenticate with ts-auth tokens, 2026-10-05, TS-519). Confirmed on
  2026-10-05: no organization claim is required; the service credential stays, restricted to the loopback admin
  listener; the token contract for TS-491 below.
- Scope: `src/TerraBase/Features/UserAuth`, `Features/Management`, `Features/Health/ReadyzEndpoint.cs`,
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
  A verified invalid token answers `401`; unavailable verification keys answer `503 auth_unavailable` with `Retry-After: 1`. The recorded reason is a failure type, never the token.
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
- **Management service credential, admin listener only (2026-10-05).** The TS-529 bootstrap creates
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
  then its `jwks_uri`; both must be `https`, the JWKS address must have the configured issuer’s origin, redirects are not followed, and each must answer `2xx`. A discovery metadata whose `issuer` is not exactly
  `auth-issuer` is not used (OIDC Discovery 1.0 §4.3), and its `jwks_uri` is never requested. Only RSA and EC keys are kept; symmetric
  (`oct`) keys are ignored. A record over 1 MiB fails. One 10-second deadline covers discovery and JWKS
  together, and shutdown cancels a fetch in progress.
- **Cache, refresh and readiness.** Each successful nonempty key set replaces the in-memory snapshot and is
  written atomically to `<data-directory>/auth-signing-keys.json`: a flushed sibling `.new` file is renamed over
  the previous file. Version 1 records the exact configured issuer, fetch timestamp and only public RSA/EC
  verification material. No private signing parameters or symmetric keys are persisted.
  - Startup loads a matching snapshot before starting its issuer fetch. Stored keys verify exactly like fetched
    keys; the same token algorithm, signature, issuer, audience, type and lifetime checks apply.
  - The maximum age since the last successful fetch is **24 hours**, in memory and on disk. Future-dated,
    expired, malformed, empty or wrong-issuer stored snapshots are not used. Restart does not reset the age.
  - Without usable keys, requests answer `503 auth_unavailable` with `Retry-After: 1`. The background fetch
    retries indefinitely with a fixed **2-second** delay and a **10-second** deadline per attempt. Once the
    issuer is healthy, even an attempt already in flight followed by the next attempt takes at most **22 seconds**
    to obtain keys, assuming the healthy issuer completes within that deadline.
  - `/readyz` stays independent of issuer availability: ts-auth itself needs TerraBase to serve its database.
  - With usable keys, refresh runs every **10 minutes** or at the snapshot’s 24-hour expiry, whichever comes
    first. Failed or empty refreshes retain the snapshot without resetting its age. Once it expires, verification
    answers 503 and the two-second background schedule applies.
  - An unknown `kid` shares one in-flight refresh, at most once per **30 seconds** across requests. Failed or
    rate-limited refreshes answer 503 rather than reporting an invalid token. A successful refresh
    authoritatively confirms whether that key exists; an absent key then answers 401. A held key’s invalid
    signature still answers 401 during an issuer outage. The next unknown-key refresh can finish within
    **40 seconds** after issuer recovery (30-second admission interval plus its 10-second deadline).
  - Project mapping misses are not cached. Key-status refusal caching accepts only explicit `key_revoked` or
    `key_expired` responses from ts-auth’s authoritative status read; failed, malformed and generic invalid-token
    responses do not populate that cache.
- **Revocation delay.** Keys removed at the issuer continue verifying here until the next successful refresh,
  normally within ten minutes plus its fetch deadline. An issuer outage can extend that window to the remaining
  snapshot age, at most 24 hours since its last successful fetch. Token expiration and API-key revocation admission
  still apply independently. An expired snapshot never extends the trust window on restart.
- **Observability.** Counter `terrabase.auth.key_fetches`, label `outcome` (`success` or `failure`), gauge
  `terrabase.auth.signing_keys_loaded` (usable snapshot: 1; unavailable: 0), and gauge
  `terrabase.auth.signing_keys_age` in seconds (-1 before any snapshot loads). Prometheus exports the latter
  as `terrabase_auth_signing_keys_age_seconds`. Alert when age exceeds **1200 seconds for five minutes**, or
  loaded stays zero for **one minute**; see [signing-key operations](../signing-key-operations.md).
  Logs never contain tokens, issuer URLs or audiences; failures name the setting and failure type only.

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
- A node can become ready during an issuer outage. Known tokens validate from a nonexpired stored snapshot;
  unavailable verification answers retryable 503. Readiness never waits on ts-auth.
- A key removed from ts-auth’s JWKS remains trusted until the next successful refresh, subject to the 24-hour
  snapshot maximum described above. Operators must account for this delay during emergency revocation.
- Tokens are not checked for revocation: one stays valid until its `exp`, so signing a user out of
  ts-auth everywhere ends Management access only when the token expires (ts-auth's removed Rust
  provider issued one-hour access tokens). Checking ts-auth's `auth_epoch` or introspection is a
  follow-up if that window is too long.
- ts-auth's removed Rust provider issued access tokens with `typ` `JWT`, `aud` set to the client id,
  `token_use` `access` and no email. This decision does not accept those; it defines the contract
  TS-491 must meet (above). If TS-491 keeps the old shape instead, the `typ` rule and the
  invitation email source must change together: accepting `typ` `JWT` alone would also accept ID
  tokens, which carry the same issuer, audience and key.
- Platform-admin authorization is still not designed (see `docs/terrabase/management-api.md`).
