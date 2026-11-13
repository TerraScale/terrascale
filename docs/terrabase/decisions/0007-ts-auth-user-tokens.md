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
- **Keys.** TerraBase reads `{auth-issuer without a trailing /}/.well-known/openid-configuration`, then its
  `jwks_uri`. Both must use HTTPS, return 2xx without redirects, and fit within 1 MiB. Discovery must name the
  configured issuer exactly; JWKS must share its origin. One ten-second deadline covers both requests.
  Entries marked for another use, of a key type other than RSA or EC, declaring an algorithm outside the
  allow-list, or declaring `key_ops` without `verify` are skipped, so a key the node does not understand never
  stops a refresh. Every remaining signing entry must convert to a usable public RSA or EC key; a malformed one,
  a symmetric key, private key material, or an empty signing set fails the entire fetch. Existing keys
  remain available after a failed fetch. A declared `alg` restricts that key to that algorithm, within the existing
  asymmetric algorithm allow-list; declared `key_ops` must include `verify`. The platform issuer publishes EC
  P-256 keys declaring `alg: ES256` and `use: sig`, without `key_ops`.
- **Cache, refresh and readiness.** Every successful nonempty set replaces the entire snapshot, including when
  it is smaller: that is how key removal propagates. Only public material is written to
  `<data-directory>/auth-signing-keys.json`. Version 2 records issuer, fetch time and a last-known-time high-water
  mark. A flushed sibling `.new` file is renamed over the snapshot, then its containing directory is fsynced.
  - Stored keys wait for the node's existing peer/kernel clock-trust gate before loading; readiness and fresh
    issuer fetches do not wait for stored trust. Disk and issuer keys share validation. A stored file is ignored
    as a whole if unreadable, larger than 2 MiB, truncated, malformed, another version or issuer, empty, or
    containing a malformed RSA or EC signing entry or secret key material. The load boundary catches content failures, logs one bounded reason
    without contents, and counts rejection; the node continues without stored keys.
  - Within a process, the **24-hour** allowance uses monotonic elapsed time from fetch or load. At load, UTC
    before either fetch time or high-water mark rejects the file as `clock_backwards`. The remaining allowance
    is 24 hours minus (high-water mark minus fetch time), further reduced by any wall time since that mark.
    Loading never resets the fetch time. A successful fetch writes a new checkpoint; while running, checkpoints
    advance at least with monotonic elapsed time and are attempted every **60 seconds**. Write failures are
    logged and do not stop the node. The high-water mark never decreases within a process.
  - Across restarts this relies on the clock-trust gate providing accurate UTC and on durable, unreverted local
    state. A high-water mark detects rollback below the recorded time; it cannot measure downtime during which
    all clocks are wrong but agree, detect restoration of an older data-directory copy, or recover an unwritten
    checkpoint after a failed write or crash. With timely successful writes and scheduling, up to one checkpoint
    period may be unrecorded after a crash; failed or stalled writes and scheduling delays can lose more. The monotonic in-process cap is unconditional; a universal 24-hour real-time
    guarantee across arbitrary clock/state rollback is not claimed. If clock trust cannot be established, stored
    keys are never loaded. The data directory must be as trusted as the process itself.
  - To drop stored trust, stop the node, delete `auth-signing-keys.json`, then start it. Deleting the file while
    the node runs does not clear its in-memory snapshot and a checkpoint may recreate the file.
  - Without usable keys, a token that could be valid answers `503 auth_unavailable` with `Retry-After: 1`.
    Unparseable tokens and readable invalid structure, algorithm, type, issuer, audience or lifetime answer 401
    first. Unverified claims can only reject; signature verification still precedes acceptance. Exchange validation
    uses the same precheck. A bad signature under a held key answers 401.
  - Background fetches use a **two-second start interval** without keys (or after the preceding attempt finishes,
    whichever is later), otherwise ten minutes. One fetch is ever in flight. After recovery, the remainder of an
    in-flight ten-second attempt, the next interval and a healthy fetch take at most **22 seconds**.
  - With usable keys, unknown IDs share in-flight work. Each **30-second** request-refresh cycle permits its first fetch plus
    exactly one follow-up for an ID first seen after the last refresh began. The follow-up starts no earlier than
    **15 seconds** after that refresh began. This bounds request-triggered fetches to two per cycle, coalesces
    requests, and bounds acceptance of an ID first seen after the latest refresh began to **25 seconds** under
    continuing random-ID traffic, assuming responsive scheduling and issuer responses within the fetch deadline.
    Tracking is bounded to 1024 IDs per cycle; saturation conservatively requests the same single follow-up,
    never additional fetches. An ID first seen after the follow-up began can queue the next cycle's first fetch at
    its boundary; no extra fetch is admitted in the current cycle. Previously observed IDs retain the **40-second**
    bound (30-second interval plus deadline). A failed or rate-limited refresh answers 503; an authoritative
    successful refresh that still lacks the ID answers 401. Expiry brings queued work forward to the no-key schedule. Smaller successful sets replace larger sets without a size heuristic.
  - Established SQL WebSocket sessions keep retryable unavailable requests open; their remembered token expiry
    closes even an idle socket. PostgreSQL renewal and pending Execute preserve `57P03` when authentication is
    unavailable; an actually refused credential answers `28P01`. Sessions with a known token expiry end at that
    expiry independently of key availability. Existing key-status admission and revocation checks still apply.
  - `/readyz` stays independent of issuer availability: ts-auth needs TerraBase to serve its database.
  - Project mapping misses are not cached. Key-status refusal caching requires authoritative `key_revoked` or
    `key_expired` responses; unavailable, malformed and generic invalid-token responses do not populate it.
- **Revocation delay.** Keys removed at the issuer continue verifying here until the next successful refresh,
  normally within ten minutes plus its fetch deadline. An issuer outage can extend that window to the remaining
  snapshot allowance described above. Token expiration and API-key revocation admission
  still apply independently. An expired snapshot is rejected on restart.
- **Observability.** Counter `terrabase.auth.stored_keys_rejected`, bounded `reason` values `invalid`,
  `clock_backwards` and `expired`, counts ignored stored files. Counter `terrabase.auth.key_fetches`, label `outcome` (`success` or `failure`), gauge
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
