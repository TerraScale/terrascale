# TerraScale.DotNet

Official .NET 10 SDK for TerraScale's versioned public APIs.

The workspace `../GOAL.md` defines target architecture. An accepted target is
not a released SDK capability.

## Service boundary

The SDK uses public TerraScale HTTPS and reactive contracts:

- Dashboard owns Better Auth, Organizations, Projects, Users, Payments, HTTP
  MCP, and those public product routes.
- The independent C#/.NET 10 `TerraScale.Database` service owns database,
  query, placement, backup, usage, and reactive routes.
- TerraBaseV2 is private. The SDK never speaks TBEP, connects to a Member,
  consumes native replication data, or uses NATS.

One customer API origin may route typed paths to both services without merging
their ownership.

## Packages

~~~bash
dotnet add package TerraScale.DotNet
dotnet add package TerraScale.EntityFrameworkCore
~~~

`TerraScale.EntityFrameworkCore` is a separate package that uses the public
SDK. It does not reference TerraBase or server projects.

## Configure

Use a Project API key:

~~~csharp
using TerraScale.DotNet;

var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ProjectId = "01J00000000000000000000000",
        ApiKey = "<project-api-key>"
    });
~~~

Or provide an OAuth bearer token:

~~~csharp
var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        BearerTokenProvider = cancellationToken =>
            tokenCache.GetAccessTokenAsync(cancellationToken)
    });
~~~

The current default origin is `https://api.terrascale.tech`. Final
single-origin edge routing awaits owner confirmation.

The SDK never logs credentials or owns token persistence. Token acquisition and
refresh remain in the calling application.

## Idempotent mutations

Configure `TerraScaleClientOptions.IdempotencyKeyFactory` or pass an
operation-specific key. One logical mutation reuses the same key for every
retry.

Keys are nonblank and at most 128 characters. Reusing a key with different
input is a typed conflict. Replay retention and response headers follow the
owning public contract.

Unsafe mutations do not retry. Validation, authentication, authorization, and
deterministic conflict failures are not automatic retry candidates.

## Region and durability model

A database selects one through twenty active Regions, initially GRU, SFO, and
FRA. Every selected Region stores every logical Partition and accepts writes.

- `RegionalDurable` is the no-WAN default and requires both local RF=2
  Members.
- `GlobalDurable` pays WAN latency and requires every selected active Region.
- Backup is an independent optional read-only copy.

A preferred Region is a failover-capable hint. Strict affinity fails instead
of silently routing elsewhere.

Automatic session consistency retains opaque causal tokens for eligible reads.
The SDK does not expose replica identities, DVVs, physical Members, or raw
Partition state.

## Honest sync status

This repository still contains a Loro-era `terrascale.sync.v2` implementation
and corpus. It uses collection sessions, host-local context, opaque Loro
deltas, and server-ack-only presentation. It is historical implementation
evidence and does not implement the accepted replacement.

The replacement .NET reactive client will:

- subscribe to bounded typed TQL queries;
- receive authorized result snapshots, insert, update, patch, remove, move, or
  reset;
- use opaque view-scoped checkpoints;
- apply reversible optimistic presentation for connected online writes;
- reject durable writes while disconnected;
- reauthorize on grant refresh and reconnect;
- remove results after permission loss;
- recover across Region movement through sparse causal tokens;
- bound every queue, buffer, result, and reconnect path; and
- pass one semantic corpus shared with Rust and TypeScript.

No old Loro byte vector qualifies the replacement. Until the generated schema
and semantic corpus pass, current v2 conformance remains unqualified.

## Public API coverage

The SDK exposes only named operations present in the reviewed public contract.
Current generated descriptors and tests, not this README, are the authority for
the exact released operation list.

Public families may include:

- identity and session operations;
- Organizations, Projects, membership, and credentials;
- database lifecycle, placement, environment, repository, document, TQL, and
  schema operations;
- backup, restore, PITR, export, and import;
- usage, billing, notifications, and queue operations;
- reactive subscriptions; and
- role-protected administration.

Dashboard owns identity and product route implementations.
`TerraScale.Database` owns database and reactive implementations. The SDK
does not call either runtime's private routes.

Payment checkout, subscriptions, portal, and supported lifecycle follow the
official Better Auth Stripe plugin. Polar and provider-neutral duplicate
payment state are retired targets.

## Database and environment safety

Customers create native databases through public Database API operations. A
small database begins with one logical Partition. The API does not ask
customers to choose physical shard count or Members.

Environments, when enabled, are native engine-enforced scopes. They are not
collection-name prefixes inside one shared RavenDB database.

The SDK sends public ULID identities and placement choices. It never constructs
physical storage names.

## Errors

`TerraScaleApiException` preserves the safe public status, error code,
request identity, retry guidance, and structured details. It does not expose
raw remote bodies, stack traces, credentials, private endpoints, or storage
paths.

Partial mutation outcomes preserve the idempotency identity and enough
documented status to resolve a retry without guessing whether durable work
occurred.

## Development

~~~bash
dotnet build -m
dotnet run --project tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj --configuration Release
dotnet run --project tests/TerraScale.EntityFrameworkCore.Tests/TerraScale.EntityFrameworkCore.Tests.csproj --configuration Release
~~~

Use the exact project paths present in the checkout if they differ from these
examples.

Live tests target fixed DevHost public routes and skip when required endpoints
or credentials are absent. They do not substitute mocks or direct server
references. The new Database API needs a fixed DevHost route before its live
tests can become current.

## Release qualification

One immutable package archive is built, inspected, installed into clean .NET
consumers, and bound to its source revision and digest. Compilation does not
replace live customer, tenant-isolation, contract, reactive conformance, or
publication evidence.

The Orchestrator-owned `develop` to `main` promotion path publishes only the
exact admitted package.
