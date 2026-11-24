# TerraScale.DotNet

The official .NET SDK for TerraBase (its v1 HTTP API) and ts-auth. The package targets .NET 11.

The .NET SDK is a preview. The current source package version is `0.0.12`. NuGet publication remains on hold:
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
`terrascale.sql.v2`, [contract](contracts/terrabase/sql-websocket.md)). Reads need `sql:read`; writes and
schema changes need `sql:write`. The other SDKs copy this shape ([API shape](../SQL-API.md)).

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

ADO.NET and Dapper: `TerraBaseDbConnection` wraps a `TerraBaseSqlDatabase` as a `DbConnection`, so
`QueryAsync<T>`/`ExecuteAsync` from Dapper (or plain `DbCommand` readers) work over SQL REST autocommit
statements (`ExecuteNonQueryAsync`, `ExecuteScalarAsync`, `ExecuteDbDataReaderAsync`; the sync overloads throw —
the SDK is async-only). Parameters map to `?` positional or `@name`/`:name`/`$name` named arguments. Transactions
run over the SQL WebSocket session: `BeginTransactionAsync`/`CommitAsync`/`RollbackAsync` (Serializable only; the
sync transaction methods throw like the other sync paths).

The SDK's ADO.NET types follow SQLite's ADO.NET conventions. A command can run several statements in one request
or inside an open transaction and return one result set per statement that returns columns. `DateTime`,
`DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid` and `decimal` arguments are sent as text and
`GetFieldValue<T>` reads them back.

For Npgsql and other .NET Postgres clients, see the [.NET ORM guide](https://docs.terrascale.tech/guides/orm-dotnet).

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
  `unique_violation`), `Sqlstate` (such as `23505`) and `Statement` (zero-based index). A confirmed refusal rolls the request back;
  `write_outcome_unknown` and `commit_uncertain` require reconciliation because the write may have committed.
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
Session errors have a null `StatusCode`. A refused upgrade throws a `TerraBaseException` carrying the HTTP status.
The upgrade goes through the caller's `HttpClient` and its handlers with
the same credential as REST (a credential-free client reaches a private listener over HTTP/2 extended CONNECT), and is
never resent. A lost committing reply throws `write_outcome_unknown` (08007), and every later call throws the
same error. If no committing request was sent, a lost open transaction throws `transaction_rolled_back` until
`RollbackAsync` clears it; only then can the next call reconnect.

### Consistency and bookmarks

Every region commits writes. The client tracks the newest `TerraScale-Bookmark` per database, shared by every
`Database(projectId, name)` handle, `TerraBaseDbConnection` and SQL session of it: each request and WebSocket frame sends
it, and each answer's bookmark is merged back (per region, the larger sequence wins), so overlapping calls never move it
backwards and reads observe the client's own writes in whichever region answers. `TerraScale-Consistency` is `fast`
(the default) or `strong`:

```csharp
var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = apiKey, Consistency = TerraBaseConsistencyMode.Fast });
var db = sql.Database(projectId, "app");
await db.ExecuteAsync("UPDATE accounts SET balance = balance - ? WHERE id = ?", [25, "acct_1"],
    new TerraBaseSqlRequestOptions { Consistency = TerraBaseConsistencyMode.Strong }, ct);
string bookmark = db.Bookmark;
workerDb.ObserveBookmark(bookmark);
await db.WithConsistency(TerraBaseConsistencyMode.Strong).ExecuteAsync("DELETE FROM holds WHERE expires_at < unixepoch()", cancellationToken: ct);
await using var session = await db.ConnectAsync(ct);
await session.BeginStrongAsync(ct);
```

`TerraBaseSqlRequestOptions.Bookmark` merges a bookmark from another process into one call; `ObserveBookmark` merges it
into every later call. A strong call that loses a race throws `serialization_conflict` (40001, HTTP 409) and a schema
change during the request `schema_mismatch`; both rolled back, and REST requests resend them at most twice. A bookmark
or consistency the server does not accept throws `invalid_bookmark` or `invalid_consistency` (22023).
`TerraBaseDbConnection.BeginTransactionAsync(IsolationLevel.Serializable)` begins a strong transaction.

## Live data

The current source/local pack provides live table shapes with `sql:read` credentials:

```csharp
var shape = db.Shape("todos", new ShapeOptions { Where = "done = 0", Columns = ["id", "title"] });
await foreach (var view in shape.WatchAsync(ct))
{
    foreach (var row in view.Rows)
        Console.WriteLine($"{view.Status}: {row.GetInt64("id")} {row.GetString("title")}");
}
```

Each view contains immutable `TerraBaseSqlRecord` rows, matching SQL query results, and an applied
`Offset`. `Status` is `Loading`, `Live` or `Reconnecting`. The SDK finishes the paged snapshot before
publishing rows, applies commits in order and overlays regional provisional writes. WebSocket hints use
bearer headers and trigger HTTP catch-up; HTTP long-polling takes over when hints are unavailable.
Transient failures resume from the applied offset with backoff; `must_refetch` clears the view and starts
a new snapshot. Authentication and permission failures end enumeration. Cancel or dispose the enumerator
to close its work. Each enumeration owns an independent subscription.

Rows support the same `GetInt64`, `GetBytes` and `As<T>(JsonTypeInfo<T>)` accessors as SQL query rows.

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
var db = await client.CreateDatabaseAsync(project, "orders", placement: TerraBaseDatabasePlacement.In("gru", "sjc"));
await client.SetDatabasePlacementAsync(project, db.Id, placement: TerraBaseDatabasePlacement.In("fra", "gru", "sjc"));
```

Every region that holds a database is equal. `TerraBaseManagedDatabase` carries `Regions` (every region that holds it,
sorted by region id; the order has no meaning) and `AllRegions`. `CreateDatabaseAsync` and `SetDatabasePlacementAsync`
take a `TerraBaseDatabasePlacement` value made with `In` (one to nine distinct regions) or `EveryRegion`. Each region commits writes locally. `Database(projectId, name).GetRegionsAsync` reads each region's
`AppliedBookmark`, `LagMs` (-1 when unknown) and `Healthy` as the answering region observes them, sampled at
`ObservedAtUtc`. `CreateDatabaseAsync(project, name, defaultConsistency)` and
`SetDatabaseConsistencyAsync` set the database's `DefaultConsistency`, which `TerraBaseManagedDatabase` reports.
Tests replay `contracts/terrabase/placement/`; live producer qualification has not run. All-regions placement
(`AllRegions`) holds the database in every serving region, including regions added later
(`ts db placement ... -all-regions`).

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

The generator uses Ulid 1.4.1 with cryptographic randomness and instance-owned
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
HTTPS `Uri` supplied by an authorized integration, not created by this SDK. See the [zero-configuration contract](https://docs.terrascale.tech/guides/zero-configuration).

## Base URL rules

Every `BaseAddress` and `AuthAddress` option, `TerraBaseStaticEndpointResolver`, and `TERRASCALE_BASE_URL`
follow the origin rule in [`sdk/SQL-API.md`](../SQL-API.md#client). Building the client or resolver throws
`ArgumentException` before any request for a base address with user info or a path, a string of more than 2,048 characters, a name of more
than 253 characters or with a label of more than 63, a label that begins or ends with a hyphen or begins with `xn--`, the name
`loopback`, and the unspecified addresses `0.0.0.0`, `[::]` and `[::ffff:0:0]` in any spelling. A `Uri` option is
parsed first by the caller's `new Uri(...)`, which refuses some of these strings with its own `UriFormatException`.

## TerraBase client

`TerraScale.DotNet.TerraBase` talks to the C# TerraBase server over its public HTTPS client listener.
`TerraBaseSqlClient` runs SQL; see [SQL](#sql). `TerraBaseDatabaseClient` addresses projects and databases:
`Project(projectId)` creates, lists, reads, renames and deletes databases and reads usage, analytics, saved queries and
backup destinations; `Database(projectId, name)` reads usage and analytics, per-region replication (`GetRegionsAsync`), branches (including `BranchIfIdAsync`/`DeleteIfIdAsync` guarded by the immutable database ID), backs up and restores, and
deploys and invokes functions. `TerraBaseManagementClient` covers the Management API: organizations, the current
organization, plans, projects (including close and erase), history erasure, members, invitations, ownership transfer,
audit retention and audit events. Databases and branches are not management operations; they live on
`TerraBaseDatabaseClient` above. `TerraBaseRegionsClient` reads the anonymous [serving-region catalog](contracts/terrabase/regions.md).
`TerraBaseHealthClient` reads the anonymous health probes of its `baseAddress`, else the `HttpClient`'s `BaseAddress`,
else `https://api.terrascale.tech`.

```csharp
using TerraScale.DotNet.TerraBase;

using var httpClient = new HttpClient(TerraBaseHttp.CreateHandler());
var client = new TerraBaseDatabaseClient(httpClient, new TerraBaseDatabaseClientOptions
{
    BaseAddress = discoveredOrigin,
    AccessTokenProvider = token => GetProjectAccessTokenAsync(token),
});

TerraBaseManagedDatabase app = await client.Project(projectId).CreateDatabaseAsync("app", cancellationToken: cancellationToken);
TerraBaseManagedDatabase copy = await client.Database(projectId, "app").BranchAsync("app-copy", cancellationToken);
```

`Regions` names every region that holds the database. `CreateDatabaseAsync` without regions uses the called region.

Every region that holds a database is equal.
REST, PostgreSQL and SQL WebSocket can read and write in any region that holds the database. Copies converge
as commits replicate. The SQL SDK sends its latest `TerraScale-Bookmark` so reads can observe previous writes in
whichever placed region answers. All-regions
placement (`-all-regions` on `ts db create` and `ts db placement`) holds the database in every
serving region; otherwise set an explicit region list.

The [published pricing catalog](https://terrascale.tech/pricing.json) meters database writes and storage; selecting additional regions does not multiply storage billing. Deleting a database destroys its backup data
keys, making retained encrypted backups unreadable; physical cleanup is separate. Forks remain independent.

```csharp
// TerraBase's private listener on the same host; the caller configures the required peer credential on httpClient.
// Qualify this pinned-contract example against the OCI server before use.
var management = new TerraBaseManagementClient(httpClient, new TerraBaseManagementClientOptions
{
    BaseAddress = new Uri("http://127.0.0.1:8081"),
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
(`Func<CancellationToken, ValueTask<string>>`); `TerraBaseDatabaseClient` also takes a static `ApiKey`. Supply at most one
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
remains `OperationCanceledException`. The SDK resends safe GET/HEAD reads up to twice after `429 tenant_busy`,
`503 auth_unavailable`, `storage_busy` or `capacity`, gateway 502 and 504 answers, or a lost connection. Each resend asks the
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

Use the global API origin, `https://api.terrascale.tech`, or configure `BaseAddress`/`EndpointResolver` for
another deployment. `Region` sends `X-TerraScale-Region-Pin`. Every `TerraBaseException` carries the
answering region (`X-TerraBase-Region`) when TerraBase named it. See the [regions guide](https://docs.terrascale.tech/concepts/regions).

`contracts/terrabase/routes.json` records the existing TerraBase routes the clients call. The monorepo's journey
drives these clients through ts-auth, the dashboard and its .NET runner against the TerraBase built from the same
commit.

`TerraBasePlatformClient` lists TerraScale's own databases (`platform-auth`, `platform-dashboard`) for the dashboard's
platform-admin pages: pass the signed-in admin's ts-auth access token through `AccessTokenProvider` (it must carry
`platform_role: admin`). It is read-only.

## ts-auth client

`TerraScaleAuthClient` exposes shipped Platform, project sign-in configuration and OIDC
operations. Platform methods use the signed-in user's bearer and role; project backend
operations carry their documented project-key authority. Hosted/device or registered-client
OIDC sign-in establishes the user's token; the SDK does not invent callbacks or sign-in grants.
The historical instance management clients are retired. See [the supported contract](contracts/auth/README.md).

```csharp
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

using var http = new HttpClient(TerraBaseHttp.CreateHandler());
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
`AuthRefused` exposes typed status/retry metadata and only
known fixed error codes. Messages are fixed safe diagnostics; arbitrary
producer messages, codes and response bodies are never retained. Options
formatting omits credentials.

Project and API-key IP allowlists use typed CIDR records and setters; see [CIDR contracts and examples](contracts/auth/cidrs.md).
Account MFA status and organization MFA policies use typed records and operations; see [MFA policy contracts and examples](contracts/auth/mfa-policy.md).
Organization SSO connections (get, put, verify, delete) use typed records whose OIDC client secret is sent once and never echoed.
End-user lifecycle webhook deliveries are verified by `TerraScaleWebhooks.VerifyWebhook` (Standard Webhooks `v1` HMAC-SHA256, 5-minute timestamp tolerance); it returns the `WebhookVerification` union of the parsed event or a fixed `WebhookRefused`.

## Verification and package distribution

```bash
dotnet restore TerraScale.DotNet.slnx --locked-mode
dotnet build TerraScale.DotNet.slnx --configuration Release --no-restore
```

NuGet remains on hold. Source/local-pack consumers must verify the loaded SDK DLL
matches their pinned package, then qualify their actual service flow. Client-side validation does
not establish server admission or plan enforcement; retain scope, role, region and quota errors.

Built-in project API-key tokens renew in the background from 60 seconds before expiry
(or at half-life for short tokens). Concurrent callers share one exchange and continue
using a valid token; only missing, actually expired, or refused tokens require a wait.

## Region-set placement

Create and update with one to nine distinct serving region ids in `regions`, separately from `allRegions: true`
and from a rename. The response sorts `regions` by region ID. `placementVersion` changes with membership
and stays unchanged for an identical set. Region status reports each region's applied bookmark, lag in
milliseconds and health.
Use the global API origin for clients that do not need to select a particular region.


## Project Authentication management

Pass the project secret API key to `client.Projects.DisableUserAsync(secretKey, projectId, userId)` and the other management methods. Reads require `auth:read` and changes require `auth:write`. Step-up operations require a person’s sign-in token. Keep keys in the backend.

Project MFA/session policy fields and sign-out-everywhere source APIs are available in the source SDK; published package qualification follows release.
Project invitation and waitlist management additions are not yet published.
Project user profile creation, merge-patch updates, and partial password-hash imports
are available in the source SDK.
Project organization management supports CRUD, memberships, emailed invitations and DNS verified domains.
User-owned API key creation, listing, revocation and backend verification are available in the in-repository auth client.

### SQL session outcome rule

An SDK reports `transaction_rolled_back` only after receiving that answer from the server, or after losing an
open transaction for which no committing request was sent. If COMMIT or a request that can commit (including an
autocommit statement or batch) may have left the client and its answer is lost, the outcome is uncertain.
The SDK returns `write_outcome_unknown` (SQLSTATE `08007`) or its existing commit-uncertain error, retains that
failure for every later call on the session, and closes the connection. It never reconnects or automatically
retries an uncertain request. If the client cannot prove no request byte left, it treats the request as sent.
An execute outside a confirmed transaction, or behind unanswered transaction control, is potentially committing.
A lost read or non-committing statement inside a confirmed open transaction retains the transaction-lost behavior.
Only a confirmed refusal or a request proven not sent permits a rollback assertion; reconcile an uncertain write
through another session before deciding whether to repeat it.

## Rules (unreleased)

Database methods `GetRulesAsync`, `DeployRulesAsync`, `ListRulesVersionsAsync`, `GetRulesVersionAsync`, `RestoreRulesAsync`, `TestRulesAsync` manage Rules and simulate requests. Reads require `databases:read`; deploy and restore require `databases:write`.

Pass the current version for `If-Match`, or `null` for `If-None-Match: *` on first deployment. Versions are newest first; pass the page’s continuation cursor to fetch older versions. Each restore creates a new version. Validation errors retain typed compiler problems; stale versions return `412 version_mismatch`. The simulator evaluates deployed or draft Rules without accessing rows.

## Custom tokens (source checkout)

Mint a token on your backend and hand it to a browser with
`auth.Projects.MintCustomTokenAsync(projectKey, project, userId, database, claims)`.
