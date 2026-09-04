# TerraScale.DotNet

The C#/.NET 10 client SDK for TerraScale. The workspace
[`../GOAL.md`](../GOAL.md) is the single authority for the product; this
repository's [`GOAL.md`](GOAL.md) points to it. The SDK is generated from the
pinned contract that terrabase publishes (D-13/D-33) and rebuilt by product
item P-2b; what is checked in today is the surviving hand-written core from
before that regeneration.

## What is here

- `TerraScaleClient` over a caller-owned `HttpClient`: authentication (API key
  or bearer token/provider), region pinning with 421 repair, bounded transient
  retries, idempotency and request IDs, typed `TerraScaleApiException`
  errors, and dependency-injection registration.
- Named clients for document CRUD, collections, databases, database branches,
  regions, backups, tenant lifecycle, SQL, and reactive HTTP views.
- `TerraScaleSyncV2ViewEngine`: the transport-independent result-view state
  machine (bounded snapshots, atomic deltas, opaque checkpoints, typed resets,
  reversible optimistic presentation) that awaits the pinned reactive contract
  (ADR 0002). No transport, route, or wire protocol ships here yet.

Retired product concepts are deleted per the workspace decisions, not
deprecated: this SDK ships only the launch surface above.

## Create a client

```csharp
using TerraScale.DotNet;

var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY")
    });

var databases = await client.Databases.ListAsync(cancellationToken);
```

`BearerToken` or `BearerTokenProvider` replaces `ApiKey` for token
authentication; the provider is called before each request and retry.

## Verification

```bash
dotnet build TerraScale.DotNet.slnx --configuration Release
dotnet test TerraScale.DotNet.slnx --configuration Release --no-build --no-progress
```

`dotnet format TerraScale.DotNet.slnx --verify-no-changes --no-restore` keeps
the tree style-clean. CI is the repository-wide D-68 five-line `ts-deploy`
workflow, added by a coordinated wave across all repositories.
