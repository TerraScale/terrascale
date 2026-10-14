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

Measured live on `ts-vtest-tb` (gru/sjc/fra, shared-cpu-2x 512 MB, image of `3c459a5f` = main + #307 + #309 + #314),
2026-10-14T20:23:03Z–20:23:15Z, 5 trials. Each trial mints a key for a test database, revokes it through gru
(`DELETE`, 204 in 232.7–250.3 ms round trip), and polls sjc and fra every 50 ms from 8 connections. Times count from
receipt of gru's 204.

| Region | first 401 after the 204: min / median / max | trials with a request accepted after the 204 |
| --- | --- | --- |
| sjc | 53.0 / 129.1 / 639.8 ms | 1 of 5 (trial 3: sent 341.1 ms after) |
| fra | 83.6 / 93.7 / 633.6 ms | 2 of 5 (trial 3: 291.1 ms; trial 5: 4.3 ms) |

| Item | Value |
| --- | --- |
| Result | MEASURED (no bound is specified for cross-region revocation; finding F3) |
| Bound | Observed ≤ 640 ms after the revoke's 204, under no partition. Not bounded in code: it is the catalog replication lag, so a partitioned region keeps accepting the key until it rejoins |
| Evidence | `benchmark/bench/2026-10-14-replication-vtest/evidence/v4-revoke-3c459a5f.jsonl` (raw per trial) and `v4-revoke-3c459a5f-stderr.txt`, script `scripts/revoke-timing.py`, on branch `feat/repl-v5-harness` (PR #303) |

**F3 (Low):** the revocation's 204 does not wait for the other regions. A revoked API key stays usable in another region
for the catalog replication lag (≤ 640 ms observed; unbounded during a partition). Options: document the bound in
`shape-reads.md` section 13 and the API-key docs, or make revocation wait for remote durability like document writes (A3).

## 4. CI outcome of the new test

Read from the completed job log, not the check status. Head `9872dc928c17806e5f9575b42b115d696f8e32cc`.

| Item | Value |
| --- | --- |
| Test | `UserAuthTests.A_project_token_whose_signing_key_is_withdrawn_reads_shapes_until_the_next_scheduled_refresh_and_never_after` |
| Outcome | **NOT VERIFIED: not found in the logs.** The CI Fast run prints only a suite summary, never test names. The name appears 0 times in the `terrabase` and `test` job logs, and the run has no test-result artifact (only the docker build record) |
| Duration | Not in the logs |
| Suite log line | `terrabase` job, 2026-10-14T19:55:08Z: `Test run summary: Passed! - …/TerraBase.Fast.dll` with `total: 1895`, `failed: 0`, `succeeded: 1895`, `skipped: 0` |
| Run | https://github.com/TerraScale/terrascale/actions/runs/38625957004 |
| Job | https://github.com/TerraScale/terrascale/actions/runs/38625957004/job/116065633084 (`terrabase`, success); https://github.com/TerraScale/terrascale/actions/runs/38625957004/job/116073039205 (`test`, success) |

The suite summary has zero failures and zero skips, but this does not show by name that the test ran.
