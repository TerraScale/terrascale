# TerraScale.DotNet

The official .NET SDK for TerraBase (its v1 HTTP API) and ts-auth. The package supports .NET 10 LTS and .NET 11; a .NET 11 toolchain builds
both targets.

Version `0.0.3` combines accepted ts-auth platform/headless contracts (#13) and
TerraBase lifecycle/usage/changes contracts (#14). The package includes their
producer artifacts; pending producer inputs remain qualified separately. Import
APIs and the later stream-timer follow-up (#15) are excluded from this release.

## Entity IDs (TS-754)

TerraScale's target entity ID contract is a plain 26-character uppercase
Crockford ULID, with no prefixes or UUIDs. Generate and validate these IDs with
the SDK's shared helpers:

```csharp
using TerraScale.DotNet;

var ids = new TerraScaleIdGenerator(); // reuse per caller-owned service
string id = ids.NewId();
bool valid = TerraScaleIds.IsValid(id);
string validated = TerraScaleIds.Validate(id); // ArgumentException for invalid input
```

The generator uses NUlid 1.7.3 with cryptographic randomness and instance-owned
monotonic state. Calls on one instance are thread-safe and strictly increasing,
including within the same millisecond and during clock rollback. Independent
instances do not share ordering. IDs are not authentication secrets. Validation
rejects lowercase, whitespace, ambiguous letters, prefixes, UUIDs and 128-bit
overflow; it does not normalize input. IDs remain strings in SDK records.

ULID timestamps are logical ordering values, not trusted creation times. A
forward clock glitch followed by correction leaves that generator's IDs at the
future timestamp until UTC catches up, even for a multi-year jump. Correct the
host clock and replace the generator to resume current timestamps; ordering
across old and new instances is not guaranteed. Forward jumps are accepted
because a generator can legitimately be idle for any length of time.

The TerraBase document key is the entity ID. Entity payloads must not repeat
that key in a separate `id` field. Keep IDs in document metadata and references
to other entities in their reference fields.

**Document ids are ULIDs.** TerraBase accepts only canonical ULIDs as document
ids and answers `400 invalid_document_id` otherwise. The SDK rejects any other id
with `ArgumentException` before sending: `GetDocumentAsync`, `PutDocumentAsync`,
`PatchDocumentAsync`, `DeleteDocumentAsync`, the platform `GetDocumentAsync` and
every `TerraBaseTransactionOperation` factory that takes an id (`Create`'s id is
optional; TerraBase generates one when it is omitted). Natural keys such as
emails become name-based ULIDs: encode the first 16 bytes of a SHA-256 over an
unambiguous input that includes the collection. SQL queries are not restricted.
There is no migration, backfill, compatibility shim or dual-read/write path.
This SDK owns no persisted service data and performs no production reset.

## Zero-configuration operating contract

First-party services must discover eligible serving regions and receive
production credentials automatically, with fixed settings and no region
activation or manual endpoint/secret wiring. Customer tenant/project scope,
authorized consumer tokens and exact registered callbacks remain required.
This docs-only TS-795 change does not implement automatic discovery or secret
delivery. Current TerraBase clients still require an endpoint/resolver pending
TS-507; legacy direct-origin, proxy and tuning selectors remain inventoried for
owning implementation changes. Examples below show current API boundaries,
not a first-party production setup recipe. `discoveredOrigin` below is a trusted
HTTPS `Uri` supplied by an authorized integration, not created by this SDK. See [AGENTS.md](AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert).

## TerraBase client

Schema10 preparation adds `TerraBaseDatabase.DeclareSchemaAsync` and `GetSchemaAsync` with native single
and compound unique declarations. See the [exact source contract and qualification gates](docs/terrabase-schema10.md).
These operations are not qualified by the existing pinned server release; Auth bootstrap credential
support must land in the producer before Auth uses them at startup.

`TerraScale.DotNet.TerraBase` talks to the C# TerraBase server over its public
HTTPS client listener. `TerraBaseDocumentsClient` covers the Documents API v1:
databases, collections, documents, queries and transactions.
`TerraBaseManagementClient` covers the Management API: organizations, the
current organization, plans, projects, databases, branches, members,
invitations, ownership transfer, audit retention and audit events.
`TerraBaseShapeClient` follows a collection's shape protocol v1 into local state;
see [shape reads and qualification](docs/terrabase-shapes.md). Bulk data endpoints
are not covered yet.

```csharp
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

using var httpClient = new HttpClient(TerraBaseHttp.CreateHandler());
var documents = new TerraBaseDocumentsClient(httpClient, new TerraBaseDocumentsClientOptions
{
    BaseAddress = discoveredOrigin,
    ApiKey = currentTerraBaseApiKey // tb_...
});

TerraBaseDatabase database = documents.Database(projectId, "app");
await database.CreateAsync(cancellationToken); // idempotent PUT
await database.CreateCollectionAsync("users", cancellationToken);

var user = JsonSerializer.SerializeToElement(new { email = "user@example.com" });
TerraBaseWriteResult created = await database.PutDocumentAsync(
    "users", userId, user, TerraBasePrecondition.Absent, cancellationToken);

TerraBaseDocument? current = await database.GetDocumentAsync("users", userId, cancellationToken);
```

A writable database requires at least two regions. The home region is where a database is created and managed;
it does not limit placement to that region. `Project(projectId).CreateDatabaseAsync` without `replicaRegions`
uses every other current serving region. An explicit replica list must contain at least one region other
than the home; an empty list on create returns 400 `placement_needs_two_regions`. `allRegions: true` instead
follows every serving region, including regions that join later, and cannot be combined with a replica list.
`SetDatabaseRegionsAsync` replaces the complete replica list and turns all-regions placement off.

Call `DeleteAsync` directly in the database's home region, without a preceding placement update.
Permanent purge authority removes every replica, including all-regions placement; branches remain independent.
A 204 response records logical deletion and local cleanup. Remote erasure is retried durably, so 204 does
not guarantee synchronous remote erasure. An empty replica-list update can temporarily make document writes
return 409 `placement_needs_two_regions`; it is not required for deletion and does not provide supported single-region placement.

Relational SQL runs on the same database handle and project access token
([contract](contracts/terrabase/sql-execute.md)). Each call is one transaction, all or nothing; reads need
`documents:read`, writes and DDL `documents:write`. Arguments are positional (`?`) or named (keys without `:`, `@`
or `$`); values are null, integers, finite doubles, booleans (stored as 1/0) or strings. Integers come back as JSON
numbers: exact here, but JavaScript clients lose precision beyond 2^53.

```csharp
await database.ExecuteSqlBatchAsync([
    new("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)"),
    new("INSERT INTO people (name) VALUES (:name)", Named: new Dictionary<string, TerraBaseSqlValue> { ["name"] = "Ada" }),
], cancellationToken);

TerraBaseSqlStatementResult inserted = await database.ExecuteSqlAsync(
    "INSERT INTO people (name) VALUES (?)", ["Grace"], cancellationToken); // RowsAffected, LastInsertRowid

foreach (TerraBaseSqlRecord row in await database.QuerySqlAsync("SELECT id, name FROM people WHERE id > ?", [0], cancellationToken))
    names[row.GetInt64("id")] = row.GetString("name");
```

A refused statement rolls the request back and throws `TerraBaseSqlException` with `Code` (`sql_error`,
`forbidden_scope`, `sql_conflict`, `sql_too_large`), `Statement` (zero-based index) and `SqliteCode`; other failures
keep the usual `TerraBaseException` types. Nothing is retried. `RunSqlAsync` is the legacy read-only document SQL
subset (`databases:read`, refused for dashboard keys); prefer the methods above.

One-time imports follow TerraBase's import contract (monorepo `docs/terrabase/import.md` and `import-ndjson.md` at
`94481f1f2`; nothing here is live-qualified). Every imported row gets a generated deterministic ULID document id;
source primary keys (and NDJSON `id`, `_id` or `$oid`) stay ordinary fields. The user exports with their own tools
(`pg_dump --format=plain`, a rollback-journal SQLite/Turso file from `VACUUM INTO`, or NDJSON) and uploads it: TerraBase never
connects to the source or receives its credentials. Create a new, empty destination database for each import with
`Project(projectId).CreateDatabaseAsync`, a fresh `import-<lowercase ULID>` name and the same ts-auth
project access token (a database created through `TerraBaseManagementClient` is reachable by a project token only once
TerraBase has verified its project binding). The pinned import contract requires a destination without replicas;
that requirement conflicts with minimum-two-region placement and remains a producer compatibility gap. The example
below describes that pinned contract, not a supported single-region creation workflow. Every import call must still
use a client pinned to the destination's home region:

```csharp
TerraBaseManagedDatabase destination = await documents.Project(projectId).CreateDatabaseAsync(destinationName, cancellationToken: cancellationToken);
TerraBaseDatabase import = regional.Database(projectId, destinationName); // a client with Region = destination.HomeRegion
TerraBaseImportJob job = await import.CreateImportAsync(TerraBaseImportFormat.PostgresPlain, fileBytes, fileSha256Hex, cancellationToken: cancellationToken);
var part = new byte[TerraBaseDatabase.MaxImportSourcePartBytes];
while (job.Source.ReceivedBytes < job.Source.Bytes)
{
    var size = (int)Math.Min(part.Length, job.Source.Bytes - job.Source.ReceivedBytes);
    file.Position = job.Source.ReceivedBytes;
    await file.ReadExactlyAsync(part.AsMemory(0, size), cancellationToken);
    job = await import.UploadImportSourceAsync(job.Source.ReceivedBytes, part.AsMemory(0, size), cancellationToken);
}

// Poll GetImportAsync until Ready (show job.Preview) or Rejected; start only the preview the user confirmed.
job = await import.StartImportAsync(confirmedPreviewId, cancellationToken);
```

Poll `GetImportAsync` for progress, validation and errors; `UpdateImportAsync`, `CancelImportAsync` and
`ResumeImportAsync` follow the contract's state machine. Every request is sent once and TerraBase takes no
`Idempotency-Key`: replaying an identical create or an already received part returns the job unchanged, and
`offset_mismatch` means "read the job and continue from `Source.ReceivedBytes`" (an upload idle for a day restarts at
0). TerraBase keeps a staged source at most 7 days after the job failed, was cancelled or its preview was left alone,
and meters upload bytes and imported documents; tell users before a real upload. Deleting the destination database
discards the import and its staged source.

`GetDocumentAsync` returns `null` for an absent document. `CreateDocumentAsync`
posts a document and TerraBase generates a ULID. Lists and queries return
pages with `NextCursor`. Patches take an RFC 7396 merge patch `JsonElement` or a
list of `TerraBasePatchOperation`. `TransactAsync` applies `Create`, `Put`,
`Patch`, `Delete`, `Assert`, `AssertAbsent` and `AssertCount` operations
atomically.

With a local package built from this source checkout, stream query results as follows:

```csharp
await foreach (TerraBaseDocument document in database.QueryDocumentsAsync(
    "users", new TerraBaseQuery { Limit = 100 }, cancellationToken))
{
    // Process document.Document in your application.
}
```

`QueryPagesAsync` instead yields complete pages with count, aggregate and explain metadata.
Both preserve the query's initial opaque cursor, follow empty pages with a continuation,
and fetch only as enumeration advances. Later requests clear `Count`; the original query
is unchanged. Cancellation stops buffered document delivery; breaking the loop stops
pagination. Repeated or cyclic continuations throw `InvalidDataException`.

`TerraBaseSort.Near(path, vector, metric)` orders a query by vector distance
(`cosine`, the default, or `l2`). It must be the only sort key; the query
returns one page of at most 1000 documents, each with `Distance`.
`SetCollectionTtlAsync`, `GetCollectionTtlAsync` and `ClearCollectionTtlAsync`
manage a collection's expiry member: documents whose member holds a past
RFC 3339 UTC time are deleted within about an hour and stay readable until then.

```csharp
await database.SetCollectionTtlAsync("sessions", "/expiresAt", cancellationToken);
TerraBaseDocumentPage nearest = await database.QueryAsync("items",
    new TerraBaseQuery { Sort = [TerraBaseSort.Near("/embedding", embedding)], Limit = 10 }, cancellationToken);
```

Vector indexes take one dotted field path and dimensions from 1 to 4096:

```csharp
TerraBaseIndex vectorIndex = await database.CreateIndexAsync(
    new TerraBaseCreateIndexRequest("items", ["embedding"], Kind: "vector", Dimensions: 768),
    cancellationToken);
```

Creation may return `vectorIndex.State == "building"` while existing documents are indexed.
`ListIndexesAsync` reports when it becomes `active`; near queries scan until then.
A matching active vector index makes near results approximate.

Points are `{"lat": …, "lon": …}` objects (WGS 84 degrees). `TerraBaseFilter.WithinRadius(path, lat, lon, meters)`
and `TerraBaseFilter.WithinBox(path, south, west, north, east)` build `within` filter nodes (a box with `west > east`
crosses the antimeridian); they nest under `and`, `or` and `not`. `TerraBaseSort.NearPoint(path, lat, lon)` orders
by haversine meters, nearest first, with the same one-key, one-page (at most 1000) rules as the vector `Near`;
`Distance` then holds meters. `TerraBaseQuery.Aggregate` groups matches instead of returning documents: up to 4
`GroupBy` paths and up to 16 `Sum`, `Avg`, `Min` and `Max` paths, without sort, project, cursor or count. The page's
`Groups` hold each `Key`, `Count` and per-path summaries (numbers only; `null` without numbers, sums `0`), and
`Limit` bounds the groups.

```csharp
TerraBaseDocumentPage nearby = await database.QueryAsync("places", new TerraBaseQuery
{
    Filter = TerraBaseFilter.WithinRadius("/loc", -23.55, -46.63, 1000),
    Sort = [TerraBaseSort.NearPoint("/loc", -23.55, -46.63)],
}, cancellationToken);
TerraBaseDocumentPage totals = await database.QueryAsync("orders", new TerraBaseQuery
{
    Aggregate = new() { GroupBy = ["/status"], Sum = ["/amount"], Avg = ["/amount"] },
}, cancellationToken);
```

`TerraBaseQuery.Explain = true` runs the query as usual (bypassing the result cache) and fills the page's `Explain`:
the engine `Plan` steps, the `Indexes` used, `FullScan`, `RowsScanned`, `WorkUnits`, `ElapsedMs` and, on a full scan,
`SuggestedIndex` (the `fields` of an index to create). `ListQueryShapesAsync` items carry `LatencyBuckets` (20
power-of-two millisecond buckets) and `P95LatencyMs`; both are null for statistics recorded before buckets existed.
The list is cumulative per shape, so filter on `LastSeenAt` for a recent window.

```csharp
TerraBaseDocumentPage explained = await database.QueryAsync("items", new TerraBaseQuery
{
    Filter = JsonDocument.Parse("""{"eq":["/status","open"]}""").RootElement,
    Explain = true,
}, cancellationToken);
IReadOnlyList<string>? fields = explained.Explain!.SuggestedIndex; // ["status"] on a full scan
```

```csharp
// Use the ts-orch NetBird service address; the caller configures the required peer credential on httpClient.
// Qualify this pinned-contract example against the OCI server before use.
var management = new TerraBaseManagementClient(httpClient, new TerraBaseManagementClientOptions
{
    BaseAddress = new Uri("http://100.64.0.1:8081"),
});

TerraBaseOrganization organization = await management.CreateOrganizationAsync(
    authenticatedUserId, "Acme", cancellationToken);
```

Every management call takes the acting `userId` first and sends it as `X-TerraBase-User-Id`; organization routes also
send `X-TerraBase-Organization-Id`. Take the user id from an authenticated session, never from browser input. On the
public HTTPS listener, pass the user's ts-auth access token through `AccessTokenProvider` instead. Production runs on
OCI under ts-orch. Requests without a credential (private listener only) go out as HTTP/2 with prior knowledge
(TerraBase's internal listener is h2c only). Pages are
`TerraBasePage<T>(Items, Next)`; pass `Next` as `after` with an optional `limit`.

Both clients accept `AccessTokenProvider`
(`Func<CancellationToken, ValueTask<string>>`); the Documents client also takes a static `ApiKey`. Supply at most one
credential source, and none only for a private plain-HTTP origin. The provider runs
once per request after origin validation, shares the request deadline, and must
be thread-safe and honor cancellation. It supplies the credential accepted by
the deployed server; the SDK performs no exchange, background refresh, or retry.
Current routes and credentials remain supported. The TS-589 project JWT and
route migration will follow its server release.

Concurrency uses opaque document versions (`tbv1-…` or `tbv2-…` ETags).
`TerraBasePrecondition.Matches(version)` sends `If-Match` with that version,
`TerraBasePrecondition.Exists` sends `If-Match: *`, and
`TerraBasePrecondition.Absent` sends `If-None-Match: *` for create-only writes;
create-only succeeds over a deleted tombstone. A failed precondition throws
`TerraBasePreconditionFailedException`. Transaction operations take the version
as their `ifMatch` argument.

Error statuses throw `TerraBaseException`, an `HttpRequestException` with
`ErrorCode` and, for transactions, `OperationIndex`. Subclasses name the
statuses callers act on: `TerraBaseValidationException` (400, 413, 415),
`TerraBaseAuthenticationException` (401), `TerraBaseForbiddenException` (403),
`TerraBaseNotFoundException` (404), `TerraBaseConflictException` (409),
`TerraBasePreconditionFailedException` (412) and `TerraBaseUnavailableException`
(503). A request that passes its `RequestTimeout` throws `TimeoutException`.
An earlier `HttpClient.Timeout` also throws `TimeoutException`; caller cancellation
remains `OperationCanceledException`. There are no automatic retries: TerraBase documents no retryable failure and
rejects `Idempotency-Key`, so each request is sent once. Retry a write only
when its precondition makes the retry safe.

Supplied `HttpClient` handlers **must disable automatic redirects**. Use
`TerraBaseHttp.CreateHandler()` or set `AllowAutoRedirect = false` on your handler
before constructing the client. Redirects can forward invitation tokens, document
bodies and acting-user headers to another origin. The SDK cannot inspect or
change a caller-owned handler's settings. Cookie handling should also be disabled.

Every call takes a `CancellationToken`. The clients are thread-safe and never
dispose the caller-owned `HttpClient`. The TerraBase slice uses
System.Text.Json source generation and is trim- and native-AOT-safe; the build
fails on any trimming or AOT warning in it.

Both clients resolve their endpoint per request through `ITerraBaseEndpointResolver`.
`BaseAddress` creates a `TerraBaseStaticEndpointResolver`; alternatively, set
`EndpointResolver` alone to supply a resolver. Resolution shares the request deadline
and cancellation token, and every returned origin is validated before credentials
are sent.

Regions follow TerraBase's regional contract (monorepo `docs/terrabase/region-identity.md`). Without `Region`, a request
goes to `BaseAddress` (for example `https://api.terrascale.tech`) and
carries no routing header. With `Region = "gru"` (a lowercase region id), every request is sent to
`https://api.terrascale.tech` (unless `BaseAddress`/`EndpointResolver` is set) and pinned with
`X-TerraScale-Region-Pin: gru`, so no other region serves it. A region that does not serve the request answers `421`,
surfaced as `TerraBaseWrongRegionException`: `Region` is the region that answered, `HomeRegion` and `Regions` (from
`X-TerraBase-Database-Home`/`-Regions`) say where to send it. Nothing is retried or redirected automatically: a write is
sent once, and conditional writes (`TerraBasePrecondition`) are the duplicate guard. Every `TerraBaseException` carries
the answering region (`X-TerraBase-Region`) when TerraBase named it.

No TerraBase runs in this repository (user rule, 2026-10-11: no repository other
than the monorepo depends on TerraBase). The clients are tested over HTTP fakes.
`contracts/terrabase/routes.json` records the existing TerraBase routes the clients call;
`TerraBaseContractTests` checks the clients send exactly those, and the monorepo's
ts-auth Fast suite checks that the TerraBase built from the same commit serves each
one, so the SDK and TerraBase cannot drift apart silently. Shape protocol routes
are separately pinned in `contracts/terrabase/shape-routes.json` while FR-20
server wiring is pending. ts-auth's suites exercise
these clients end to end against that same TerraBase.

```bash
nice -n 19 dotnet run --project tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj --framework net10.0 --configuration Release
```

`TerraBasePlatformClient` reads TerraBase's explore API over TerraScale's own databases (`platform-auth`,
`platform-dashboard`) for the dashboard's platform-admin pages: pass the signed-in admin's ts-auth access token through
`AccessTokenProvider` (it must carry `platform_role: admin`). It is read-only, and TerraBase redacts secret-looking fields.

See [TerraBase consumers](docs/terrabase-consumers.md).

## ts-auth client

`TerraScaleAuthClient` implements the existing instance-scoped ts-auth
management and OIDC APIs: users, sessions, API keys, organizations, SSO,
directories, JWT templates, webhooks, audit, billing, instance settings and
OAuth clients. `TerraScaleAuthWebhookVerifier` verifies webhook HMAC-SHA256
signatures. Management calls use the instance key and `/v1/{slug}/...` routes;
OIDC calls use the published `/{slug}/oauth/...` surface.

```csharp
using TerraScale.DotNet;

var auth = new TerraScaleAuthClient(
    new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }),
    new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.terrascale.tech"),
        ApiKey = currentAuthManagementKey,
        InstanceSlug = "acme"
    });

var user = await auth.Users.CreateAsync(email: "user@example.com");
```

The auth client calls its `BaseAddress` directly; there is no regional discovery or proxy switch. This does not
route database requests. `AddTerraScaleAuthClient` supplies dependency-injection registration
with redirects disabled. Only safe reads can retry; auth mutations are sent
once. `TerraScaleAuthException` exposes typed status/retry metadata and only
known fixed error codes. Messages are fixed safe diagnostics; arbitrary
producer messages, codes and response bodies are never retained. Options
formatting omits credentials.

Project and API-key IP allowlists use typed CIDR records and setters; see [CIDR contracts and examples](contracts/auth/cidrs.md).

## Verification and package distribution

```bash
dotnet build TerraScale.DotNet.slnx --configuration Release
dotnet format style TerraScale.DotNet.slnx --verify-no-changes --no-restore
dotnet run --project tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj \
  --framework net10.0 --configuration Release --no-build --no-restore -- \
  --minimum-expected-tests 231
```

Run the suite for `net11.0` with a .NET 11 toolchain too. Tests use TUnit over HTTP fakes and cover the TerraBase
clients (route manifest, regional contract, responses) and the retained ts-auth contract.

[Package release](docs/package-release.md) describes how a tagged version is
packed once, pushed to GitHub Packages and restored into a clean installed-package
consumer, and how the same bytes reach nuget.org. Live-service qualification is
a separate gate; neither a source build nor a publication implies it.
