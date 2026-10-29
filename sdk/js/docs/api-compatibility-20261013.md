# TerraBase API audit — JavaScript/TypeScript SDK

**Owner:** `TerraScale/ts-js`, branch `sdk/js-api-contracts-20261013` only. Imports, other SDKs, dashboard, server implementation, deployment, instructions and publishing configuration are excluded. No existing open SDK PR covered this work when checked initially or again before admission. PR creation was admitted by the user's explicit 2026-10-13 request; the preceding hold was respected.

## Exact inputs and qualification boundary

| Input | Exact revision / evidence |
| --- | --- |
| SDK audited `origin/main` | `eac7e3fca1178d30d81eb18ff35d8629e5f454e4` |
| Current actual server, `TerraScale/terrascale/main` | `e30ffdc287530551b161b0130220be53d3c0475e` (rechecked unchanged) |
| Current source evidence | [pin.json](../contracts/terrabase-current-source/pin.json): exact selected handler, DTO, validation and authorization bytes, each SHA-256 bound; no import files |
| Existing immutable qualification producer | `6cb97456b9b52a92a61856601cc025c4f2833518`, existing [release pin](../scripts/ci/terrabase-release.json) and generated historical OpenAPI |
| Draft SDK revision | API implementation commit: `2028c61d23b1746600ad42de7b9fe1f575cdc4cd`. The PR records the exact head including the ESNext/JSDoc configuration follow-up. |

Local HTTP fixtures are deterministic SDK wire/decoder tests. They do **not** qualify a producer. No current server build, live allocation, credentials, generated current OpenAPI, or immutable current producer runtime was used. The historical qualification pin was preserved. Historical OpenAPI drift checks remain intact; 25 current routes additionally verify registration, named/in-handler scope, media and success statuses against current source. The retained shape route is explicitly historical-only. All source hashes are checked, and negative parity tests cover each current route.

## Exact method/path/scope matrix

The full expanded paths, historical OpenAPI records and current producer operation IDs are in [sdk-v1-route-manifest.json](../sdk-v1-route-manifest.json). Below, `D = /v1/projects/{project}/databases/{database}`, `C = D/collections/{collection}`; database and collection selectors are names, while `project` is a canonical ULID. These abbreviations expand literally.

| SDK operation | Method and path | Scope | Request / success |
| --- | --- | --- | --- |
| `collection.get` | GET `C/documents/by-key?id=…` | documents:read | optional If-None-Match; 200 document |
| `collection.put` | PUT `C/documents/by-key?id=…` | documents:write | JSON object, conditional headers; 200/201 write |
| `collection.create` | POST `C/documents` | documents:write | JSON object; server-generated key; 201 write |
| `collection.patch` | PATCH `C/documents/by-key?id=…` | documents:write | merge-patch or ops JSON; 200 write with stored document |
| `collection.delete` | DELETE `C/documents/by-key?id=…` | documents:write | optional If-Match; 200 write |
| `collection.list` | GET `C/documents` | documents:read | limit/cursor query; 200 items/next_cursor |
| `collection.query/pages/items` | POST `C/query` | documents:read | typed JSON query; 200 items/next_cursor/count |
| `transaction.commit` | POST `D/transactions` | documents:write | bounded operations; 200 transaction outcomes; assertions also need documents:read |
| `db.collections.create` | POST `D/collections` | documents:write | JSON name; 201 collection including catalog id |
| `db.collections.get` | GET `C` | documents:read | 200 collection including catalog id |
| `db.collections.list` | GET `D/collections` | documents:read | limit/cursor; 200 items/next_cursor |
| `db.collections.drop` | DELETE `C` | documents:write | 200 collection |
| `collection.getSchema` | GET `C/schema` | documents:read | 200 schema/dialect |
| `collection.putSchema` | PUT `C/schema` | documents:write | raw boolean/object JSON Schema; 200 schema/dialect |
| `collection.deleteSchema` | DELETE `C/schema` | documents:write | 200 schema:null |
| `db.sql` | POST `D/sql` | databases:read | JSON `{sql}`; 200 matched/truncated/rows |
| `db.checkSql` | POST `D/sql/check` | databases:read | JSON `{sql}`; 200 check, including invalid SQL as ok:false |
| `db.explainSql` | POST `D/sql/explain` | databases:read | JSON `{sql}`; 200 check; invalid SQL is HTTP400 |
| `db.changes` | GET `D/changes` | documents:read | cursor/collections query, Last-Event-ID; 200 text/event-stream |
| `client.databases.create` | POST `/v1/projects/{project}/databases` | databases:write | name/homeRegion?/replicaRegions?; 201 database |
| `client.databases.list` | GET `/v1/projects/{project}/databases` | databases:read | after/limit query; 200 items/next |
| `client.databases.get` | GET `D` | databases:read | 200 database |
| `client.databases.update` | PATCH `D` | databases:write | replicaRegions, optional unchanged homeRegion; 200 database |
| `client.databases.delete` | DELETE `D` | databases:write | 204 with no response media/body |
| `client.databases.branch` | POST `D/branches` | databases:write | JSON name; 201 database |
| Retained `collection.shape` | GET `C/shape` | historical shapes:read | historical release only; absent from current source |

JSON requests/responses use `application/json` except the two PATCH media types and SSE. There is no invented SQL cursor, generic request, new custom header, alias to an import route, or unscoped data endpoint.

## Implemented behavior and negative cases

| Surface | Actual producer behavior / SDK correction | Exact source and deterministic tests |
| --- | --- | --- |
| Document identities | Document keys are 1–256 UTF-16 units, no controls/lone surrogates; query addressing preserves slash, percent, dots, Unicode and whitespace. Entity IDs remain canonical ULIDs. Unrelated root payload `id` is ordinary data; producer strips only an id equal to the key. | Current `DocumentRoute.cs`, `DocumentModels.cs`; [HTTP fixtures](../test/v1-http-contract.test.js), `src/ids.test.js` |
| Client-generated identities | Caller retry of PUT retains one key and identical body. Stable key prevents another address; it does not deduplicate commit versions, offsets or responses. POST/create, transaction and lattice PATCH have no operation-result deduplication promise. No SDK mutation retry loop. | Current `PutDocumentEndpoint.cs`, `DocumentRoute.CheckIdempotencyKey`; HTTP stable-key fixture |
| CAS | If-Match / If-None-Match and transaction preconditions are still implemented. Removing them would be a mismatch. Transaction ETags now carry the producer-required quotes. | Current `ETags.cs`, `TransactionEndpoint.cs`, document handlers; HTTP precondition fixtures |
| PATCH | Adds top-level nonzero signed Int64 inc and scalar add/remove; preserves stored document on direct and transaction results. Keeps set/unset/append and merge semantics. Server owns final canonical-JSON set-size validation; serializer escaping can differ from JS byte counting. | Current `DocumentPatch.cs`, `DocumentState.cs`, `PatchDocumentEndpoint.cs`; HTTP PATCH bounds/type/conflict fixtures and packed browser ops test |
| Pagination | Opaque next cursor, including empty final pages. Query iterators reject repeated/cyclic cursors and observe cancellation between buffered items. Count is sent only on the first query page. Document list and catalog return their actual distinct page DTOs. | Current list/query handlers; HTTP fixtures and packed browser pagination test |
| SQL | 16,384 UTF-16 statement units; one bounded result, at most 1000 rows, truncated flag. Exact row numbers retained; malformed/empty/count/duplicate-ID DTOs rejected. Check and explain have distinct invalid-SQL behavior. | Current `IQueryService.cs`, `SqlSubset.cs`, `QueryService.cs`, three handlers; [45 SQL fixtures](../test/v1-sql-http.test.js) |
| SSE | Cursor/change/error frames; Last-Event-ID overrides query cursor. Stable filter and latest cursor survive normal valid EOF reconnect. At-least-once delivery; decimal UInt64 version and origin hex; nullable current serving document. No reset on expired history, denial or malformed data. Pending read closes on return/caller abort. Network loss propagates for caller reconciliation. | Current `GetDatabaseChangesEndpoint.cs`, `DatabaseChanges.cs`, `DatabaseChangeModels.cs`, `ChangeCursor.cs`; [44 SSE fixtures](../test/v1-changes-http.test.js), real packed Chrome/Edge native-fetch SSE test |
| Database/schema APIs | Names in paths; project-scoped catalog resolver omits database. CamelCase database fields, nullable branch/creation inputs, empty DELETE204; raw boolean/object schema and schema:null detach. Validation does not grant region/project access. | Current managed database handlers, `ManagedDatabaseBody.cs`, `DatabaseRegions.cs`, three declared-schema handlers; [31 catalog fixtures](../test/v1-catalog-http.test.js) |
| Errors and request IDs | Producer still emits JSON `{error:{code,message,op,details}}`, not RFC9457. Preserve full parsed body, raw text, unknown fields, headers, Retry-After, ETag, op and a received X-Request-ID. Preserve malformed/empty denied bodies without automatic retry. Request-ID emission is not demonstrated by current producer source. | Current `ErrorResponse.cs`; HTTP/SQL/catalog/SSE denial fixtures, browser diagnostic test; generated TypeScript error fields |
| Authentication and authorization | Header-safe syntax validation only; producer validates signature, lifetime, at+jwt, exact configured issuer/audience, canonical ULID subject, project/database narrowing and exact scopes. Wrong project/database gives404; absent scope403. Legacy tb_ keys cannot grant database tooling scopes. No SDK issuer substitution, token rewriting, permission union, identity creation or access broadening. | Current `UserAuthenticationHandler.cs`, `UserAuthExtensions.cs`, `ProjectToken.cs`, `IApiKeyStore.cs`, `ApiKeyAuthenticationHandler.cs`; HTTP denial fixtures verify client behavior, not token-verifier qualification |

Current server documentation describes service subjects such as `tsk_…`, but `UserAuthenticationHandler.cs` requires `EntityIds.IsValid(sub)`, a canonical uppercase ULID. This is a producer documentation/implementation dependency; the SDK does not reinterpret credentials to bypass it.

## Supported package exports and types

| Export | Runtime / compatibility status |
| --- | --- |
| root | Current project-scoped API above; JS ESM and generated TypeScript declarations. Packed consumer installs and checks current SQL/SSE/catalog/schema methods plus typed diagnostics. |
| admin, management | Existing typed capability guards; no current management HTTP implementation is claimed for those entrypoints. Current database catalog lives on the root project client. |
| ts-auth | Existing hosted identity runtime; no issuer/audience or credential changes. |
| schema, sql | Existing legacy builder helpers; they are not the new server SQL or declared-schema operations. Preserved, not extended. |
| react, svelte, tanstack | Existing framework adapters/peer requirements. They bind legacy subscription capabilities; current root changes SSE is not silently substituted for authorized query snapshots. |
| better-auth | Named server-only entry; browser condition rejects it. Current named-operation producer qualification remains separate. |
| local, postgres | Deprecated Node-only legacy support; browser condition rejects them. Inventoried without new activation guidance or extensions. |

Browser-condition resolution loaded the nine allowed entries and rejected better-auth/local/postgres. This checks export resolution, not a bundled browser test of every framework peer. Root packed native-browser fetch is actually exercised. Compiler target/library are now ESNext with checked JSDoc in JavaScript; this follows the compiler’s newest language support without claiming every new built-in is available in Node22/24. Publishing configuration, package version and dependencies are unchanged.

## Browser 421 and ambiguous effects

[Fetch 421 assessment](fetch-421-assessment-20261013.md) records primary standards/browser references and real requests. Node one-shot mutation streams prevent 421 retransmission, including all four bodyless DELETE surfaces. Chrome/Edge currently make **one SDK fetch call but two wire requests** after421; a fixture that rejects before applying a mutation records zero effects. Both one-wire PATCH and DELETE assertions remain red. This is browser transport retransmission, not an SDK retry loop or evidence of duplicate producer effects.

Current `DatabaseRegions.UseTerraBaseDatabasePlacement` rejects before calling the endpoint; `Program.cs` registers it after authorization and before endpoint execution. Managed create/update/branch/delete handlers check placement/home region before mutation. These source paths support rejection-before-effect, but actual current end-to-end deployment qualification has not run.

The bounded integration dependency is producer/edge enforcement of **421 only before mutation**, across all write routes and forwarding paths. Buffered POST, PUT, PATCH, transactions and bodyless DELETE can be retransmitted by browsers. An ambiguous lost response or failure after commit additionally needs a published stable operation identity, atomic scoped deduplication, result replay/conflict/retention semantics, and reconciliation API before the SDK can offer automatic mutation recovery. Current routes reject Idempotency-Key and expose no such general contract. Stable document PUT addresses and CAS are useful existing controls, not a general exactly-once promise. Producer status/API changes belong to its owner; none were made or assumed here.

## Test evidence and remaining gates

[Recorded evidence](evidence/api-contracts-20261013.txt) contains commands, exact versions, red/green counts and unhidden failures.

| Check | Result |
| --- | --- |
| Original 56 regression fixtures against exact SDK main | 44 failed, 12 passed; same56 with fixes:56 passed |
| Public current API exports against main / draft | 2 failed / 2 passed |
| Pending SSE return before / after fix | Targeted regression red (timeout) / all44 SSE tests passed |
| Focused current suites, Node24.21.0 | 14 files,260 passed, zero skips |
| Same focused suites, independently verified Node22.23.2 | 14 files,260 passed, zero skips |
| Reviewed npm11.18.0 packed install, TypeScript consumer, helper/framework tests | 9 files,62 passed; one pre-existing live producer test skipped because no authorized live configuration was supplied |
| Typecheck / declarations / manifest | Passed; current25 source routes plus historical OpenAPI checks; each current route has negative drift tests |
| Real pinned Chrome154.0.8037.57 / Edge154.0.4258.37 | 20 passed,4 failed: PATCH and DELETE one-wire421 assertions, both browsers |
| Firefox155 / WebKit26.6 attempt | 20 launch failures from missing host libraries; no SDK/browser compatibility result, no skips added |
| Current immutable C# producer / generated OpenAPI / live identity verification | Not run; no current runtime qualification claimed |

Automatic eligible-region discovery, current authorized bounded-query snapshots/WebSocket reactive delivery, retained historical shape parity, and separately owned adapter capability gaps remain documented dependencies. Local fixtures do not resolve those gates. The full heavy release/security suite was not run while release CI was contended. The user's subsequent 2026-10-13 instruction admitted PR creation. GitHub reports repository auto-merge disabled; known red checks and missing producer qualification remain visible.
