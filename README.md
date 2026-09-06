# TerraScale.DotNet

The C#/.NET 10 client SDK for TerraScale. The workspace
[workspace goal](../GOAL.md#ts-dotnet) defines the product and SDK responsibilities. Generated models and route metadata consume the pinned contract published by terrabase; named clients implement customer operations against that contract.

## What is here

- `TerraScaleClient` over a caller-owned `HttpClient`: authentication (API key
  or bearer token/provider), region pinning with 421 repair, bounded transient
  retries, idempotency and request IDs, typed `TerraScaleApiException`
  errors, and dependency-injection registration.
- Named clients for document CRUD, collections, databases, database branches,
  regions, backups, tenant lifecycle, SQL, and reactive HTTP views.
- `TerraScaleSyncV1ViewEngine`: the transport-independent result-view state
  machine (bounded snapshots, atomic deltas, opaque checkpoints, typed resets,
  reversible optimistic presentation). `TerraScaleReactiveHttpClient` supplies
  MessagePack snapshot bootstrap, HTTP log delivery, and WebSocket delivery.

Retired product concepts are deleted per the workspace decisions, not
deprecated: this SDK ships only the launch surface above.

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

var databases = await client.Databases.ListAsync(cancellationToken);
```

Caller-supplied `HttpClient` handlers must set `AllowAutoRedirect = false`.
The SDK cannot inspect or reconfigure an existing handler; dependency-injection
registration supplies a handler with redirects disabled. Ordinary HTTP redirects
are errors; regional routing uses the explicit 421 repair contract.

`BearerToken` or `BearerTokenProvider` replaces `ApiKey` for token
authentication; the provider is called before each request and retry.

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
