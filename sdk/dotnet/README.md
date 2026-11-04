# TerraScale.DotNet

The official .NET SDK for TerraBase (its v1 HTTP API) and ts-auth. The package supports .NET 10 LTS and .NET 11; a .NET 11 toolchain builds
both targets.

The current source package version is `0.0.12`. NuGet publication remains on hold:
use a `ProjectReference` to an immutable checkout or `dotnet pack` into a local package source.
Keep the source revision with the package; isolate its restore cache so a previous local
pack with the same version cannot be reused. See the [pinned C# quickstart](https://docs.terrascale.tech/getting-started/csharp).

| Artifact | Availability |
| --- | --- |
| Current source/local pack | SQL over REST and WebSocket, database lifecycle, backups, functions, management and the supported auth APIs below |

These are source/API availability claims. The included producer contracts and HTTP fixtures
carry their own pins; deployment, scope, plan and live-method qualification remain separate gates.

## SQL

`TerraBaseSqlClient` is the SQL surface: SQL over REST (`POST …/sql/execute`,
[contract](contracts/terrabase/sql-execute.md)) and SQL over WebSocket (`GET …/sql/ws`, subprotocol
`terrascale.sql.v1`, [contract](contracts/terrabase/sql-websocket.md)). Reads need `documents:read`; writes and
schema changes need `documents:write`. The other SDKs copy this shape ([API shape](../SQL-API.md)).

```csharp
using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "gru", ApiKey = apiKey });
TerraBaseSqlDatabase db = sql.Database(projectId, "app");

await db.BatchAsync([
    new("CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT NOT NULL, avatar BLOB)"),
    new("INSERT INTO people (id, name) VALUES (:id, :name)", Named: new Dictionary<string, TerraBaseSqlValue> { ["id"] = ids.NewId(), ["name"] = "Ada" }),
], ct);

IReadOnlyList<TerraBaseSqlRecord> rows = await db.QueryAsync("SELECT id, name FROM people WHERE name = ?", ["Ada"], ct);
IReadOnlyList<Person> people = await db.QueryAsync("SELECT id, name FROM people", null, AppJson.Default.Person, ct);
```

- `ApiKey` is a project or organization API key from the dashboard. The client exchanges it at ts-auth
  (`client_credentials`) for an access token to the project named in `Database(projectId, …)`, caches the token and
  renews it before it expires; concurrent calls share one exchange and the key goes only to ts-auth. Pass
  `AccessTokenProvider` instead to supply your own bearer token.
- `ExecuteAsync`, `QueryAsync` and `BatchAsync` send one request that runs as one transaction, all or nothing.
  `BatchAsync` takes 1 to 256 statements in a body of at most 1 MiB and returns one result per statement.
- Arguments are positional (`?`, `?NNN`) or named (keys without `:`, `@` or `$`). Values are null, 64-bit integers,
  finite doubles, booleans (stored as 1 or 0), strings and blobs (`TerraBaseSqlValue.FromBytes`, sent as
  `{"base64":"…"}`).
- `TerraBaseSqlRecord` reads values by ordinal or column name (`GetInt64`, `GetDouble`, `GetBoolean`, `GetString`,
  `GetBytes`, `IsNull`). `As<T>(JsonTypeInfo<T>)` and the typed `QueryAsync<T>` map a row through source-generated
  JSON metadata: the row becomes an object keyed by column name and a blob a base64 string.
- A refused statement throws `TerraBaseSqlException` with `Code` (one of `TerraBaseSqlErrorCodes`, such as
  `unique_violation`), `Sqlstate` (such as `23505`) and `Statement` (zero-based index). The request rolled back.
  A REST request refused with `serialization_conflict` is resent at most twice; nothing else is resent.
- Primary keys are ULIDs from `TerraScaleIdGenerator`; tables that take local writes avoid `AUTOINCREMENT`.

[`samples/sql-quickstart.cs`](samples/sql-quickstart.cs) runs this flow against a live region with a ts CLI sign-in:
`dotnet run samples/sql-quickstart.cs -- <refresh-token-file> <organization> <project> <database> <region>`.

Interactive transactions use the WebSocket session. The caller owns it; dispose closes it and rolls back an open
transaction. A transaction lasts at most 5 seconds. Calls on one session may overlap; answers are matched by id.

```csharp
await using TerraBaseSqlSession session = await db.ConnectAsync(ct);
long balance = await session.TransactionAsync(async (tx, token) =>
{
    await tx.ExecuteAsync("UPDATE accounts SET balance = balance - ? WHERE id = ?", [amount, from], token);
    await tx.ExecuteAsync("UPDATE accounts SET balance = balance + ? WHERE id = ?", [amount, to], token);
    return (await tx.QueryAsync("SELECT balance FROM accounts WHERE id = ?", [from], token))[0].GetInt64(0);
}, ct);
```

`TransactionAsync` begins, runs the callback and commits; when the callback throws it rolls back and rethrows.
`BeginAsync`, `CommitAsync` and `RollbackAsync` are the explicit form. After a failed statement inside a transaction
every call throws `transaction_aborted` until rollback; commit then rolls back and throws `transaction_rolled_back`.
Session errors have a null `StatusCode`. A refused upgrade throws the usual `TerraBaseException` subclass (401, 404,
421 `TerraBaseWrongRegionException`). The session connects with its own `ClientWebSocket`, not the caller's
`HttpClient`, and is never resent. When the socket drops, calls in flight fail and the next call reconnects; a
transaction open at the drop rolled back, so every call but `RollbackAsync` throws `transaction_rolled_back` until the
caller rolls back.

## Databases for agents

`TerraBaseSqlClient` creates, forks and deletes databases, sets or clears their TTL, and mints database-scoped
API keys. Database mutations need `databases:write` and use the same `ApiKey` exchange or `AccessTokenProvider`
as SQL calls. Database names or ULIDs are accepted except when minting a key, which requires the database ULID.

```csharp
TerraBaseManagedDatabase agent = await sql.CreateDatabaseAsync(projectId, "agent", ttlSeconds: 3600, cancellationToken: ct);
TerraBaseManagedDatabase fork = await sql.ForkDatabaseAsync(projectId, agent.Id, "agent-copy", ttlSeconds: 3600, cancellationToken: ct);
TerraBaseScopedKey key = await sql.CreateScopedKeyAsync(agent.Id, "read_write", projectId, ttlSeconds: 3600, cancellationToken: ct);
var agentClient = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "gru", ApiKey = key.Key });
await sql.SetDatabaseTtlAsync(projectId, agent.Id, 7200, ct);
await sql.ClearDatabaseTtlAsync(projectId, agent.Id, ct);
await sql.DeleteDatabaseAsync(projectId, agent.Id, ct);
```

Database TTL is 60 to 31,536,000 seconds. `ExpiresAt` is null without a TTL; expiry deletes the database, backups
and data keys. TTL databases count against plan limits and billing until deleted. Forks never inherit the source's
TTL. Pass `pointInTime` to `ForkDatabaseAsync` to restore an instant inside the source's restore window.

`CreateScopedKeyAsync` requires the client's `ApiKey` and sends Basic `keyId:key` only to ts-auth. Access is `read`
or `read_write`; TTL is 60 to 2,592,000 seconds, default 3600, bounded by the parent's expiry. Pass the project for
organization keys; it must match project keys. The returned `Key` is shown once. It cannot mint keys, manage databases
or reach another database; parent revocation or rotation revokes it. ts-auth snake_case fields map to `ProjectId`
and `ExpiresAt`. HTTP refusals use the existing `TerraBaseException` types and scoped-key requests are sent once.

The [shared HTTP fixtures](contracts/terrabase/agent/) record the [published API contract](../SQL-API.md#databases-for-agents)
with synthetic values. Both supported .NET frameworks replay them; live producer qualification is separate.

## Placement

```csharp
var db = await client.CreateDatabaseAsync(project, "orders", homeRegion: "gru", replicaRegions: ["sjc"]);
await client.SetDatabasePlacementAsync(project, db.Id, allRegions: true);
await client.MoveDatabaseHomeAsync(project, db.Id, "sjc");
```

`TerraBaseManagedDatabase` carries `HomeRegion`, `ReplicaRegions` and `AllRegions`. `SetDatabasePlacementAsync` takes
exactly one of `replicaRegions` or `allRegions`. `MoveDatabaseHomeAsync` waits up to 10 minutes (set the caller's
`HttpClient.Timeout` to at least that) and resends only on `503 move_unresolved`, at most 5 times after `Retry-After` or
1 s. Tests replay `contracts/terrabase/placement/`; live producer qualification has not run.

## Database request observations

Read recent authorized request observations with `await database.ListRequestLogsAsync(status: "2xx", cancellationToken: ct)`.
Omit `status` to read all status classes. The response preserves `DatabaseId`, `Items`,
`Truncated`, `RecordingLimited` and each nullable `RequestId`. The server returns at most
100 observations from the last 24 hours; recording is best effort. See the
[pinned request-log contract](contracts/terrabase/request-logs.md).

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

SQL and PostgreSQL primary keys are user-chosen integer, text or composite values; the SDK does not hash or remap
them to ULIDs.
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

`TerraScale.DotNet.TerraBase` talks to the C# TerraBase server over its public HTTPS client listener.
`TerraBaseSqlClient` runs SQL; see [SQL](#sql). `TerraBaseDocumentsClient` addresses projects and databases:
`Project(projectId)` creates, lists, reads, renames and deletes databases and reads usage, analytics, saved queries and
backup destinations; `Database(projectId, name)` reads usage and analytics, branches, backs up and restores, and
deploys and invokes functions. `TerraBaseManagementClient` covers the Management API: organizations, the current
organization, plans, projects, databases, branches, members, invitations, ownership transfer, audit retention and
audit events. `TerraBaseRegionsClient` reads the anonymous [serving-region catalog](contracts/terrabase/regions.md).
`TerraBaseHealthClient` reads the anonymous health probes.

```csharp
using TerraScale.DotNet.TerraBase;

using var httpClient = new HttpClient(TerraBaseHttp.CreateHandler());
var client = new TerraBaseDocumentsClient(httpClient, new TerraBaseDocumentsClientOptions
{
    BaseAddress = discoveredOrigin,
    AccessTokenProvider = token => GetProjectAccessTokenAsync(token),
});

TerraBaseManagedDatabase app = await client.Project(projectId).CreateDatabaseAsync("app", cancellationToken: cancellationToken);
TerraBaseManagedDatabase copy = await client.Database(projectId, "app").BranchAsync("app-copy", cancellationToken);
```

A database is stored and served in exactly one region, its `HomeRegion`. `CreateDatabaseAsync` without a home region
uses the called region. Call `DeleteDatabaseAsync` in the database's region. A 204 response records logical deletion
and local cleanup; branches remain independent.

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
(`Func<CancellationToken, ValueTask<string>>`); `TerraBaseDocumentsClient` also takes a static `ApiKey`. Supply at most one
credential source, and none only for a private plain-HTTP origin. The provider runs
once per request after origin validation, shares the request deadline, and must
be thread-safe and honor cancellation. It supplies the credential accepted by
the deployed server; the SDK performs no exchange, background refresh, or retry.
Current routes and credentials remain supported. The TS-589 project JWT and
route migration will follow its server release.

Error statuses throw `TerraBaseException`, an `HttpRequestException` with
`ErrorCode`. Subclasses name the
statuses callers act on: `TerraBaseValidationException` (400, 413, 415),
`TerraBaseAuthenticationException` (401), including `TerraBaseKeyRevokedException`
and `TerraBaseKeyExpiredException` (permanent; replace the key), `TerraBaseForbiddenException` (403),
`TerraBaseNotFoundException` (404), `TerraBaseConflictException` (409),
`TerraBasePreconditionFailedException` (412) and `TerraBaseUnavailableException`
(503). A request that exceeds the fixed 30-second deadline throws `TimeoutException`.
An earlier `HttpClient.Timeout` also throws `TimeoutException`; caller cancellation
remains `OperationCanceledException`. TerraBase refuses `503 remote_region_unavailable`,
`confirmation_busy` and `catalog_stale` before it executes a request, so the SDK resends any request
with a buffered body at most twice. Safe GET/HEAD reads also resend `429 tenant_busy`, `503 auth_unavailable`,
`storage_busy` and `capacity`, gateway 502 and 504 answers, and a lost connection. Each resend asks the
`AccessTokenProvider` again and waits for `Retry-After` plus up to 250 ms, or full jitter over 250/500 ms; when that
wait would pass the request deadline, the failure is thrown at once. Authentication errors never retry.
The SDK does not exchange or cache tokens; caller-owned token providers must drop revoked/expired tokens
and stop exchanging the refused key. `503 write_outcome_unknown` and all other errors are sent once. TerraBase
rejects `Idempotency-Key`; do not replay a write whose outcome is unknown.
`TerraBaseHttp.CreateHandler()` fails a connection attempt after 10 seconds and replaces pooled connections every
2 minutes, so long-lived clients follow DNS changes.

Supplied `HttpClient` handlers **must disable automatic redirects**. Use
`TerraBaseHttp.CreateHandler()` or set `AllowAutoRedirect = false` on your handler
before constructing the client. Redirects can forward invitation tokens, request
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
sent once. Every `TerraBaseException` carries
the answering region (`X-TerraBase-Region`) when TerraBase named it.

No TerraBase runs in this repository (user rule, 2026-10-11: no repository other
than the monorepo depends on TerraBase). The clients are tested over HTTP fakes.
`contracts/terrabase/routes.json` records the existing TerraBase routes the clients call;
`TerraBaseContractTests` checks the clients send exactly those, and the monorepo's
ts-auth Fast suite checks that the TerraBase built from the same commit serves each
one, so the SDK and TerraBase cannot drift apart silently. ts-auth's suites exercise
these clients end to end against that same TerraBase.

```bash
nice -n 19 dotnet run --project tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj --framework net10.0 --configuration Release
```

`TerraBasePlatformClient` lists TerraScale's own databases (`platform-auth`, `platform-dashboard`) for the dashboard's
platform-admin pages: pass the signed-in admin's ts-auth access token through `AccessTokenProvider` (it must carry
`platform_role: admin`). It is read-only.

See [TerraBase consumers](docs/terrabase-consumers.md).

## ts-auth client

`TerraScaleAuthClient` exposes shipped Platform, project sign-in configuration and OIDC
operations. Platform methods use the signed-in user's bearer and role; project backend
operations carry their documented project-key authority. Hosted/device or registered-client
OIDC sign-in establishes the user's token; the SDK does not invent callbacks or sign-in grants.
Historical instance management and headless surfaces are unsupported; retained legacy types
are not evidence that their routes exist. See [the supported contract](contracts/auth/README.md).

```csharp
using TerraScale.DotNet;

using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
var auth = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions
{
    BaseAddress = new Uri("https://auth.terrascale.tech"),
    InstanceSlug = "terrascale"
});
var organizations = await auth.Platform.ListOrganizationsAsync(signInAccessToken,
    cancellationToken: cancellationToken);
```

The auth client calls its `BaseAddress` directly; there is no regional discovery or proxy switch. This does not
route database requests. `AddTerraScaleAuthClient` supplies dependency-injection registration
with redirects disabled. Every auth operation is sent once; retry behavior and body/deadline bounds are fixed by the SDK.
`TerraScaleAuthException` exposes typed status/retry metadata and only
known fixed error codes. Messages are fixed safe diagnostics; arbitrary
producer messages, codes and response bodies are never retained. Options
formatting omits credentials.

Project and API-key IP allowlists use typed CIDR records and setters; see [CIDR contracts and examples](contracts/auth/cidrs.md).
Account MFA status and organization MFA policies use typed records and operations; see [MFA policy contracts and examples](contracts/auth/mfa-policy.md).

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

[Package release](docs/package-release.md) records the release procedure; it does not authorize
publication. NuGet remains on hold. Source/local-pack consumers must verify the loaded SDK DLL
matches their pinned package, then qualify their actual service flow. Client-side validation does
not establish server admission or plan enforcement; retain scope, role, region and quota errors.
