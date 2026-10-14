# TerraBase V4 revocation bounds — 2026-10-14

Companion to `2026-10-14-replication-v4.md` (rows 9, 10 and 11). Raw outcomes: `2026-10-14-replication-v4-revocation.json`.

## 1. Live poll admitted before API-key revocation (row 9): kept as is

Decision: the current behaviour is kept, and its bound is preserved by a Fast test.

| Item | Value |
| --- | --- |
| Bound | A live poll admitted before its key is revoked answers **at most once**, **at most `LiveTimeout` = 20 s after admission**. Every later request gets `401` at once |
| Measured | The parked poll delivered a commit made after revocation, **110 ms** after the revocation (mariogk-dev, commit `4682879`, 2026-10-14) |
| Test | `tests/TerraBase.Fast/Shapes/ShapeSecurityTests.cs`: `A_live_poll_admitted_before_its_key_is_revoked_delivers_at_most_one_answer_within_the_live_timeout` |
| Why | Authorization runs once at admission: `RequireAuthorization(ApiKeyScopes.DocumentsRead)` (`src/TerraBase/Features/Shapes/GetShapeEndpoint.cs:75`). A live request then releases its request slot and parks in the flight (`GetShapeEndpoint.cs:142-146,165`) until a commit or `LiveTimeout` (`src/TerraBase/Features/Shapes/ShapeFlights.cs:80`) |
| Origin refusal | The API key is read from storage on every request, with no cache (`src/TerraBase/Features/ApiKeys/EngineApiKeyStore.cs:50-75`). Revocation deletes it (`EngineApiKeyStore.cs:131-157`) |

## 2. JWT (offline ts-auth token) revocation (row 10)

How TerraBase validates ts-auth tokens:

- **Signature and keys.** Signing keys come from OIDC discovery and then JWKS, cached in memory
  (`src/TerraBase/Features/UserAuth/TsAuthKeys.cs:11-17`).
  - Refresh: every `RefreshInterval` = 10 min (`TsAuthKeys.cs:30,120`), plus at most one refresh per 30 s when a
    token names an unknown key id (`TsAuthKeys.cs:33,65-78`; `UserAuthenticationHandler.cs:73-82`).
  - A failed fetch keeps the cached keys (`TsAuthKeys.cs:15-16`; test `UserAuthTests.Ts_auth_going_down_after_startup_keeps_the_cached_keys`).
- **Lifetime.** `RequireExpirationTime`, `ClockSkew` = 30 s (`src/TerraBase/Features/UserAuth/UserAuthenticationHandler.cs:31,181-182`).
  - Accepted tokens are cached by SHA-256 only while the issuer key list is unchanged and `now ≤ exp + 30 s`
    (`src/TerraBase/Features/UserAuth/ValidatedTokens.cs:30-37`).
  - ts-auth lifetimes: organization and project tokens 15 min (`src/TerraScale.Authentication/Features/Platform/PlatformGrants.cs:39,151`);
    OIDC access tokens 1 h (`src/TerraScale.Authentication/Features/Oidc/OidcServer.cs:53,124`).
- **Revocation list.** None. TerraBase never asks ts-auth whether a token was revoked. Per request, a project token is
  checked only against the project catalog, `ResolveProjectAsync` (`src/TerraBase/Features/UserAuth/UserAuthExtensions.cs:150-160`).

Bounds after ts-auth revokes:

| Path | Bound for new requests | Plus an already-fetched `K_e` (rows 8 and 12 of the V4 report) |
| --- | --- | --- |
| Token revoked, key kept | Remaining token lifetime + 30 s: ≤ 15 min 30 s for project tokens, ≤ 1 h 0 min 30 s for OIDC access tokens | + remaining epoch < 10 min |
| Signing key withdrawn from JWKS, ts-auth reachable | ≤ `RefreshInterval` (10 min) + one fetch (≤ `FetchTimeout`, 10 s), capped by exp + 30 s | + remaining epoch < 10 min |
| Signing key withdrawn, ts-auth unreachable | Remaining token lifetime + 30 s (cached keys are kept) | + remaining epoch < 10 min |

**Measured (key withdrawal):** `tests/TerraBase.Fast/Management/UserAuthTests.cs`:
`A_project_token_whose_signing_key_is_withdrawn_reads_shapes_until_the_next_scheduled_refresh_and_never_after`.

- A project token reads the shape bootstrap (`307`) before the withdrawal.
- It still reads after ts-auth withdraws the key.
- It gets `401` after one scheduled refresh (`RefreshInterval`, fired on a manual timer).
- The outcome is in section 4.

**Not measured:** revocation of a single token, which has no mechanism in TerraBase (exp only). Measuring it would need
a real-time wait until exp, which the Fast rules forbid.

## 3. Cross-region API-key revocation (row 11)

Measured live on `ts-vtest-tb` by another agent; the owner fills this in.

| Item | Value |
| --- | --- |
| Bound | _pending (measured live)_ |
| Evidence | _pending_ |

## 4. CI outcome of the new test

_pending: filled in from PR #301 CI._
