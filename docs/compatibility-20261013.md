# Rust SDK compatibility audit

Ownership: TerraScale/ts-rust only, isolated branch `sdk/rust-api-contracts-20261013`.
No open Rust PR existed when the audit began. Independent audits covered SQL,
errors/auth/SSE and String-ID test adaptations. A subsequent read-only review found
unconditional-304 diagnostic loss and retry of truncated denied responses; both have regression fixtures.

SDK baseline/current origin/main at inspection: `8a5d983684a5b26f2992a9e7ee07969a7bc2c12a`.
Server origin/main source snapshot: `e30ffdc287530551b161b0130220be53d3c0475e`
(committed 2026-10-12 22:38:35 UTC). Exact candidate SDK commit and final test counts are
recorded in the draft PR; a source file cannot contain its own Git commit hash.

Authority is implemented code at that SHA: `docs/terrabase/api.md`,
`docs/terrabase/id-contract.md`, endpoint Configure/Handle methods and middleware.
Issue descriptions were not used to invent endpoints. Server files were read through GitHub's
contents API into a temporary source snapshot. Imports were excluded entirely.
No server builds, live credential files, private transcripts or secret-bearing histories were read.

`P = /v1/projects/{canonical uppercase ULID}`; `D = P/databases/{database name}`;
`C = D/collections/{collection name}`. All calls retain bearer project authorization.

| Surface | Implemented server contract | Rust result / evidence | Remaining dependency |
| --- | --- | --- | --- |
| Project and entity IDs | ULID projects/database/collection/generated-document IDs; names select databases/collections | ULID project validation before dispatch, String IDs and metadata codecs | UUID-project callers must migrate; live Atlas ULID acceptance unqualified |
| Raw document keys / changed routes | GET/PUT/PATCH/DELETE `C/documents/by-key?id=…`; keys 1–256 UTF-16 units, no controls | Fixed removed point paths; one query encoding; reserved characters and Unicode remain distinct | No fallback to removed routes |
| Documents / creation | GET200 or conditional304; PUT200/201; PATCH/DELETE200; POST `C/documents` 201/generated ULID | Correct statuses, full-range string offset, exact raw numbers, identity checks, malformed/empty acknowledgements | No current-server storage qualification |
| Preconditions / CAS | `ETags.CheckPrecondition`, `CheckVersion` and transaction `if_match` still implemented | Existing headers/transaction preconditions retained and tested | Removing If-Match contradicts this exact main; revisit only with implemented server change |
| Client IDs / retries | Stable caller keys permitted; `Idempotency-Key` rejected; repeated PUT is a new commit | UUIDv5 helper; explicit uncertain retry preserves key/body; no automatic mutation replay on disconnect/503 | No receipt deduplication; explicit retry may overwrite concurrent edits |
| Collections | POST/GET `D/collections`, GET/DELETE `C`; ULID `id`, string `created_at`; limit1–200/cursor | Preserve collection identity; serde roundtrip; current fixtures | Schema endpoints and counter/set add/remove helpers absent |
| Databases / changed create route | POST `P/databases`, `{name}`, `databases:write`, 201 managed body with placement | Replaced legacy PUT; full ULID/placement metadata and serde roundtrip | GET/list/update/delete/branch management wrappers absent |
| Transactions | POST `D/transactions`; String keys, generated creates, op-indexed errors/preconditions | String IDs across request/results, matching-op and generated-ULID checks | No replay receipt support on this server |
| Queries / pagination | POST `C/query`, GET `C/documents`; opaque `next_cursor`, null terminates; empty pages may continue | Existing filter/sort/projection/count; missing/empty/cyclic cursors fail; no request after termination | `lower`/`explain` remain server rejections; collection-list iteration manual |
| HTTP SQL / recent explain changes | POST `D/sql`, `D/sql/check`, `D/sql/explain`, `{sql}`, `databases:read`, exact200 | Added run/check/explain; RawValue rows/plans, invalid SQL distinctions, denied/status/cancel and read-retry fixtures | Server SELECT subset only: no parameters/cursors/join/aggregation; truncation explicit |
| Change-stream SSE | GET `D/changes`, `documents:read`; Last-Event-ID overrides cursor; cursor=now; change/cursor/error events, heartbeat15s; 410 cursor_expired/429 changes_busy | Exact endpoint/source audited; no Rust SSE consumer | Reconnect, Last-Event-ID, retention and SSE cancellation remain SDK work; native poll differs |
| Errors / RFC9457 / request IDs | Actual writer emits application/json nested ErrorBody; inspected writer does not generate X-Request-ID | Project HTTP errors retain actual status/full bytes (bounded prefix for size/stream failures), typed source/header ID; defensive RFC9457, unknown/exact-number extensions and spoof tests | RFC9457 emission/request-ID generation are server/gateway dependencies; live gateway unverified |
| Issuer / audience / project / permissions | Trusted issuer/JWKS, exact audience, token type/signature/lifetime/sub; exact project/database before scope; invalid token401, wrong target404, missing scope403 | Only caller bearer; no grant broadening; denied fixtures; truncated denied responses never replay | JWT enforcement server-owned/inspected, not independently executed; credential exchange/acquisition unqualified |
| Cancellation / bounded streaming | Futures may be dropped while request or JSON body is pending | Socket tests confirm connection closure/no detached retries; malformed/empty/oversized bodies and wrong-region acks retain diagnostics | No SSE or current-server PG stream qualification |
| Cargo features / older protocols | default=[]; sole optional `postgres` feature adds old PG transport | New project APIs compile without flags; all-feature tests include PG TLS/codec fixtures; no flags added | Native UUID/shard API, `/v1/dashboard` and PG are older-producer APIs, not current C# compatibility |

## Exact source evidence

All server links resolve to the same immutable commit:

1. [Point routes/keys/idempotency](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Documents/DocumentRoute.cs),
   [ID contract](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/docs/terrabase/id-contract.md),
   [document bodies](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Documents/DocumentModels.cs),
   [actual ETags/CAS](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Documents/ETags.cs).
2. [Management database POST](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/CreateManagedDatabaseEndpoint.cs),
   [project routes](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/ProjectApiRoute.cs),
   [SQL bodies](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/IQueryService.cs),
   [current explain route](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/ExplainSqlEndpoint.cs).
3. [JWT validation](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/UserAuth/UserAuthenticationHandler.cs),
   [project check ordering](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/UserAuth/UserAuthExtensions.cs),
   [scope policies](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/ApiKeys/ApiKeysExtensions.cs),
   [actual error writer](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Hosting/BadRequests.cs).
4. [SSE endpoint/cursor precedence](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Documents/GetDatabaseChangesEndpoint.cs),
   [SSE heartbeat/retention/capacity/cleanup](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Documents/DatabaseChanges.cs).

## Deterministic validation

Final results: **78** default-feature contract/regression tests, **239** all-feature local tests,
**1** doctest; all passed with zero failures and zero ignored tests in these selected targets.
Formatting and all-target/all-feature Clippy with warnings denied passed.
[Full command output](../.agents/evidence/rust-api-contracts-20261013.log) is committed evidence.

Local commands use `nice -n 19`, Rust 1.98.1 and unchanged Cargo.lock. The machine's mise
shim did not locate its installed toolchain; validation used the existing real binary under
`/home/mariogk/.rustup/toolchains/1.98.1-x86_64-unknown-linux-gnu/bin`.
No compiler/model/CI settings were edited.

1. `cargo test --locked --no-default-features --test csharp_current_contract --test csharp_problem_contract --test csharp_sql_contract --test v1_fast`
2. `cargo test --locked --all-features --lib --test auth_idp --test auth_idp_edge --test native_discovery --test postgres --test serde_base64 --test transport --test typed_documents --test typed_routing --test v1_fast --test v1_proxy --test csharp_current_contract --test csharp_problem_contract --test csharp_sql_contract`
3. `cargo fmt --all --check`, `cargo clippy --locked --all-targets --all-features -- -D warnings`, `cargo test --locked --all-features --doc`.

Fixtures use loopback HTTP or throwaway test TLS identities and synthetic credentials only.
They prove SDK wire behavior, not actual JWT enforcement, storage, serving or deployment.
No tests were deleted, newly ignored or changed to silently pass missing prerequisites.
Legacy fixture assertions were adapted to String IDs without changing expected wire behavior.

## Release dependencies

The current-source client must remain a draft until live qualification with a current server
artifact and an authorized disposable project-token fixture. The required runner in
`scripts/terrabase/release.json` is pinned to older TerraBase
`263598b47ffc79398db394550e3c074acf4fc57d` (`ci-263598b47ffc79398db394550e3c074acf4fc57d-run-15005`),
uses UUID projects/legacy tb_ credentials and expects old routes/generated UUIDs. It cannot
qualify these changes. Neither `csharp_live` nor `v1_live` ran; existing ignore/prerequisite
behavior is not counted as passing evidence. No release artifact or deployment script changed.

SSE, remaining management/schema/set helpers, automatic credential acquisition, live Atlas ULID
discovery, actual RFC9457/request-ID emission and native/PG retirement remain separate work.
Imports, other SDKs, dashboard, model configuration, AGENTS instructions, security/access
changes, deployment, package publication and merging were outside this ownership.
