# I2 TerraBase SDK contract

**Unproven:** no live service or producer process ran. Cursor recovery, repair/refill ordering, region discovery and placement remain live interoperability gates. R6-FIX has not published an operation-ID/ambiguous-outcome API. SDK tests establish client behavior against synthetic HTTP fixtures only.

The accepted source below includes merged producer PRs #349 (OCI peer discovery), #353 (`ts-replay`) and #359 (listener hosting cleanup). Synthetic fixtures qualify SDK handling of those source contracts; live OCI interoperability remains unproven.

The source pin is `TerraScale/terrascale@8db4c792142dcd6644e1d14a2b2efd6bc04a107a`. `provenance.json` names every exact source export and its SHA-256; `producer/` contains those bytes. `artifacts/terrabase-8db4c792-source.tar.gz` is a commit-stamped `git archive` of those producer files, obtained read-only from the pinned producer Git object, compressed with gzip mtime=0. Tests compare exports with the artifact's bytes and commit stamp, not just SDK-maintained hashes. This is source-artifact parity, not running-producer DTO/security/OpenAPI qualification; M2 against the final producer and SDK remains required. `fixtures/` contains synthetic JSON/SSE derived from those definitions, not captured service responses. `routes.json` records every client method/path, including existing uniqueness and the distinct declared JSON Schema endpoints. No TerraBase binary/project is used by SDK tests.

The historical R8 interoperability snapshot is retained separately at PR #165 head `2b95f5ef0641d3a8b8695ec1989e02943befbef2` in `pending-r8.json`, with three exact exports in `producer-pending/r8/` and its own commit-stamped producer archive. `transaction-changes.sse` follows that source's initial cursor and two changes sharing an origin/version, with an id only on the last change. Its manifest records qualification at capture time; PR #165 has since merged. The historical snapshot does not replace the accepted source pin.

## Consumer signatures

Create one `TerraBaseDocumentsClient` with a caller-owned redirect-disabled `HttpClient` and an `AccessTokenProvider` acquiring a fresh action-scoped bearer token. Each request acquires its credential once; caller cancellation applies to acquisition. Region chooses `https://{region}.api.terrascale.tech` and sends `X-TerraScale-Region-Pin`. An explicit origin/resolver can be retained for authorized private services or HTTP fixtures. The SDK never disposes the caller's client.

`client.Project(projectId)` returns `TerraBaseProjectClient`:

```csharp
Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string name,
    string? homeRegion = null, IReadOnlyList<string>? replicaRegions = null, bool? allRegions = null,
    CancellationToken cancellationToken = default);
Task<TerraBasePage<TerraBaseManagedDatabase>> ListDatabasesAsync(
    string? after = null, int? limit = null, CancellationToken cancellationToken = default);
Task<TerraBaseManagedDatabase> GetDatabaseAsync(string databaseName,
    CancellationToken cancellationToken = default);
Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default);
Task<TerraBaseManagedDatabase> SetDatabaseRegionsAsync(string databaseName,
    IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken = default);
Task<TerraBaseManagedDatabase> SetDatabaseAllRegionsAsync(string databaseName,
    CancellationToken cancellationToken = default);
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
IAsyncEnumerable<TerraBaseExportLine> ExportAsync(TerraBaseExportCursor? after = null,
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

Project lifecycle/usage, branch, SQL and index methods use `/v1/projects/{project}`. `{project}` is the canonical ts-auth project ULID. `{database}` is its **name**, not the database ULID. The source DTO `ManagedDatabaseBody` uses camelCase and includes `id`, `projectId`, `name`, nullable `branchedFrom`, RFC 3339 `createdAt`, answering `region`, `homeRegion`, `replicaRegions` and boolean `allRegions` (true: every serving region holds a copy and `replicaRegions` is empty; sending both on create is `placement_conflict`). `allRegions` is included in the merged producer source pin. The SDK keeps its existing five-argument record constructor and adds required JSON placement members; old incomplete server payloads are now rejected. List pagination is `items`/nullable `next`, query `after` and `limit` 1–100 (default **20**, from ManagementRoute).

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

No cursor replays retained history; `cursor=now` starts at the current head. `Last-Event-ID` takes precedence, so the SDK omits the query cursor when the header is supplied. Retain the **same collections filter** on reconnect, including in another region. The cursor is a vector; the SDK never decodes, compares, combines or rewrites it. Only opacity is relied on; the SDK never interprets the producer cursor format. Metadata/collection drops are outside this feed; it is not a live SQL/query subscription.

400 `invalid_cursor` is `TerraBaseValidationException` for multiple `Last-Event-ID` values; the SDK never resets it automatically. The pinned producer returns 410 `cursor_expired` for undecodable or legacy cursors, a cursor from another database or collection filter, expired retention or issue time, and a scanned prefix that cannot be verified on the serving machine. These become `TerraBaseCursorExpiredException`: explicitly restart with `cursor=now` and the same filter, **re-read** the data, and reconcile buffered/duplicate changes before replacing the old checkpoint. Recovery is application-owned; the feed alone provides no atomic snapshot handoff or live-query guarantee. The same expired type is raised for a terminal SSE error after a 200. 429 is `TerraBaseRateLimitedException`; `RetryAfter` preserves an advisory server delay, without background reconnect/retry. Stream error codes `not_found`/`unknown_database`, `tenant_memory` and `storage_busy` map to not-found, rate-limited and unavailable exceptions. A clean end at token expiry or the 30-minute server lifetime returns normally; the consumer reconnects with its last accepted checkpoint and a fresh credential.

`TerraBaseWrongRegionException` preserves 421 and exposes validated answering `Region`, `HomeRegion`, `Regions`, and `ErrorCode`. An empty 421 still fails; even with a `Location` or `Retry-After` hint, the SDK does not redirect, retry or change hosts. Lifecycle DTOs and stream items expose the answering region. Invalid region header values are ignored. A server-reported placement is not global membership/discovery.

**Producer routing limitation (pinned source, not observed on OCI):** a pinned regional `CreateDatabaseAsync(homeRegion: otherRegion)` can return 503 `region_unavailable`, rather than 421, after routing to the home region while retaining the pinned host (`DatabaseRegions.cs` and `Listeners.cs`). This remains `TerraBaseUnavailableException`, sent once; no SDK reroute/retry is added. The source pin establishes the routing definitions, not live OCI qualification.

Streams use the in-box SSE parser behind a 4 MiB event bound, including unterminated lines and multiline data. There is no background queue. Response acquisition uses RequestTimeout; an active stream outlives it. Each blocked read has a fixed 45-second timeout, allowing the producer's 15-second heartbeat. Consumer cancellation/disposal closes the response and request; backpressure retains only parser buffers/current event. Caller-owned HttpClient timeouts remain the caller's responsibility.

## Database export

`ExportAsync` reads `GET /v1/projects/{project}/databases/{database}/export` (documents:read) from the unmerged producer PR [#412](https://github.com/TerraScale/terrascale/pull/412); it is in `routes.json` but not in the pinned source or `current-public-routes.json` yet. The body is `application/x-ndjson`, one `{collection,id,document}` line per live document, in collection then id order, from one read snapshot. `after` sends `after_collection` and `after_id` together; pass the last received line's `Cursor` to resume, which reads a new snapshot. Lines are parsed incrementally and each is bounded by MaxResponseBytes. A connection lost mid-stream throws; a final line without its newline is `invalid_response`, so a delivered line is always whole. Errors use the usual mapping: 400 validation, 403 forbidden, 404 `unknown_database`, 503 unavailable. RequestTimeout covers acquisition only; the producer sends no heartbeat, so the SDK has no idle timeout and caller cancellation closes the stream. Sent once, never retried.

## R6-FIX surface design — pending producer, not implemented

The merged producer rejects `Idempotency-Key`. There is no operation-ID header/body field, ambiguous status/DTO, retention policy or outcome route to implement. Until its API is published and merged, writes use their current contract; `TimeoutException`/transport loss leaves the outcome unknown, with no automatic retry or speculative reconciliation call.

The SDK adaptation will keep operation identity explicit at call sites: a write-options overload carrying the producer's operation ID, a typed `TerraBaseAmbiguousWriteException` carrying the same ID (an explicit non-success), and a database-scoped outcome lookup returning the producer's terminal/pending/unknown states. These are **surface proposals**, not callable APIs or finalized names/signatures. Existing preconditions must remain available. Caller retains the original operation identity and content; an explicit retry reuses them. Never generate a new ID while retrying, translate ambiguous/pending/unknown into success or abort, or replay across regions without the producer's documented authority/retention semantics. Final parameter placement, ID grammar, result model, wire mapping and reconcile method wait for the exact R6-FIX contract and matching tests.

## I5 handoff

Root version stays `0.0.2` here; I5 selects the next `0.0.X` candidate after accepted I1/I2 work. Copy `contracts/terrabase/fixtures/**`, `provenance.json`/`producer/**`, `pending-r8.json`/`producer-pending/**` and `artifacts/**` into test output with their relative paths, and include the reviewed contract inputs in the package. The current tests locate fixtures in their source worktree; route-manifest copying already exists. No new package is needed for the in-box SSE/TAR/gzip APIs on net10.0/net11.0. `TerraBaseChangesEvent.Cursor` is now `string?`: I4 must preserve its existing checkpoint on null and process the change, and retain consumer deduplication across reconnects. Update root README/release notes and the J8 compatibility-matrix row to distinguish both schema APIs plus counts, the source pin, usage qualification and unresolved producer gates. Root locks/CI/toolchain provenance stay I5-owned.

## Function catalog extension

`functions.json` pins the four catalog operations to producer `1d823d8534fc5035859a47e263691d0c7cb7b744`, including exact source hashes, scope and replacement semantics. Both SDK route manifests include these scoped additions; their older root producer pins still describe the historical base inventory. `function-invocations.json` extends this with invoke/log/delivery operations pinned to `2cc348ec75a11079aa20547c09203e581ce70cb8`, preserving arbitrary response bytes/status, bounded observations and no mutation replay. HTTP route/DTO fixtures establish no production qualification or complete current-contract parity.

Export continuation keys follow [the scoped export cursor contract](export-cursors.json):
use the last complete line's collection and stored id unchanged. SQL primary keys are
valid export cursors (1 to 256 characters without control characters), including keys
that require URL encoding. Document point operations still require canonical ULIDs;
resuming an export does not authorize using SQL keys with those operations.
