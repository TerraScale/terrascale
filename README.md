# TerraScale.DotNet

The C#/.NET 11 client SDK for TerraScale. The workspace
[goal](../GOAL.md) defines the product and SDK responsibilities. Generated
models and route metadata consume the pinned public contract published by
terrabase; named clients implement customer operations against that contract.

## Atlas routing (ADR 0050)

`api.terrascale.tech` is the dashboard-owned product API origin only; the
per-request proxy is retired. Metadata lives on `atlas.terrascale.tech`
(ts-atlas), and every database operation connects directly to a regional
member:

- **Discovery** — the first database-scoped request resolves its routing scope
  (`GET https://atlas.terrascale.tech/api/v1/discovery?database=…&branch=main`).
  The document is strictly validated (region codes, HTTPS
  `{region}.api.terrascale.tech` endpoints — loopback dev origins for local
  workers — capabilities, priority), cached per scope for the served
  `refreshSeconds` clamped to 60–3600 s, fetched single-flight, and pinned
  regionally while eligible. Refreshes never re-pin a healthy route; a `421`
  adopts the member's authoritative `X-TerraScale-Placement` (repair once,
  safe or idempotency-keyed requests only) and schedules one corrective
  rediscovery (`stale=1`). `404` never rediscovers.
- **Catalog fallback** — a discovery failure degrades to the region catalog
  (`GET https://atlas.terrascale.tech/api/v1/regions`, cached five minutes)
  and the first available regional host. There is no proxy fallback; member
  data never targets the bare product origin.
- **Route table** — database-scoped requests (`/api/v1/databases/{name}/…`)
  use discovery; product routes (`/api/v1/organizations*`,
  `/api/v1/projects*`, `/api/v1/regions/{code}`) target the configured
  `BaseAddress`; `/api/v1/regions` exactly is the catalog on Atlas; every
  other database-less member route (database create/list, operations, health)
  uses catalog selection.
- **Reactive** — view attach targets the discovered regional endpoint, with
  the same catalog fallback; advertised snapshot/log URL canonicalization is
  unchanged.

Routing is active for the public product origin and any client configured
with `DiscoveryBaseAddress`; custom origins, regional hosts, strict affinity,
and the loopback emulator keep direct routing exactly as before. Set
`DiscoveryBaseAddress` (and optionally `DiscoveryTimeout`, default 3 s) in
`TerraScaleClientOptions` for tests and local development.

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
  regions, backups, SQL, reactive views, usage, schemas, indexes, webhooks,
  development operations, and durable exports. Provisioning creation distinguishes
  immediate HTTP 201 records from HTTP 202 accepted operations; use
  `Operations.GetAsync` to observe authoritative progress.
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
  delivery, and WebSocket delivery.

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

```bash
dotnet build TerraScale.DotNet.slnx --configuration Release
dotnet test TerraScale.DotNet.slnx --configuration Release --no-build
```

The solution build and test verify the checked-in generated sources and
formatting. Tests consume the producer OpenAPI,
HTTP error corpus, reactive snapshot/delta/reset/timeout fixtures, and hostile
MessagePack vectors. The package check then restores and runs a separate NuGet
consumer with an isolated package directory and no SDK project reference.

The installed `contracts/` directory is disposable input and is not tracked.
Tests intentionally fail when their producer fixtures are missing. Local
contract checks do not qualify immutable publication, real-service behavior,
or complete four-SDK semantic conformance.

## Tests
- We MUST use TUnit for tests
- Prefer using `dotnet run --project {project}` instead of `dotnet test` as using dotnet run for TUnit is faster
