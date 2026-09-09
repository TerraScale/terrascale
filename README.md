# TerraScale.DotNet

The C#/.NET 10 client SDK for TerraScale. The workspace
[goal](../GOAL.md) defines the product and SDK responsibilities. Generated
models and route metadata consume the pinned public contract published by
terrabase; named clients implement customer operations against that contract.

## What is here

- `TerraScaleClient` over a caller-owned `HttpClient`: authentication (API key
  or bearer token/provider), region pinning with 421 repair, bounded transient
  retries, idempotency and request IDs, typed `TerraScaleApiException`
  errors, and dependency-injection registration.
- Named clients for document CRUD, collections, databases, database branches,
  regions, backups, SQL, reactive views, usage, schemas, indexes, webhooks,
  development operations, and durable exports. Provisioning creation distinguishes
  immediate HTTP 201 records from HTTP 202 accepted operations; use
  `Operations.GetAsync` to observe authoritative progress.
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
# Published immutable bundle:
make check CONTRACT_URL="$CONTRACT_URL" CONTRACT_ARCHIVE_SHA256="$CONTRACT_ARCHIVE_SHA256"

# Explicit local producer input for development:
make check CONTRACT_SOURCE=../terrabase/contracts
```

`make check` verifies and installs the pinned artifacts, checks generated sources
and formatting, builds, and runs tests. Tests consume the producer OpenAPI,
HTTP error corpus, reactive snapshot/delta/reset/timeout fixtures, and hostile
MessagePack vectors. The package check then restores and runs a separate NuGet
consumer with an isolated package directory and no SDK project reference.

The installed `contracts/` directory is disposable input and is not tracked.
Tests intentionally fail when their producer fixtures are missing. Local
contract checks do not qualify immutable publication, real-service behavior,
or complete four-SDK semantic conformance.
