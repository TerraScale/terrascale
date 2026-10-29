# Go SDK compatibility with implemented TerraBase

Audit ownership: TerraScale/ts-go only, in `sdk/go-api-contracts-20261013`.
Imports, other SDKs, dashboard, server changes, deployment, model settings and
AGENTS instructions are excluded. No customer credentials or private thread
transcripts were accessed. SDK and server checkout instructions were read;
this SDK contains no `.agents/skills` files. Modern Go Guidelines and Ponytail
were applied, with three user-requested GPT-6.1 Sol child tasks and independent
review of their changes.

## Exact source snapshot

- SDK baseline and fetched `origin/main`: `727ad3f4a84090aaeb6f324df77ff88cd8f35de5`.
- TerraScale/terrascale main: `e30ffdc287530551b161b0130220be53d3c0475e`.
- Merged issuer/audience PR #134: `2ae35da0c92392738fb7a079da43127a58b47b4b`.
- No open ts-go PR existed at initial inspection. The draft PR records its
  exact tested SDK implementation commit; there is no competing branch update.
- Server files were read with `git show <server-sha>:<path>` from existing
  local objects. That checkout's HEAD is different and was never edited,
  fetched, built or used as the contract snapshot.

The tables describe **source compatibility and deterministic local HTTP
fixtures**, not a deployed-server pass. Server paths below are relative to
`src/TerraBase` at the pinned commit. TS-748 is a requested future API shape;
where it contradicts implemented main, implemented source governs this draft.

## Data routes and identifiers

`P = /v1/projects/{project}`; `D = P/databases/{database-name}`;
`C = D/collections/{collection-name}`. Projects and generated/catalog IDs are
canonical uppercase ULIDs. Database/collection names remain lowercase protocol
names; document keys are separate opaque strings.

| Surface | Implemented producer and SDK behavior | Evidence / result |
| --- | --- | --- |
| Point documents | GET/PUT/PATCH/DELETE `C/documents/by-key?id=...`; exactly one query encoding, no ID path segments. UTF-8 keys are 1–256 UTF-16 units, without controls. | `Features/Documents/DocumentRoute.cs:211`; key/address fixtures cover slash, dot, percent, plus, Unicode, astral length, invalid UTF-8 and controls. Fixed SDK ULID-only rejection. |
| Generated creation | POST `C/documents` generates a ULID. `v1.NewID` also supplies a client key for PUT. Generated create/transaction results still require canonical ULIDs. | `CreateDocumentEndpoint.cs`; generated/malformed key fixtures. No invented idempotency endpoint. |
| Payload | The document is a JSON object; application `id` is an ordinary field independent of the key. PATCH acknowledgements contain the stored object. | `Primitives/DocumentJson.cs:48`, `DocumentService.cs:451`, `DocumentModels.cs:19`; fixed payload ban and omitted PATCH results; malformed object fixtures. |
| Collections | POST/GET `D/collections`, GET/DELETE `C`; body includes ULID `id`, name and decimal `created_at`. | `DocumentModels.cs:41`; create/info/list/drop fixtures and required-ID validation. |
| Transactions | POST `D/transactions`; quote opaque `if_match` entity tags; zero-filter `assert_count` is supported. One outcome per op; PATCH object and assertion outcomes required. | `ETags.cs:73`, `TransactionEndpoint.cs:158`; quoted version/count/result-count and negative fixtures. |

## Writes, conditions and retry safety

| Surface | Implemented producer and SDK behavior | Evidence / result |
| --- | --- | --- |
| If-Match/CAS | Still implemented, including 412, ETags, tombstones and conditional GET. Retained in the SDK. | `Features/Documents/ETags.cs`, `PutDocumentEndpoint.cs`; opaque-version and transaction fixtures. Removing it now would contradict main. |
| Idempotent retries | Main rejects `Idempotency-Key` and does not deduplicate writes. Retaining a client key does not grant exactly-once replay; concurrent writes/tombstones can change state. | `DocumentRoute.cs:246`; fixture verifies stable key/body, no unsupported header, CAS-only retry of known 429 rejection. |
| Uncertain outcomes | Unconditional POST/PUT/PATCH/DELETE and database creation are not replayed after transient/uncertain outcomes. CAS writes retry only known rejections; transport/commit uncertainty is not retried for mutations. | `v1/client.go`, `v1/version_test.go`, `v1/transport_contract_test.go`; original transport causes and API errors retained. Persistence/durability not simulated. |
| Credential refresh | A TokenSource implementing `Invalidate(string)` is refreshed once after 401, before an authorized operation could execute; same method/body/key/project/grant. No 403/404 or alternate-origin/scope fallback. | Local denial/refresh fixtures for HTTP and SSE; second 401 stops. Token acquisition failure sends no resource request and is not reported as a committed mutation. |

## Public database and SQL routes

| Surface | Implemented producer and SDK behavior | Evidence / result |
| --- | --- | --- |
| Database create | POST `P/databases` with `{name}` and `databases:write`. Existing name is an error; no automatic mutation retry. SDK `Database.Create` now uses it. | `Features/Management/CreateManagedDatabaseEndpoint.cs`; request/body fixtures. Old PUT explicitly refuses project JWTs in `CreateDatabaseEndpoint.cs`. |
| Database read/list/delete | GET `D`, GET `P/databases` (`limit/after` → `next`), DELETE `D`, using name selectors and project scopes. | `ProjectApiRoute.cs:14`; public metadata/list/delete fixtures. `Client.Databases`, `Database.Info` added; delete already matched. |
| SQL execute | POST `D/sql` with `{sql}`, `databases:read`; read-only SELECT subset, default LIMIT 100, max 1000 and 8 MiB results. Exact JSON numbers and object-valued rows. | `Queries/RunSqlEndpoint.cs`, `IQueryService.cs:12`; public SQL and malformed-row fixtures. Read-write SQL is not implemented by this producer. |
| SQL check/explain | POST `D/sql/check` / `D/sql/explain`, `databases:read`. Check can return `ok:false`; invalid explain returns an API error. | `CheckSqlEndpoint.cs`, `ExplainSqlEndpoint.cs`; `Database.CheckSQL` / `ExplainSQL` contracts, false/null/missing field fixtures. |
| Hidden management | Existing `/v1/management/organizations/...` methods still require membership/service identity and catalog database IDs. They were not remapped to project routes. | `Features/Management/LegacyRoutes.cs`; no new alias or silent authority translation. Public backup/index/branch/saved-query coverage remains incomplete. |

## Pagination and database change stream

| Surface | Implemented producer and SDK behavior | Evidence / result |
| --- | --- | --- |
| Document/query pages | GET `C/documents` / POST `C/query`, `limit/cursor` → nullable `next_cursor`; empty pages can have a next cursor. Count only on the first query page. | `DocumentModels.cs`, `QueryDocumentsEndpoint.cs`; fixtures cover empty continuation, termination, reuse, missing items and cancellation between documents. |
| Cursor cycles | Repeated/cyclic opaque cursors return a visible error. Tracking uses constant memory without a page cap. | `v1/query.go`; repeated/cyclic cursor and 100-page acyclic fixtures. No cursor decoding or snapshot guarantee is invented. |
| SSE connect/resume | GET `D/changes`, `documents:read`, optional `cursor` and at most 64 `collections`. Cursor checkpoints and changes carry event IDs. Clean EOF resumes with `Last-Event-ID`, original endpoint/filter and fresh source token. | `GetDatabaseChangesEndpoint.cs`; reconnect/header/filter/EOF fixtures. Cursors remain opaque across machines/regions. |
| SSE data/errors | `change` contains collection/key/op/version/origin/current document (object or null). `cursor` carries cursor. Terminal `error` contains the usual error envelope and stops iteration; HTTP status remains 200 after stream start. | `DatabaseChangeModels.cs`, `DatabaseChanges.cs:328`; exact-number, terminal error, empty/malformed/truncated/oversized event and error-body fixtures. |
| SSE refusal/cancellation | 400 invalid cursor, 410 expired cursor and 429 stream capacity are returned; no automatic reset to `now`. Cancellation closes the stream. EOF without a delivered resume cursor fails visibly. | HTTP refusal and cancellation fixtures. Live retention, at-least-once delivery and cross-region behavior remain unverified; see server PR #165 below. |

## Identity and permission contract (TS-756)

| Boundary | Implemented contract | Qualification |
| --- | --- | --- |
| Issuer/audience/type | Exact platform issuer `https://auth.terrascale.tech/terrascale`; audience includes `https://api.terrascale.tech`; `at+jwt` / `application/at+jwt`; project JWT requires ES256 and issuer JWKS `kid`; exp/nbf with 30 s skew. | `Configuration/TerraBaseOptions.cs:117,123`; `UserAuthenticationHandler.cs:44,114,171`. SDK treats tokens as opaque bearer credentials; fixtures do not validate cryptographic claims. |
| Project/database | `project_id` canonical ULID must equal route project. Missing/wrong project or unavailable trusted binding returns 404. Optional database name narrows access and listing; other databases are hidden. | `ProjectToken.cs`, `ProjectAccess.cs`, `DocumentRoute.cs`; denial propagation fixtures, no live issuer/binding qualification. |
| Tenant/actor | Only the platform issuer is trusted for TerraBase. Customer project-tenant issuers are for customer applications. Actual `sub` is canonical user/key ULID, not the opaque `<ULID>_<secret>` key string. Public authority comes from project grant, not client-supplied org/user headers. | `UserAuthenticationHandler.cs:90`; `docs/auth/platform-contract.md` claim table. SDK preserves requested project/scopes and sends no public impersonation header. |
| Supported permissions | Actual `TerraBaseScopes.All` and `ApiKeyScopes.ProjectTokenScopes` expose `databases:read`, `databases:write`, `documents:read`, `documents:write`. Missing scope is 403 `forbidden_scope`. | `src/TerraScale.Authentication/Features/Platform/ApiKeys.cs:28`; removed unsupported SDK `ScopeShapesRead`. No substitute/wider scope is sent. Stale docs mention usage/billing scopes not in this producer vocabulary. |
| Exchange/denials | `<ULID>_<secret>` exchanges at `{issuer}/oauth/token`; project and scopes are retained. Revoked/expired keys → OAuth `invalid_client`, unsupported/excess scope → `invalid_scope`, wrong target → `invalid_target`. API denies invalid credentials with 401, missing permission with 403, hidden project/database with 404. | Local token-to-resource cache/negative fixtures and existing `tsauth/tokens_test.go`; wrong issuer/audience/lifetime denials are scripted responses, not server enforcement tests. Deployed TS-756 acceptance remains open. |

The pinned platform-contract prose also retains a regional-audience paragraph
after the updated global claim table, and future `usage:*`/`billing:*` text.
The implemented constants, scope lists and validation code take precedence.
No server trust, issuer, audience, callback or permission policy was changed.

## Errors, usage, metrics and module identity

| Surface | Compatibility result | Evidence / limit |
| --- | --- | --- |
| Errors / request IDs | Main uses `application/json` `{error:{code,message,op,details}}`, not RFC 9457. `APIError` preserves full raw body, status, op, ETag, Retry-After and optional `X-Request-ID`; interrupted/oversized errors preserve bounded bytes and read cause. | `Hosting/BadRequests.cs`, `Errors/ErrorResponse.cs`; malformed/empty/plain error, denial and body-read fixtures. Main has no universal X-Request-ID contract. RFC 9457 migration depends on producer implementation. |
| Usage | Public GET `P/usage` / `D/usage` with UTC hour/day windows; `databases:read`. Exact int64 counters, zero distinct from missing; required counters checked. | `Usage/GetProjectUsageEndpoint.cs`, `GetDatabaseUsageEndpoint.cs`; best-effort observations may lose unsaved increments and do not qualify billing. |
| Analytics/replication | `/v1/projects/{project}/{analytics,replication}` with range/database/region only. Nested database/region items, series, nullable sampled storage, latency, boolean divergence, `convergenceMs`, bandwidth kind. No pagination. | Actual `IUsageAnalytics.cs:7–35`, `AnalyticsWindow.cs:9`; replaced provisional flattening/pagination/meter fields; [detailed contract](replication-analytics.md) and deterministic fixtures. |
| Module/imports | `go.mod` remains `terrascale.tech/sdk`, Go 1.25.0; imports root, `/v1`, `/tsauth`. Packaged consumer uses an isolated file proxy, no replace or publication. | Consumer compiles all three import paths and TokenSource interface. HTTPS `?go-get=1` probes for root/v1/tsauth returned HTTP 403 on 2026-10-13; external vanity acquisition is unqualified. Routing unchanged. |
| Separate surfaces | Root SQL builders and pgx listener execution are separate from HTTP SQL. Removed historical shape routes remain absent; the proposed v1 shape protocol now has a separate [SDK contract](shapes.md) and fixture coverage. Imports were not inspected or changed. | No external PostgreSQL/listener journey, database service or deployed shape qualification was run. |

## Test evidence

Every local build/test/lint used `nice -n 19`. Fixtures use synthetic local
credentials and isolated `httptest` servers or bounded scripted transports.
No TerraBase binary was downloaded/built/launched and no production write ran.

Both final suites passed 596 tests/subtests with zero failures and the two
pre-existing live skips below. Exact SDK commit and commands are recorded in
the draft PR:

1. `nice -n 19 go test -race -count=1 -json ./...` using Go 1.27.2, including
   the isolated packaged-module consumer.
2. `nice -n 19 go vet ./...`, formatting and `git diff --check`.
3. `GOTOOLCHAIN=go1.25.0 nice -n 19 go test ./... -count=1` for the declared
   minimum Go version.

| Package | Go 1.27.2 race | Go 1.25.0 |
| --- | --- | --- |
| `terrascale.tech/sdk` | PASS, 4.114 s | PASS, 10.695 s |
| `terrascale.tech/sdk/v1` | PASS, 1.539 s | PASS, 0.259 s |
| `terrascale.tech/sdk/tsauth` | PASS, 1.174 s | PASS, 0.100 s |
| `terrascale.tech/sdk/internal/entityid` | PASS, 1.016 s | PASS, 0.003 s |

Final targeted Go 1.27.2 race and Go 1.25.0 checks also passed for the
TokenSource/unsupported-scope fixtures after aligning OAuth `invalid_client`
with its actual 401 status. The production behavior remained unchanged.
Independent reviews found and fixed terminal SSE errors, empty SSE events,
PATCH object acknowledgements and object-valued SQL rows before final suites.

Two pre-existing live tests (`TestStatusLatencyLive` and
`TestTSAuthOIDCRealProvider`) require separate authorized inputs and are skipped
by the unchanged baseline. Those skips are not qualification evidence. The
qualification command package has no test files. No skip was added, failure
disabled or test removed to make this change pass.

## Residual gaps and producer dependencies

1. TS-748's CAS removal, deduplicated client-ID writes, read-write SQL and RFC
   9457/request-ID rollout need an implemented producer contract before SDK
   changes. This draft deliberately reports their absence.
2. TS-756 needs authorized deployed positive and negative issuer/audience,
   signature, expiry/revocation, project/database/tenant and scope evidence.
   Local fixtures establish only forwarding, refusal propagation and no
   fallback. Existing cached project tokens follow the producer's validity;
   immediate revocation is not claimed.
3. Main's changes-feed follow-up PR #165 is open at
   `2b95f5ef0641d3a8b8695ec1989e02943befbef2`: missing-origin replay/retention
   expiry corrections. The SDK passes cursors through, cannot repair server
   event loss and makes no live cross-region guarantee.
4. Public backup/restore, branches/placement updates, indexes/recommendations,
   JSON schema/query shapes/saved queries and general management commits
   remain SDK coverage gaps. Existing legacy methods are not proof of
   compatibility with their public JWT routes.
5. Vanity `go-import` discovery returned 403; repository code and packaged
   module identity pass independently. No module-path rename, publication,
   vanity routing change, trust activation, merge or deployment is included.
