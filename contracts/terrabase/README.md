# I2 TerraBase SDK contract

**Unproven:** no live service or producer process ran. R8 #165 (cursor issue time/unnamed origins), repair/refill ordering, region discovery and R11 placement acceptance remain producer gates. R6-FIX has not published an operation-ID/ambiguous-outcome API. SDK tests establish client behavior against synthetic HTTP fixtures only.

The source pin is `TerraScale/terrascale@e30ffdc287530551b161b0130220be53d3c0475e`. `provenance.json` names every exact source export and its SHA-256; `producer/` contains those bytes. `artifacts/terrabase-e30ffdc-source.tar.gz` is a commit-stamped `git archive` of those producer files, obtained read-only from the pinned producer Git object, compressed with gzip mtime=0. Tests compare exports with the artifact's bytes and commit stamp, not just SDK-maintained hashes. This is source-artifact parity, not running-producer DTO/security/OpenAPI qualification; M2 against the final producer and SDK remains required. `fixtures/` contains synthetic JSON/SSE derived from those definitions, not captured service responses. `routes.json` records every client method/path, including existing uniqueness and the distinct declared JSON Schema endpoints. No TerraBase binary/project is used by SDK tests.

Pending R8 interoperability is separately pinned to PR #165 head `2b95f5ef0641d3a8b8695ec1989e02943befbef2` in `pending-r8.json`, with three exact exports in `producer-pending/r8/` and its own commit-stamped producer archive. `transaction-changes.sse` follows that source's initial cursor and two changes sharing an origin/version, with an id only on the last change. This unmerged producer snapshot does not replace the accepted source pin.

## Consumer signatures

Create one `TerraBaseDocumentsClient` with a caller-owned redirect-disabled `HttpClient` and an `AccessTokenProvider` acquiring a fresh action-scoped bearer token. Each request acquires its credential once; caller cancellation applies to acquisition. Region chooses `https://{region}.api.terrascale.tech` and sends `X-TerraScale-Region-Pin`. An explicit origin/resolver can be retained for authorized private services or HTTP fixtures. The SDK never disposes the caller's client.

`client.Project(projectId)` returns `TerraBaseProjectClient`:

```csharp
Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string name,
    string? homeRegion = null, IReadOnlyList<string>? replicaRegions = null,
    CancellationToken cancellationToken = default);
Task<TerraBasePage<TerraBaseManagedDatabase>> ListDatabasesAsync(
    string? after = null, int? limit = null, CancellationToken cancellationToken = default);
Task<TerraBaseManagedDatabase> GetDatabaseAsync(string databaseName,
    CancellationToken cancellationToken = default);
Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);
Task<TerraBaseManagedDatabase> SetDatabaseRegionsAsync(string databaseName,
    IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken = default);
Task<TerraBaseProjectUsage> GetUsageAsync(string? granularity = null,
    DateTimeOffset? from = null, DateTimeOffset? to = null,
    CancellationToken cancellationToken = default);
```

`client.Database(projectId, databaseName)` retains all CRUD, conditional-write, transaction and uniqueness methods and adds:

```csharp
Task<TerraBaseDatabaseUsage> GetUsageAsync(string? granularity = null,
    DateTimeOffset? from = null, DateTimeOffset? to = null,
    CancellationToken cancellationToken = default);
IAsyncEnumerable<TerraBaseChangesEvent> StreamChangesAsync(string? cursor = null,
    string? lastEventId = null, IReadOnlyList<string>? collections = null,
    CancellationToken cancellationToken = default);
Task<TerraBaseManagedDatabase> BranchAsync(string name, CancellationToken cancellationToken = default);
Task<TerraBaseSqlResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default);
Task<TerraBaseIndexList> ListIndexesAsync(string? collection = null, CancellationToken cancellationToken = default);
Task<TerraBaseIndex> CreateIndexAsync(string collection, IReadOnlyList<string> fields,
    string? name = null, CancellationToken cancellationToken = default);
Task DropIndexAsync(string name, CancellationToken cancellationToken = default);
Task<TerraBaseCollectionList> ListCollectionStatsAsync(CancellationToken cancellationToken = default);
Task<TerraBaseCollectionSchema> GetInferredSchemaAsync(string collection, CancellationToken cancellationToken = default);
Task<TerraBaseDeclaredSchema> GetDeclaredSchemaAsync(string collection, CancellationToken cancellationToken = default);
Task<TerraBaseDeclaredSchema> PutDeclaredSchemaAsync(string collection, JsonElement schema,
    CancellationToken cancellationToken = default);
Task<TerraBaseDeclaredSchema> DeleteDeclaredSchemaAsync(string collection, CancellationToken cancellationToken = default);
```

No registration change is required to use these instance methods. I5 owns root README/package release notes/version and fixture packaging; I2 does not edit shared files.

## Route, identity and scope

Project lifecycle/usage, branch, SQL and index methods use `/v1/projects/{project}`. `{project}` is the canonical ts-auth project ULID. `{database}` is its **name**, not the database ULID. The source DTO `ManagedDatabaseBody` uses camelCase and includes `id`, `projectId`, `name`, nullable `branchedFrom`, RFC 3339 `createdAt`, answering `region`, `homeRegion` and `replicaRegions`. The SDK keeps its existing five-argument record constructor and adds required JSON placement members; old incomplete server payloads are now rejected. List pagination is `items`/nullable `next`, query `after` and `limit` 1–100 (default **20**, from ManagementRoute).

Lifecycle reads/usage/SQL/index listing require `databases:read`; create/delete/placement/branch/index changes require `databases:write`. Collection/document/feed reads require `documents:read`; corresponding data/declared-schema changes require `documents:write`. The existing inferred/unique schema `/schema/{collection}` uses `databases:read` or `databases:write`. Declared Draft 2020-12 schema uses `/collections/{collection}/schema`, documents scopes, and an unwrapped JSON Schema body, not uniqueness declarations.

Project/database mismatch is 404, not disclosure of foreign ownership. Existing CRUD 409/412 preconditions are preserved. All mutations send once; no transport retries. Database create/placement/delete/branch use the source's home-region rules; replicated delete is 409 `database_replicated`. R11 may replace this merged placement model; do not infer the future model or a global region catalog from it.

Canonical document-side collection reads expose only `name` and decimal-string Unix-millisecond `created_at`. The distinct management-side `GET /schema` returns `CollectionList {items:[{name,documents,bytes}]}` through `ListCollectionStatsAsync`; it requires databases:read. `GetInferredSchemaAsync` explicitly names the inferred/unique view at `/schema/{collection}`; the existing `GetSchemaAsync` keeps that same behavior. Usage gives database/project totals; do not use those totals as per-collection counts. SQL returns `{matched,truncated,rows:[{id,value}]}`. Index list is `{items:[{name,collection,fields,state}]}`, not a bare array; state is `active` in this source pin. Create accepts `{collection,fields,name?}` and drop has no success body.

**Compatibility-report correction:** the earlier schema-path mismatch claim compared two distinct producer APIs. Both `/schema/{collection}` (inferred/uniqueness) and `/collections/{collection}/schema` (declared JSON Schema) are supported, alongside `/schema` counts. Replacing the inferred route with the declared route would break its DTO contract. Client/manifest/fixture parity covers all three.

## Usage qualification

Usage JSON exactly mirrors `Usage/IUsageService.cs`: 64-bit totals, a `period` (`from`, `to`, `granularity`, `reads`, `writes`), oldest-first `series` (`start`, `reads`, `writes`), and per-database `items` on project responses. Project items have empty series. `granularity` accepts hour/day only; minute is not accepted by these routes. `from` is inclusive/rounded down and `to` exclusive/rounded up. Default period is 24 hours or 30 days; maximum 840 hourly or 400 daily buckets. A project over 100 databases or an expensive query fails 400, without manufacturing zero usage.

**These counters are best-effort dashboard usage, not billing data.** A crash loses operations since the last successful count save. A 404/503/invalid payload stays an error rather than unavailable data becoming a successful zero-count response.

## Feed and routing errors

The changes endpoint is SSE with `change`, `cursor`, and terminal `error` events. A delivered SDK item's nullable `Cursor` is its explicit SSE `id` or the parser's previously received id; `Change == null` denotes a cursor checkpoint. Both event kinds can advance resume state. In #165 only the final change of an `(origin,version)` group has an id: preceding changes retain the checkpoint before that group. An interrupted group therefore replays; do not drop id-less changes or advance a cursor based on their version. Each change preserves `collection`, key `id`, `op` upsert/delete, decimal-string `version`, 16-hex-digit `origin`, and nullable current `document`. A null document does not imply delete: an upsert may no longer exist on the serving machine. Deduplicate at-least-once delivery by `(origin,version,collection,id)`.

The producer sends an initial cursor before changes, including on a request with no previous event ID. The SDK also delivers a valid id-less change received before any checkpoint with `Cursor == null`: this means no server checkpoint has arrived, not `now`, an empty cursor, or an advancement. Keep the last successfully applied checkpoint (or the original starting request if none), and update it only after applying the associated item. Persist consumer effects/deduplication and checkpoint consistently; the SDK owns none of that state and does not provide exactly-once application. Reconnection uses a fresh credential and the same filter. Tests cover disposal/cancellation halfway through a transaction, replaying the already delivered change, consumer deduplication, and reconnecting before/after the group. The client retains only parser buffers/current event; the amount of server replay is governed by its groups, retained history and cursor origins, not a fixed SDK replay limit.

No cursor replays retained history; `cursor=now` starts at the current head. `Last-Event-ID` takes precedence, so the SDK omits the query cursor when the header is supplied. Retain the **same collections filter** on reconnect, including in another region. The cursor is a vector; the SDK never decodes, compares, combines or rewrites it. #165 may revise its format, so only opacity is relied on. Metadata/collection drops are outside this feed; it is not a live SQL/query subscription.

400 `invalid_cursor` is `TerraBaseValidationException`; the SDK never resets it automatically. #165 rejects stored pre-#165 cursor formats with this same 400. If the application establishes that a previously valid checkpoint was invalidated by that producer upgrade, it must explicitly resynchronize: start a new feed with `cursor=now` and the same filter, **re-read** the data, and reconcile buffered/duplicate changes before replacing its old state/checkpoint. Do not resume by repeatedly sending the rejected Last-Event-ID, or substitute `now` for arbitrary invalid input without a re-read. This recovery is application-owned; the feed alone provides no atomic snapshot handoff or live-query guarantee. 410 `cursor_expired` is `TerraBaseCursorExpiredException`; explicitly restart with `now` **and re-read**, preserving application state until resynchronization succeeds. The same expired type is raised for a terminal SSE error after a 200. 429 is `TerraBaseRateLimitedException`; `RetryAfter` preserves an advisory server delay, without background reconnect/retry. Stream error codes `not_found`/`unknown_database`, `tenant_memory` and `storage_busy` map to not-found, rate-limited and unavailable exceptions. A clean end at token expiry or the 30-minute server lifetime returns normally; the consumer reconnects with its last accepted checkpoint and a fresh credential.

`TerraBaseWrongRegionException` preserves 421 and exposes validated answering `Region`, `HomeRegion`, `Regions`, and `ErrorCode`. An empty 421 with Fly replay still fails; the SDK does not follow replay or change hosts. Lifecycle DTOs and stream items expose the answering region. Invalid region header values are ignored. A server-reported placement is not global membership/discovery.

**Producer routing limitation (source-derived, not observed live):** a pinned regional `CreateDatabaseAsync(homeRegion: otherRegion)` can return 503 `region_unavailable`, rather than 421, after Fly replays it to the home region while retaining the pinned host (`DatabaseRegions.cs` and `Listeners.cs`). This remains `TerraBaseUnavailableException`, sent once; no SDK reroute/retry is added. The producer owner must qualify that interaction.

Streams use the in-box SSE parser behind a 4 MiB event bound, including unterminated lines and multiline data. There is no background queue. Response acquisition uses RequestTimeout; an active stream outlives it. Each blocked read has a fixed 45-second timeout, allowing the producer's 15-second heartbeat. Consumer cancellation/disposal closes the response and request; backpressure retains only parser buffers/current event. Caller-owned HttpClient timeouts remain the caller's responsibility.

## R6-FIX surface design — pending producer, not implemented

The merged producer rejects `Idempotency-Key`. There is no operation-ID header/body field, ambiguous status/DTO, retention policy or outcome route to implement. Until its API is published and merged, writes use their current contract; `TimeoutException`/transport loss leaves the outcome unknown, with no automatic retry or speculative reconciliation call.

The SDK adaptation will keep operation identity explicit at call sites: a write-options overload carrying the producer's operation ID, a typed `TerraBaseAmbiguousWriteException` carrying the same ID (an explicit non-success), and a database-scoped outcome lookup returning the producer's terminal/pending/unknown states. These are **surface proposals**, not callable APIs or finalized names/signatures. Existing preconditions must remain available. Caller retains the original operation identity and content; an explicit retry reuses them. Never generate a new ID while retrying, translate ambiguous/pending/unknown into success or abort, or replay across regions without the producer's documented authority/retention semantics. Final parameter placement, ID grammar, result model, wire mapping and reconcile method wait for the exact R6-FIX contract and matching tests.

## I5 handoff

Root version stays `0.0.2` here; I5 selects the next `0.0.X` candidate after accepted I1/I2 work. Copy `contracts/terrabase/fixtures/**`, `provenance.json`/`producer/**`, `pending-r8.json`/`producer-pending/**` and `artifacts/**` into test output with their relative paths, and include the reviewed contract inputs in the package. The current tests locate fixtures in their source worktree; route-manifest copying already exists. No new package is needed for the in-box SSE/TAR/gzip APIs on net10.0/net11.0. `TerraBaseChangesEvent.Cursor` is now `string?`: I4 must preserve its existing checkpoint on null and process the change, and retain consumer deduplication across reconnects. Update root README/release notes and the J8 compatibility-matrix row to distinguish both schema APIs plus counts, the source pin, usage qualification and unresolved producer gates. Root locks/CI/toolchain provenance stay I5-owned.
