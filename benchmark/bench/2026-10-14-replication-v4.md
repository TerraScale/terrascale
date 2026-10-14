# TerraBase V4 security review of free shape reads — 2026-10-14

Scope: tenancy, cache keys, revocation bound and TBS1 metadata leakage of shape reads (FR-34, plan row V4), plus a
review of A1 enforcement PR #271. Raw outcomes: `2026-10-14-replication-v4.json`.

## Provenance

- Tested commit: `4682879458008894ae1379e04eb4b39a24d9d56b` (branch `feat/repl-v4-shape-security`, base `origin/main` `a439e1d1`).
- Host: `mariogk-dev` through `ts-heavy-any`, Debug configuration.
- `make terrabase-build`: exit 0, 0 warnings. `make fmt-check`: exit 0.
- Fast filtered `ShapeSecurityTests`: 5/5 passed. `ShapeCryptoTests`: 23/23 passed (21 existing tests and 2 new ones).
- Not run: the full Fast suite, Slow, E2E and any Fly or production action.

## Results

| # | Property | Test | Result | Measured bound / number | Evidence |
| --- | --- | --- | --- | --- | --- |
| 1 | Tenancy: another organization, a sibling-project key or a sibling-database key never reaches a shape (bootstrap, page, chunk), its key or its ETag | `ShapeSecurityTests.No_other_organization_project_or_database_key_reaches_a_shape_its_key_or_its_etag` | PASS | 24/24 refusals: 3 principals × 4 targets × 2 `If-None-Match` (victim ETag and `*`). All are `404 unknown_database`, `no-store`, with no ETag, no `TB-Shape-Handle` and no handle in the body | `DocumentRoute.cs:96-130` (`ApiKey.Reaches`, `IApiKeyStore.cs:51-57`); tenant checks run before `CheckNotModified` (`GetShapeEndpoint.cs:89-112,192`) |
| 2 | Tenancy: a handle from another tenant, collection or filter is refused, and the refusal names only the caller's own handle | `ShapeSecurityTests.A_handle_from_another_tenant_collection_or_filter_is_refused_and_answers_only_the_callers_own_handle` | PASS | 6/6 `409 must_refetch` (shape and key routes), `no-store`, with no `key` | Handle hash binds org, project, database, incarnation, replica, collection name and id, and filter (`ShapeHandle.cs:38-42`); key route `ShapeService.cs:113-135`; flight key holds database and handle (`ShapeFlights.cs:132-133,489-491`) |
| 3 | Tenancy: deleting a database and recreating it under the same name refuses the old handle | `ShapeSecurityTests.A_database_deleted_and_recreated_under_the_same_name_refuses_the_old_handle_on_the_shape_and_key_routes` | PASS | 4/4 `409` (chunk, snapshot page, live, key), each naming the new handle | New `DatabaseId` and `Incarnation` in the handle fields (`ShapeHandle.cs:38`) |
| 4 | Cache policy per origin response class; no two tenants or filters share a cache key | `ShapeSecurityTests.Every_shape_response_class_carries_its_cache_policy_and_no_two_tenants_or_filters_share_a_cache_key` | PASS | 4 shapes (2 tenants with identical data, 2 filters): 4 distinct continuation URLs and 4 distinct ETags. `307`/`200`/`304` are `private`, not `public`, with no `s-maxage` and `Vary: Authorization`. 8 error classes (`401`, `403`, `400`, `404`, `409` on both routes) are `no-store` with no ETag | `GetShapeEndpoint.cs:24,190-201,240-258`; `BadRequests.cs:45,96`; `ErrorResponse.cs:38` |
| 5 | The key endpoint is `private, no-store` | `ShapeCryptoTests.The_key_endpoint_releases_the_current_epoch_key_derived_from_the_frozen_project_key` (existing) | PASS | `200` is private and no-store; the body holds `K_e`, never `K_d` | `GetShapeKeyEndpoint.cs:40` |
| 6 | Shared-cache headers on T1 objects (`public, max-age=31536000, immutable`) | — | BLOCKED (FR-24 not written; FR-22 #283 unmerged; A1 #271 unmerged) | — | Requirements below |
| 7 | Revocation: a revoked API key's new requests are refused at the origin | `ShapeSecurityTests.A_live_poll_…` and `ShapeCryptoTests.A_revoked_holders_epoch_key_…` | PASS | 0 requests accepted after revocation on the serving machine (`401` on the next shape and key request). There is no API-key cache | `EngineApiKeyStore.cs:50-75` reads the key per request; `RevokeProjectKeyAsync` deletes it (`EngineApiKeyStore.cs:131-157`) |
| 8 | Revocation: an already-fetched `K_e` opens newly sealed objects only until its epoch ends | `ShapeCryptoTests.A_revoked_holders_epoch_key_opens_objects_sealed_until_the_epoch_ends_and_none_sealed_after` | PASS | Worst case measured on a fake clock: 598 s after revocation (key fetched at second 1, object sealed at second 599). The object sealed at second 600 does not open. Bound: < 600 s | `ShapeCrypto.cs:74,87,169-174` |
| 9 | Revocation: a live poll admitted before revocation | `ShapeSecurityTests.A_live_poll_admitted_before_its_key_is_revoked_delivers_at_most_one_answer_within_the_live_timeout` | PASS (bound held; finding F1) | The parked poll delivered a commit made after revocation, 110 ms after it. Bound: `LiveTimeout` = 20 s from admission. The follow-up request got `401` | Authorization runs once at admission (`GetShapeEndpoint.cs:75,142-146,165`). Kept as is; bound documented in [`2026-10-14-replication-v4-revocation.md`](2026-10-14-replication-v4-revocation.md) section 1 |
| 10 | Revocation: JWT (offline ts-auth token) | `UserAuthTests.A_project_token_whose_signing_key_is_withdrawn_reads_shapes_until_the_next_scheduled_refresh_and_never_after` | Key withdrawal: see the revocation file. Single-token revocation: NOT MEASURED (TerraBase has no revocation list, so exp is the only cut-off, and a Fast test cannot wait in real time for exp) | Key withdrawal ≤ 10 min (`RefreshInterval`) + 10 s fetch. Token revoked: exp + 30 s (project ≤ 15 min 30 s, OIDC ≤ 1 h 0 min 30 s). Then + < 600 s epoch | [`2026-10-14-replication-v4-revocation.md`](2026-10-14-replication-v4-revocation.md) section 2 |
| 11 | Revocation: propagation to another region's machine | live, `ts-vtest-tb` (`scripts/revoke-timing.py`) | MEASURED (finding F3) | first 401 after the revoke's 204: sjc 53/129/640 ms, fra 84/94/634 ms (min/median/max, 5 trials); 2 of 5 trials accepted a request sent ≤ 341 ms after the 204 | [`2026-10-14-replication-v4-revocation.md`](2026-10-14-replication-v4-revocation.md) section 3 |
| 12 | TBS1 ciphertext metadata | `ShapeCryptoTests.A_sealed_object_reveals_only_its_epoch_key_id_salt_and_exact_plaintext_length_and_the_key_id_links_a_projects_shapes` | PASS (property measured; finding F2) | Size = plaintext + 70 + 20 × frames exactly (lengths 0, random 1–4096, 1 MiB). The cleartext header shows epoch `E` (10-minute seal time) and `KID`. `KID` is the same for every shape of the project (1 distinct value across 2 handles), and its ULID gives the project key's creation time to the millisecond. No org, project, database, collection or handle bytes appear. Salt is unique per object (4/4) | `ShapeCrypto.cs:180-235`; spec `shape-reads.md` section 12, `free-reads.md` 3.9 ("sizes and object counts") |
| 13 | Manifest and Tigris URL/header metadata | — | BLOCKED (FR-22 #283 unmerged) | — | — |
| 14 | Internal listener requires the peer credential (C1) | PR #271 tests (not run here) | BLOCKED (A1 #271 unmerged; review only) | — | Review below |

## Findings

- **F1 (Low, spec gap): a live poll outlives revocation by up to 20 s.** A long poll admitted before an API key is revoked
  still answers with plaintext committed after the revocation. The bound is one answer, at most `LiveTimeout` (20 s)
  after admission. Measured: 110 ms. Section 13 states the bound only for keys. Fix options: document the bound in
  `shape-reads.md` section 13, or re-check the API key when the flight wakes, before answering. No fix is in this branch.
- **F2 (Low/informational, beyond the spec's stated leakage): the TBS1 header links a project's objects and dates its
  key.** `free-reads.md` 3.9 lists only sizes and object counts as visible. An object holder also learns three things:
  - `KID`, constant across every shape and object of the project until key rotation. Objects are linkable to one
    project even though the Tigris keys are hashed.
  - The project data key's creation time to the millisecond, from the ULID.
  - The seal time to 10 minutes (`E`).

  Sizes are exact; there is no padding. Either document these, or derive a per-handle opaque key reference in the
  header (a format change: TBS2) and pad to size buckets.

No defect in tenancy or cache keying was found on `main`. No production code changed.

## What FR-24 must guarantee

1. `public, max-age=31536000, immutable` goes only on TBS1 objects stored in Tigris, set as object metadata at the
   one-time PUT. No origin (T0) response ever becomes `public` or gets `s-maxage`. Row 4 is the regression gate.
2. Write-once: the PUT is conditional (`If-None-Match: *`). An object key (`v1/shapes/<sha256(shape_id‖0‖binding)>`) never
   maps to different bytes, so `immutable` stays true. An object is mirrored only after the FR-25 durable-publication gate.
3. Objects must carry no tenant, handle, offset, collection or filter in their metadata, headers or `Content-Disposition`.
   They must have no `Content-Encoding` (compression would leak length) and `Content-Type: application/octet-stream`.
4. Tigris errors (`403` expired signature, `404`) must not be stored long by any cache. Presigned URLs expire no later
   than `(E_now + 1) × 600`. The manifest and key responses stay `private, no-store`.
5. Enabled only after A1 (#271) is enforced, so a forged peer write cannot be sealed and shared. FR-24 tests must also
   state the F2 leakage (exact size, `E`, `KID`) or remove it.

## A1 #271 review (head `2d5a33e3`, not pushed to, nothing posted)

- **Correct:** the check runs in `UseTerraBaseListeners` after routing and before the stop gate, QoS, catalog gate,
  authentication and endpoints (`Program.cs:135-153`, PR `Listeners.cs:+275-279`).
  - It covers every Internal request, including unknown routes, probes, administration and gRPC (HTTP 401, which
    gRPC maps to `Unauthenticated`).
  - Comparison is `CryptographicOperations.FixedTimeEquals` on equal-length, 64-hex values. A repeated or
    comma-joined header is refused (`PeerCredential.cs:42-51`).
  - Startup fails closed on Fly when the secret is missing or malformed, exiting 2 before storage opens
    (`Program.cs:39-49`, `PeerCredential.cs:26-40`). Off Fly, an absent secret refuses every internal request.
  - The configuration error names the variable, never its value. No HTTP header logging exists (`UseHttpLogging` is absent).
- **R1 (Medium, operational): rotation is an outage.** Only one credential is accepted, so rotating means a
  coordinated restart of ts-auth and TerraBase in every region. gRPC peer streams are authenticated only at stream start,
  so streams opened with the old secret keep flowing until they reconnect. Suggest accepting a current and a next
  secret during rotation, both from Fly secrets. That is a second secret, not a switch.
- **R2 (Low): uppercase hex passes the format check** (`char.IsAsciiHexDigit`), but the comparison is bytewise.
  Peers must hold byte-identical case. Normalize, or require lowercase at startup.
- **R3 (Info): pressure metering runs before the check** (`PressureExtensions.cs:39-48`), so unauthenticated internal
  traffic is counted in pressure metrics. Usage metering skips Internal (`UsageRequestMetering.cs:29`).
- **R4 (Info): the blast radius is shared.** Anyone holding the secret (including every ts-auth machine) has peer
  and operator privileges. It is documented in the PR, with no mTLS or machine binding.
- Fly checks use the client port (`src/TerraBase/fly.toml:26-34`) and metrics use 9091, so enforcement does not break
  platform probes. Not verified here: the PR's own tests and E2E (not run on this host).
