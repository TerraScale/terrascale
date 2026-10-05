# TerraScale.DotNet

The official .NET SDK for TerraBase, the TerraScale native database APIs and
ts-auth. The package supports .NET 10 LTS and .NET 11; a .NET 11 toolchain builds
both targets.

## TerraBase client

`TerraScale.DotNet.TerraBase` talks to the C# TerraBase server over its public
HTTPS client listener. `TerraBaseDocumentsClient` covers the Documents API v1:
databases, collections, documents, queries and transactions.
`TerraBaseManagementClient` covers the Management API: organizations, the
current organization, plans, projects, databases, branches, members,
invitations, ownership transfer, audit retention and audit events. Shapes and
the bulk data endpoints are not covered yet.

```csharp
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

using var httpClient = new HttpClient(TerraBaseHttp.CreateHandler());
var documents = new TerraBaseDocumentsClient(httpClient, new TerraBaseDocumentsClientOptions
{
    BaseAddress = new Uri("https://terrabase.internal:8443"),
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

`GetDocumentAsync` returns `null` for an absent document. `CreateDocumentAsync`
posts a document and TerraBase generates a UUIDv7 id. Lists and queries return
pages with `NextCursor`. Patches take an RFC 7396 merge patch `JsonElement` or a
list of `TerraBasePatchOperation`. `TransactAsync` applies `Create`, `Put`,
`Patch`, `Delete`, `Assert`, `AssertAbsent` and `AssertCount` operations
atomically.

```csharp
var management = new TerraBaseManagementClient(httpClient, new TerraBaseManagementClientOptions
{
    BaseAddress = new Uri("https://terrabase.internal:8443"),
    ServiceCredential = dashboardServiceCredential
});

TerraBaseOrganization organization = await management.CreateOrganizationAsync(
    authenticatedUserId, "Acme", cancellationToken);
TerraBaseProject project = await management.CreateProjectAsync(
    authenticatedUserId, organization.Id, "web", cancellationToken);
TerraBasePage<TerraBaseProject> page = await management.ListProjectsAsync(
    authenticatedUserId, organization.Id, cancellationToken: cancellationToken);
```

The service credential is sent as a bearer token. Every management call takes
the acting `userId` first and sends it as `X-TerraBase-User-Id`; organization
routes also send `X-TerraBase-Organization-Id`. Take the user id from an
authenticated session, never from browser input. Pages are
`TerraBasePage<T>(Items, Next)`; pass `Next` as `after` with an optional `limit`.

Concurrency uses document versions (`tbv1-…` ETags).
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
fails on any trimming or AOT warning in it. The legacy `Native` and
`AuthService` slices still use reflection-based JSON and are not.

Both clients resolve their endpoint per request through `ITerraBaseEndpointResolver`.
`BaseAddress` creates a `TerraBaseStaticEndpointResolver`; alternatively, set
`EndpointResolver` alone to supply a resolver. Resolution shares the request deadline
and cancellation token, and every returned origin is validated before credentials
are sent. Atlas discovery is deferred to TS-507; no production origin is hardcoded.

Tests never use a fake TerraBase. `TerraScale.DotNet.Testing`
(`src/TerraScale.DotNet.Testing`) starts the real release binary
`terrabase-linux-amd64`, pinned by tag and SHA-256 in `TerraBaseRelease.Pinned`,
with a fresh data directory, a generated self-signed localhost certificate and a
generated service credential. TerraBase binds both listeners to OS-assigned ports;
the helper discovers their addresses from the pinned release's startup logs.
That release clears generic-host configuration sources, so inherited logging
filters and console formatters do not change these announcements. CI verifies
startup while its environment requests disabled logging and the JSON formatter;
these inherited settings are ignored by the pinned server.
It downloads the binary once into
`~/.cache/terrascale/terrabase/<sha256>/` (or `TERRABASE_CACHE_DIRECTORY`) using
`TERRABASE_DOWNLOAD_TOKEN` or `ORG_READ_TOKEN`, because terrabase is private, and
checks the SHA-256 on every use. `TERRABASE_BINARY` points at a binary you
already have; it must match the pinned digest too. Cached and explicit binaries
must be executable by the effective user; resolution reports `chmod u+x` guidance if access is denied.
Downloads have a ten-minute deadline and a 256 MiB size limit, checked against
both the declared length and streamed bytes. Failed downloads remove partial files.
If removal also fails, the primary exception retains the secondary error in
`Data["TerraBaseCleanupException"]` for diagnosis and manual cleanup.
Disposal waits up to five seconds for process exit and redirected output completion
before releasing resources and deleting data. A failed termination or timeout retains
those resources so a later disposal call can retry.
Failed directory deletion can also be retried, without reusing disposed resources.
If startup cleanup fails, the original exception retains the server in
`Data[nameof(TerraBaseTestServer)]` so its cleanup can be retried.

```bash
ORG_READ_TOKEN=<forgejo read token> nice -n 19 dotnet run \
  --project tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj \
  --framework net10.0 --configuration Release
```

When the binary is neither cached nor downloadable, the TerraBase tests are
skipped, except under `CI=true`, where they fail. Other services, such as
ts-auth, start the same binary through the same helper:
[TerraBase consumers](docs/terrabase-consumers.md).

## Native database client (legacy)

`Native/` targets the retired Rust UUID/shard API; it is kept so existing
consumers compile, and new code should use the TerraBase client.

`TerraScale.DotNet.Native.NativeClient` exposes native v1 document reads,
bounded typed queries, atomic commits, and original transaction status.
Provisioned tenant, database, collection and document UUIDs, shard identifiers,
numeric field identifiers, schema generation and schema digest are explicit.
`NativePoint` preserves absent, deleted, value and conflict states with their
complete causal context and siblings. Native integer values preserve all 64
bits; bytes use canonical base64url.

Database requests use authenticated Atlas admission followed by an eligible
direct regional origin. `NativeDiscoveryRouter` calls Atlas with the current
credential and exact tenant/database/shard scope and reuses the matching bounded lease. It checks
scope, operation capabilities, observation monotonicity and lease freshness
immediately before dispatch. Admission failure stops the request.
`api.terrascale.tech` is a proxy and retired root regional hosts are
rejected as native origins. There is no database proxy fallback.

```csharp
using TerraScale.DotNet.Native;

var scope = new NativeScope(tenantUuid, databaseUuid, provisionedShard);
using var admission = new NativeDiscoveryRouter(TimeSpan.FromSeconds(3));
using var database = new NativeClient(
    scope,
    admission,
    cancellationToken => ValueTask.FromResult(currentApiKey),
    TimeSpan.FromSeconds(15));

NativeResult<NativePoint> point = await database.ReadAsync(
    collectionUuid, documentUuid, cancellationToken);
```

A direct-origin constructor is available for an origin already admitted by the
caller. It does not discover eligibility or establish a grant. Within the TerraScale zone, origins must use canonical single-level nonreserved
regional HTTPS hosts with the default port. Independently provisioned HTTPS
origins remain available; HTTP loopback requires explicit `development: true`.
`NativeOrigin.Validate` exposes this validation without sending a request.

Prepare a commit with `PrepareCommit` before its first transmission. The
prepared body, original expiry, nonce, authority and canonical SHA-256 effect
digest remain fixed. `NativeOperationStore.SaveAsync` retains that original
operation before dispatch; `LoadAsync` and `RestoreCommit` restore it for
recovery. Lost responses leave the outcome uncertain. Use
`TransactionStatusAsync` or bounded `NativeRecovery.ResolveAsync` with the
original identity. Pending, unknown and expired states never establish abort.
The transport sends each operation once; it does not automatically replay a
mutation or change its identity.

Owned native transports disable redirects, cookies and decompression.
Caller-owned `HttpClient` instances must be configured the same way; the SDK
never reconfigures or disposes them. Deadlines and cancellation are explicit.
Failures expose fixed safe metadata instead of response bodies or credentials.

The pinned producer OpenAPI with a documented status-description correction,
atomic vectors, historical Atlas wire identity, derived production-origin success
codec fixture and historical forbidden fixture are tracked under `contracts/native`, embedded in
the SDK, and included in the package with their provenance. These inputs are
contract evidence; local handler checks do not prove live-service behavior.

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

The public auth origin uses Atlas' separate auth discovery contract and ranked
regional `.auth` origins. `UseReverseProxy = true` explicitly selects the auth
proxy; custom auth origins are used directly. This does not route database
requests. `AddTerraScaleAuthClient` supplies dependency-injection registration
with redirects disabled. Only safe reads can retry; auth mutations are sent
once. `TerraScaleAuthException` exposes typed status/retry metadata and only
known fixed error codes. Messages are fixed safe diagnostics; arbitrary
producer messages, codes and response bodies are never retained. Options
formatting omits credentials. Auth discovery accepts the owner-published
regional token grammar rather than only the three launch fallback regions.

## Verification and package distribution

```bash
dotnet build TerraScale.DotNet.slnx --configuration Release
dotnet format style TerraScale.DotNet.slnx --verify-no-changes --no-restore
dotnet run --project tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj \
  --framework net10.0 --configuration Release --no-build --no-restore -- \
  --minimum-expected-tests 424
```

Run the suite for `net11.0` with a .NET 11 toolchain too. Tests use TUnit and
cover native public semantics plus the retained ts-auth contract. CI also
checks dependency audit failure handling, fails closed on vulnerable packages,
and checks installed native package consumers for both frameworks.

CI prepares a source-revision-bound `.nupkg` and verifies its native inputs,
framework assemblies and repository identity before exporting its artifact.
[Package distribution](docs/native-package-distribution.md) describes the
exact-byte handoff and its publication boundary.
[Package qualification](scripts/qualification/README.md) describes the bounded
package-only consumer. Publishing and live-service qualification are separate
gates; neither is implied by a source build or artifact export.

## Native client migration

This native SDK release removes `TerraScaleClient`, `TerraScaleClientOptions`,
`AddTerraScaleClient`, and the obsolete Entity Framework provider. Construct
`NativeClient` with a provisioned `NativeScope`, current scoped-key provider,
and `NativeDiscoveryRouter`, or register that client explicitly with DI.
Auth consumers use `TerraScaleAuthClientOptions` and `AddTerraScaleAuthClient`.

Regional database origins are `https://<region>.api.terrascale.tech`, for example
`https://gru.api.terrascale.tech`. Direct-origin callers own prior provisioning
and capability checks; accepting a canonical URL does not establish eligibility.
Native clients and discovery routers use owned workers for direct SDK invocation
and context-capturing continuations. Key/admission providers and custom HTTP
handler/content callbacks must return tasks representing their complete work;
detached or async-void work is unsupported. Callbacks that opt out of context
capture with `ConfigureAwait(false)`, `Task.Run`, or similar APIs must keep
shared-pool continuations nonblocking. Shared-pool starvation caused by such
callback code is outside the finite-deadline guarantee.
Extensions receive a separate token cancelled on distinct owned workers, so
blocking cancellation registrations cannot delay the caller-facing deadline.
Cancellation callbacks retain operation capacity until completion. Response
disposal waits for actual body invocation completion, allowing captured read
continuations to finish before synchronous cleanup runs.
Owned transport disposal is deferred until all operations, cancellation callbacks
and response cleanup drain, then runs on an owned worker. `Dispose` stops new
calls without waiting for noncooperative outstanding work.
Cancellation sources introduced by a caller-owned `HttpClient`, such as its
`Timeout`, execute their own registrations; callbacks registered with those
sources must remain nonblocking.
Concrete framework `StreamContent` uses an isolated invocation for every stream
read, including reads after an earlier suspension. Custom content (including
`StreamContent` subclasses) serializes directly into a bounded destination.
Custom callbacks invoked outside the captured context by framework helpers,
such as connection callbacks or a custom serializer's own copy loop, must remain
nonblocking.
Discovery caches only authenticated leases for the same credential, coalesces
scope requests, and retains at most 256 scope observation floors for its lifetime.
Floors survive lease expiry: deployment or cell changes require explicit
reprovisioning and a new router. The pinned contract rejects future publication
timestamps and consumes fetch latency from the lease, so clocks must be synchronized.
Recovery persistence flushes the file and synchronizes its rename before `SaveAsync`
returns: Linux/macOS anchor temporary creation, no-replace publication, cleanup and fsync
to one open directory descriptor; Windows uses a write-through move.
Unix recovery loads open without following links and validate permissions on the
opened file. Unix directories must have a trusted owner and deny group/other
writes. Windows callers must secure the directory hierarchy with private ACLs.
