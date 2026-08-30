# TerraScale.DotNet

`TerraScale.DotNet` is the official .NET 10 client for TerraScale's named,
versioned public API operations. `TerraScale.EntityFrameworkCore` is a
separate source integration package built on the client.

The checkout is a development candidate. The package projects currently
declare version `0.1.0`; this README does not claim that version is ready for
customer production. See [GOAL.md](GOAL.md) for the qualification plan and
known blockers.

## Scope

The repository contains:

- the `TerraScaleClient` HTTP client and named clients for project, database,
  repository, document, query, schema, change-feed, queue, billing, usage,
  access, credential, notification, administration, backup, and restore
  operations;
- `TerraScaleTypedRepository<TEntity>` for generic typed document operations;
- the `TerraScale.EntityFrameworkCore` source integration;
- the current WebSocket `TerraScaleSyncClient` and its resumable live-query
  session; and
- the `terrascale.sync.v2` codec, checkpoint session, and transport-independent
  result-view engine.

The customer-facing client calls only versioned public HTTP, WebSocket, and SSE
contracts. It does not open engine sessions, use a private wire protocol,
connect to storage members, or use a broker as a data path. The separate v2
checkpoint exercise is explicitly documented as not customer-ready. A public
contract must exist before a new named operation or transport binding is added
here.

## Support and packages

The projects target `net10.0`. The repository pins SDK `10.0.400` in
`global.json`, uses nullable reference types and warnings as errors, and
generates XML documentation for both package projects.

Install the packages from the feed selected by your release process:

```bash
dotnet add package TerraScale.DotNet
dotnet add package TerraScale.EntityFrameworkCore
```

The package metadata currently declares `0.1.0`. Check the admitted release
manifest for the version and digest that a production consumer may install.

## Create a client

`HttpClient` remains caller-owned. Select one authentication mode. A project
API key requires `ProjectId`; a static bearer token and a
`BearerTokenProvider` are mutually exclusive. The provider is called before
each request and retry, so token acquisition and refresh stay in the caller.

```csharp
using TerraScale.DotNet;

var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ProjectId = "01J00000000000000000000000",
        ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY")
    });

var databases = await client.Databases.ListAsync(cancellationToken);
```

For an OAuth or machine-to-machine token, replace `ApiKey` and `ProjectId`
with `BearerToken` or `BearerTokenProvider`:

```csharp
var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        BearerTokenProvider = tokenCache.GetAccessTokenAsync
    });
```

`TerraScaleClientOptions` also supports a preferred region, failover or strict
regional affinity, optional automatic session-consistency tokens, bounded
transient retries, an idempotency-key factory, and a request-ID factory. Strict
regional affinity requires `PreferredRegion`. The client validates conflicting
authentication, missing project/database context, retry limits, and regional
configuration at construction.

## Named API clients

The root client exposes these named properties. Their exact request and model
contracts are the public source in `src/TerraScale.DotNet` and the focused
tests in `tests/TerraScale.DotNet.Tests`.

| Property | Current scope |
| --- | --- |
| `Databases`, `ProjectDatabases`, `Collections` | Logical database, project-database, and collection reads and lifecycle |
| `Repositories`, `Documents`, `TypedRepository<TEntity>` | Repository lifecycle plus untyped and typed document CRUD, patch, batch, list, and query |
| `Tql`, `Schemas`, `Changes` | TQL queries, schema configuration/validation, and repository/TQL change streams |
| `Projects`, `Access`, `Regions`, `Environments` | Project, team-member, region-catalog, and database-environment operations |
| `SavedQueries` | Owner-published saved-query list, create, get, update, and delete operations |
| `Queues` | Project queue/topic lifecycle, publish, receive, acknowledgement, lock, purge, and dead-letter operations |
| `ProjectUsage`, `Billing` | Project/organization usage, invoices, pricing, access, checkout, and portal operations |
| `ApiKeys`, `M2MApplications` | Project API-key and machine-to-machine application lifecycle |
| `Notifications`, `Administration` | User notifications and role-protected platform administration |
| `Backups` | Backup policy, plan/execute, jobs, restore points, and restores |
| `Sync` | Explicit resumable WebSocket live-query sessions |

`ForDatabase(databaseId)` and `ForEnvironment(databaseId, environmentId)`
return a new client context over the same caller-owned transport. They set
defaults for repository operations; they do not create a database or change
server-side authorization.

## Typed repositories

The typed facade materializes generic entities using its configured JSON
options. The untyped methods return `JsonElement`-backed models. Typed Sync
live queries take a caller-supplied `JsonTypeInfo<TEntity>`; use a
source-generated context for those queries.

```csharp
var orders = client.Repository<Order>("orders");
var created = await orders.CreateAsync(
    new Order("ORD-100", "new"),
    cancellationToken);

var page = await orders.QueryAsync(
    TerraScaleQuery.From("orders").OrderBy("number").Take(20),
    cancellationToken);

public sealed record Order(string Number, string Status);
```

Mutating methods accept an operation-specific idempotency key where the public
operation requires one. Automatic retries are limited to documented transient
responses and never apply to validation, authentication, authorization, or
deterministic conflict failures. If a mutation has a partial outcome, inspect
`TerraScaleApiException` and the returned operation metadata before retrying.

## Entity Framework Core integration

`TerraScale.EntityFrameworkCore` is not a database provider. It adds source
metadata to an ordinary `DbContext` and sends entity operations through the
public SDK. It supports asynchronous, cancellation-aware add, upsert, get,
find, list, query, delete, and source save operations.

```csharp
services.AddTerraScaleEntityFrameworkCore<OrdersContext>(options =>
{
    options.BaseAddress = new Uri("https://api.terrascale.tech");
    options.ProjectId = Environment.GetEnvironmentVariable("TERRASCALE_PROJECT_ID");
    options.ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY");
});

services.AddDbContext<OrdersContext>(options =>
    options.UseTerraScaleSource(source =>
        source.RepositoryNameResolver = entityType => entityType.ClrType.Name));
```

The EF Core integration remains in `src/TerraScale.EntityFrameworkCore`,
references only the SDK and EF Core, and has a separate real-HTTP test project.

## Sync status

There are three distinct sync-related surfaces in the checkout:

1. `TerraScaleSyncClient` opens the current WebSocket session, registers
   bounded TQL live queries, executes idempotent mutations, applies optional
   reversible optimistic presentation, reconnects with bounded backoff, and
   exposes non-sensitive connection diagnostics. `StartAsync` begins the
   session; disposing the session releases the network resources.
2. `TerraScaleSyncV2Client` and `TerraScaleSyncV2Session` implement the
   checked-in v2 frame/session exercise over HTTP and SSE. The session supports
   setup/resume, checkpoint-ordered delta pulls, snapshot resync, checkpoint
   acknowledgement, content-free trigger handling, bounded stream reconnects,
   and server-ack-only mutations. Its options require a backend context and
   absolute transport base address, so this surface is not a customer-ready
   public contract merely because it is public in the assembly.
3. `TerraScaleSyncV2ViewEngine` and `TerraScaleSyncV2ViewModels` implement the
   result-view state machine without transport or routes. They cover bounded
   snapshots, atomic result deltas, opaque checkpoints, authorization generation
   changes, divergence, and reversible optimistic presentation. A transport
   binding for this engine is still a separate work item.

The workspace goal treats the existing v2 session and corpus as migration
evidence. The accepted replacement requires authorized bounded TQL result
views, view-scoped checkpoints, permission-driven removals, Region movement,
backpressure, and one semantic corpus shared with the other official clients.
Do not infer replacement readiness from the existing codec or from passing
loopback tests. See [the sync implementation record](docs/sync/terrascale-sync-v2-client.md).

## Errors and resource rules

`TerraScaleApiException` preserves the safe HTTP status, public error code,
request ID, retry guidance, and bounded response details. It does not expose
credentials, private endpoints, stack traces, or storage paths. All public
operations are asynchronous and cancellation-aware. The SDK never disposes a
caller-owned `HttpClient`, blocks on async work, or starts hidden background
work outside an owned sync session.

## Verification

Run the repository checks with the pinned .NET SDK:

```bash
dotnet build TerraScale.DotNet.slnx --configuration Release
dotnet test TerraScale.DotNet.slnx --configuration Release --no-build --no-progress
```

The default test run covers serialization, route construction, validation,
retry behavior, API error handling, sync state and transport fixtures, v2
golden vectors, workflow policy checks, and package-consumer fixtures. Live
customer/API tests are environment-gated. Missing configuration causes an
explicit skip, not a substitute mock or fake service. The expected variables
and opt-in lifecycle flags are listed in
`tests/TerraScale.DotNet.Tests/TerraScaleApiE2E.cs`.

The migration-only v2 corpus is copied into test output from the two corpus
directories under `tests/TerraScale.DotNet.Tests/vectors/`. The TerraBase-owned
`corpus-manifest@2` pins 11 unique corpus/file pairs: nine base vectors plus
the ADR-0024 `authorization-reset` and `slow-consumer-overload` vectors. The
integrity gate verifies the owner manifest digest, each file's size and
SHA-256 digest, and the exact category set before any conformance step runs.
These vectors remain migration evidence, not qualification of the replacement
public result-view transport.

## Release qualification

The package workflow has two separate paths:

- pull requests run an untrusted, secretless build and test validation;
- the protected delivery workflow consumes one immutable v2 package-set
  handoff for `TerraScale.DotNet` and `TerraScale.EntityFrameworkCore`, verifies
  both SHA-256 digests against one source revision/tree, inspects both
  archives, installs the EF package and its exact SDK dependency into a clean
  consumer, and emits one candidate-bound verification receipt.

`scripts/verify-package-set-candidate.py` rejects dirty no-argument packs,
missing or substituted package-set members, symlinked or malformed packages,
unexpected archive content, unpublished API versions, dependency drift,
metadata that does not match the source revision, and handoffs that do not
match `origin/develop`. Its clean
consumer proves that NuGet restored the exact two supplied archives, builds
with warnings as errors, audits the complete dependency closure from low
severity upward, and records the handoff digest plus the accepted MIT license
disposition. The older
single-SDK verifier remains available for focused compatibility regression,
but it is not the protected package-set release gate.

The protected Deploy build target `dotnet-package-set-v2` invokes the same
verifier with `--emit-candidate` and produces exactly three transferable files:
the v2 handoff manifest and the two sibling nupkgs. Candidate-bound release
profiles likewise publish TUnit apphosts first; Deploy executes those exact
bytes, requires at least 225 base-SDK and 10 EF Core tests, and rejects any
skipped release test.

This proves package-set provenance, archive safety, dependency closure, and
clean installation. It does not prove live API compatibility, tenant
isolation, reactive cross-client conformance, publication, or the production
gates in [GOAL.md](GOAL.md).

The package-set regression wrapper is
`tests/TerraScale.DotNet.Tests/verify-package-set-candidate-tests.sh`. It covers
digest and package substitution, symlinks, dependency spoofing, secrets,
unpublished API versions, duplicate archive entries, receipt completeness, and
dirty-source refusal.
