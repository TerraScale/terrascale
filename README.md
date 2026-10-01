# TerraScale.DotNet

The official .NET SDK for TerraScale native database APIs and ts-auth. The
package supports .NET 10 LTS and .NET 11; a .NET 11 toolchain builds both targets.

## Native database client

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
  --minimum-expected-tests 135
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
