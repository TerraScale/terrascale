# TerraScale.DotNet

The C#/.NET client SDK for TerraScale, targeting .NET 10 LTS and .NET 11
(`net11.0` is built when the toolchain supports it). The workspace
[goal](../GOAL.md) defines the product and SDK responsibilities. Generated
models and route metadata consume the pinned public contract published by
terrabase; named clients implement customer operations against that contract.

## Atlas routing (ADR 0050)

`https://api.terrascale.tech` is the Atlas reverse proxy. By default the SDK
asks `https://atlas.terrascale.tech/api/v1/discovery?database=…&branch=main`
first, validates the existing ADR 0050 document, and sends database requests
directly to the top-ranked eligible `https://<region>.api.terrascale.tech`
endpoint (GRU: `https://gru.api.terrascale.tech`). Atlas ranks measured
`X-TerraScale-Region-Rtt`, then client distance, then placement order.

The SDK keeps its pin for the document's `refreshSeconds` lifetime, then
refreshes and selects from the new ranking. Transport failures and 5xx demote
the failed region and fail over to the next eligible endpoint, which becomes
the pin for subsequent requests. A 421 repairs once using authoritative
placement when provided, otherwise the next ranked endpoint. Corrective
rediscovery uses the existing `rev` and `stale=1` parameters. Routing failover
works with the default retry budget; replay still requires a safe request or
a stable idempotency key. Caller cancellation and `Retry-After` are respected.

If discovery fails or times out, database requests and reactive attach use
`https://api.terrascale.tech` so Atlas can proxy them. Database-less member
routes use the Atlas API proxy, since discovery requires a database selector.
The catalog route itself targets Atlas; organization and project routes use the
configured `BaseAddress`.

A custom `BaseAddress` disables discovery, including when
`DiscoveryBaseAddress` is supplied. `RegionAffinity = Strict` requires
`PreferredRegion` and uses its `.api.` regional host directly;
`RegionAffinity = Failover` keeps the preferred-region option and can move to
another eligible region. `DiscoveryBaseAddress` and `DiscoveryTimeout`
(default 3 seconds) configure metadata requests for the default API base.
Discovery caches and pins belong to the client instance.

## What is here

- `TerraScaleClient` over a caller-owned `HttpClient`: authentication (API key
  or bearer token/provider), Atlas discovery and direct regional routing with
  421 repair and endpoint failover, bounded transient retries, idempotency and
  request IDs, typed `TerraScaleApiException` errors, and
  dependency-injection registration.
- `TerraScaleAuthClient`: the ts-auth hosted identity provider from the same
  package. One instance-scoped client covers the whole management API — users
  (including impersonation, MFA factors, passkeys), sessions, API keys,
  organizations with memberships/invitations/domains, SSO connections, SCIM
  directories, JWT templates, webhooks with dead-letter replay, audit events,
  billing, instance branding — plus the public OIDC surface (discovery, JWKS,
  authorization-code exchange with PKCE, refresh and client-credentials grants,
  introspection, revocation, userinfo). `TerraScaleAuthWebhookVerifier`
  authenticates inbound ts-auth webhook deliveries (HMAC-SHA256 over
  `timestamp + "." + body`) in constant time. Management responses mix
  snake_case and camelCase per family; every wire name is pinned explicitly.
  Safe reads retry per options; mutations carry no idempotency contract and are
  sent exactly once. One-time secrets (API keys, OAuth client secrets, SCIM
  tokens — the SCIM token also by its rotating call — are returned only by
their creating (or rotating) call.
- Named clients for document CRUD, collections, databases, database branches,
  regions, backups, files, SQL, reactive views, operations, auth persistence,
  authorization leases, and — under `Management` — usage, schemas, indexes,
  webhooks, development operations, durable exports, and imports. Provisioning
  creation distinguishes immediate HTTP 201 records from HTTP 202 accepted
  operations; use `Operations.GetAsync` to observe authoritative progress.
- `Documents.QueryAsync` runs bounded predicate queries
  (`POST .../documents/query`) with typed predicates (`equal`, `notEqual`,
  `exists`, ranges, `contains`, `startsWith`), optional projection paths,
  paging through `NextAfter`, and the server-reported access plan and work
  measurement on the result. `Documents.ExplainAsync` sends the same query
  with `explain: true` and returns the planner's decision — the selected
  candidate with its access method, freshness, and cost, plus every rejected
  candidate and its rejection reason — without reading any documents.
- `TerraScaleSyncV1ViewEngine`: the transport-independent result-view state
  machine (bounded snapshots, atomic deltas, opaque checkpoints, typed resets,
  reversible optimistic presentation). `TerraScaleReactiveHttpClient` is the
  public reactive binding and supplies MessagePack snapshot bootstrap, HTTP log
  delivery, WebSocket delivery, and Server-Sent Events delivery.
- `TerraScale.EntityFrameworkCore`: a separate package that maps EF Core
  entity types onto TerraScale documents (`UseTerraScaleSource`,
  `AsTerraScaleSource`, `TerraScaleSet<TEntity>`, and
  `AddTerraScaleEntityFrameworkCore<TContext>`).

Retired product concepts are deleted per the workspace decisions, not
deprecated: this SDK documents only the current public SDK surface above.

## Create a client

```csharp
using TerraScale.DotNet;

var client = new TerraScaleClient(
    new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY")
    });

var databases = await client.Databases.ListAsync(cancellationToken: cancellationToken);
```

For ts-auth, use the same package with `TerraScaleAuthClient`. With the public
`auth.terrascale.tech` origin, the SDK discovers the nearest healthy regional
auth endpoint from Atlas and sends requests there directly. Set
`UseReverseProxy = true` to send them through the public auth proxy instead.
Custom auth origins are used directly. The key is the instance `sk_...`
management key:

```csharp
var auth = new TerraScaleAuthClient(
    httpClient,
    new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.terrascale.tech"),
        ApiKey = Environment.GetEnvironmentVariable("TS_AUTH_API_KEY"),
        InstanceSlug = "acme"
    });

var user = await auth.Users.CreateAsync(email: "user@example.com");
var token = await auth.Oidc.RequestClientCredentialsTokenAsync(clientId, clientSecret);
```

Caller-supplied `HttpClient` handlers must set `AllowAutoRedirect = false`.
The SDK cannot inspect or reconfigure an existing handler; dependency-injection
registration supplies a handler with redirects disabled. Ordinary HTTP redirects
are errors; regional routing uses the explicit 421 repair contract.

`BearerToken` or `BearerTokenProvider` replaces `ApiKey` for token
authentication; the provider is called before each request and retry.

Document and SQL values encode native `byte[]` as standard padded base64 and
`DateTime`/`DateTimeOffset` as integer Unix milliseconds. A `DateTime` must have
UTC or local kind; unspecified time zones are rejected. `Documents.CreateAsync`
uses the server's atomic create-only precondition. For conditional PUT, set
`TerraScaleDocumentMutationOptions.CreateOnly`; it cannot be combined with
`ExpectedChangeToken`.

Document mutations retry only with a stable idempotency key, supplied through
`TerraScaleDocumentMutationOptions.IdempotencyKey` or `IdempotencyKeyFactory`.
The per-operation key takes precedence over the factory. A lost response or
cancellation after transmission does not prove a write failed; retain the same
key when resolving or retrying that operation. Unkeyed document PUTs are sent
once. Disposing a reactive subscription cancels active and queued reads and
waits for active transport cleanup.

Configure `TenantId` and `ProjectId` together for project-scoped operations.
These are selectors verified by server-side authority, not authorization grants.
Webhook create and rotation return a one-time signing secret; list responses do
not contain that secret. Pass a stable create idempotency key when enabling
retries; rotation and replay are not automatically retried.

## Verification

Materialize the pinned contract bundle first; `CONTRACT_URL` (or
`CONTRACT_SOURCE`) must be set explicitly:

```bash
CONTRACT_ARCHIVE_SHA256="$(tr -d '[:space:]' < CONTRACT_ARCHIVE_SHA256)" \
  dotnet run scripts/contracts.cs --version "$(tr -d '[:space:]' < CONTRACT_VERSION)"
dotnet build TerraScale.DotNet.slnx --configuration Release
dotnet format style TerraScale.DotNet.slnx --verify-no-changes --no-restore
dotnet run --project tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj \
  --framework net11.0 --configuration Release --no-build
dotnet run --project tests/TerraScale.EntityFrameworkCore.Tests/TerraScale.EntityFrameworkCore.Tests.csproj \
  --framework net11.0 --configuration Release --no-build
```

CI runs both test projects for `net10.0` and `net11.0` and fails closed on
vulnerable NuGet packages (`dotnet run scripts/check-nuget-vulnerabilities.cs`).
Tests consume the producer OpenAPI, HTTP error corpus, reactive
snapshot/delta/reset/timeout fixtures, and hostile MessagePack vectors. After a
Release build, `python3 scripts/check_package.py` packs the SDK and restores
and runs a separate NuGet consumer with an isolated package directory and no
SDK project reference.

The installed `contracts/` directory is disposable input and is not tracked.
Tests intentionally fail when their producer fixtures are missing. Local
contract checks do not qualify immutable publication, real-service behavior,
or complete four-SDK semantic conformance.

## Tests
- We MUST use TUnit for tests
- Prefer using `dotnet run --project {project}` instead of `dotnet test` as using dotnet run for TUnit is faster
