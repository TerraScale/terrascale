# TerraScale.DotNet

Official C# SDK for TerraScale Platform API families.

## Support policy

TerraScale.DotNet 0.1.x targets stable .NET 10 LTS and TerraScale public API `/api/v1`. Customers do not need a preview runtime, preview package feed, or runtime feature flag. A breaking API contract requires a new API version and an aligned SDK release.

The repository pins the .NET 10 SDK in `global.json`. Package references used by the public SDK and Entity Framework Core integration are stable 10.x releases.

## Idempotent mutations

Public clients opt in by configuring `TerraScaleClientOptions.IdempotencyKeyFactory`. The SDK creates one key per logical mutation, reuses it across retries, and never retries an unsafe mutation without a key. Keys must be nonblank and no longer than 128 characters. Replayable API results expire after 24 hours; replays include `Idempotency-Replayed: true`, while reuse with different input returns `409 idempotency_key_reused`.

The SDK calls verified TerraScale public endpoints. It covers region catalog, database, repository, document, schema, TQL, changes, billing, project, project database, physical database-branch capability and clone requests, environment, queue, usage, project auth API key, machine-to-machine application, notification, backup, restore-point, restore, v2 saved-query conditional-update, v3 saved-query snapshot-pagination, and role-protected platform administration operations. It does not call TerraBase storage directly or Dashboard-internal endpoints.

## Platform administration

Use `client.Administration` with a bearer token that carries the platform owner role. The client exposes only the published `/api/v1/admin` operations: platform usage, user lifecycle, support notes, platform-wide resource pages, pricing-plan management, audit, notifications, and payment-webhook inspection/replay. Webhook replay requires an explicit idempotency key and a fresh MFA-verified owner session. Authorization failures are returned as `TerraScaleApiException` with the public HTTP status and error code.

Administration E2E mutations are disabled unless their dedicated environment gates are set. User-state and delete-user fixtures must be different IDs. Pricing-plan updates restore the original name and visibility, but because interruption could leave the temporary update in place, the update E2E additionally requires `TERRASCALE_E2E_ADMIN_UPDATE_PLAN_CONFIRMED_DISPOSABLE=true` for its dedicated plan fixture. Support-note creation has no public list or delete contract, so its owner-success E2E test is intentionally irreversible and additionally requires `TERRASCALE_E2E_ENABLE_ADMIN_SUPPORT_NOTE_CREATE=true`, `TERRASCALE_E2E_ADMIN_SUPPORT_NOTE_CONFIRMED_ISOLATED_DISPOSABLE_ENVIRONMENT=true`, and a disposable `TERRASCALE_E2E_ADMIN_SUPPORT_NOTE_PROJECT_ID`; do not enable it outside an isolated disposable environment.

## Billing checkout

Create a checkout session with a provider-neutral `PlanId`, `BillingInterval` (`monthly` or `annual`), optional `Provider` (`polar` or `stripe`), and optional `Quantity`. `PriceId` remains available only for legacy integrations. `SuccessUrl`, `CancelUrl`, and `CustomerEmail` remain as obsolete compatibility members but are not sent: the checkout contract does not use them.

## Install

Use the project reference in this checkout, or install the package once it is published:

```bash
dotnet add package TerraScale.DotNet
```

The repo also contains the EF Core source integration package:

```bash
dotnet add package TerraScale.EntityFrameworkCore
```

Release qualification uses one immutable package archive. `scripts/verify-package-candidate.sh` refuses to pack a dirty checkout, checks its metadata and archive contents, requires the nuspec repository commit to equal the exact source revision, restores those exact bytes into a clean `net10.0` consumer outside this repository, builds the consumer, and prints the source revision and SHA-256 digest. Orchestrator can pass an existing candidate with `--package`, its expected `--sha256` digest, and (when validating a candidate from a different checkout) `--source-revision` so verification never rebuilds or silently substitutes a different archive.

The manual CI package-byte lane consumes the protected handoff path, digest, and source revision supplied by the release coordinator. It does not repack or publish a substitute. The runner must already have the immutable package staged; protected artifact transfer is an external release gate, and a missing or unresolved handoff fails closed.

Live customer-journey, tenant-isolation, intended-feed download, and publication evidence remain release admission checks. Source compilation and the local package-consumer build do not replace them. Compatibility with the Public Alpha API candidate is recorded by the release admission process once that candidate and its digest are available; this package does not claim live qualification on its own.

## Configure

Use an API key with a project id:

```csharp
using TerraScale.DotNet;

var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ProjectId = "01J00000000000000000000000",
        ApiKey = "<project-api-key>"
    });
```

Or use a caller-provided bearer token from OAuth or machine-to-machine auth:

```csharp
var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        BearerToken = "<access-token>"
    });
```

For expiring OAuth or machine-to-machine tokens, provide a token provider. The SDK invokes it before each HTTP request and retry, while token acquisition and refresh stay in your application boundary:

```csharp
var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        BearerTokenProvider = async cancellationToken =>
            await tokenCache.GetAccessTokenAsync(cancellationToken)
    });
```

You can also explicitly create an M2M token from the anonymous project M2M token endpoint when your application owns the client credentials. This bootstrap client does not need an API key or bearer token:

```csharp
var tokenBootstrapClient = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech")
    });

var token = await tokenBootstrapClient.M2MApplications.CreateTokenAsync(
    projectId,
    clientId,
    clientSecret,
    scope: "documents:read documents:write",
    cancellationToken);
```

The same token exchange is also available as OAuth form-encoded and HTTP Basic client-credential requests through `CreateTokenFormAsync` and `CreateTokenWithBasicCredentialsAsync`.

## Regional and session consistency

`TerraScaleClientOptions.BaseAddress` defaults to
`https://api.terrascale.tech`. A preferred region with `Failover` affinity
uses the global endpoint and sends the public routing hint. `Strict` affinity
uses the selected region's hostname and never silently falls back:

```csharp
var session = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        ProjectId = projectId,
        BearerToken = accessToken,
        PreferredRegion = "fra",
        RegionAffinity = TerraScaleRegionAffinity.Strict,
        SessionConsistency = TerraScaleSessionConsistency.Automatic
    });

var current = await session.Documents.GetAsync(repositoryId, documentId, cancellationToken);
var updated = await session.Documents.UpsertAsync(
    repositoryId,
    documentId,
    new { Status = "approved" },
    new TerraScaleDocumentMutationOptions(current.ChangeToken),
    cancellationToken);
```

The automatic session mode keeps each document's latest opaque write token inside the
client instance and sends it on later reads of that document. Pass
`new TerraScaleDocumentReadOptions(AllowStale: true)` to opt one read out of
the server wait. A mismatched `ExpectedChangeToken` throws a typed
`TerraScaleApiException` with HTTP 409 and the public error code. Document
results and failures expose only the public routing observation. A 503
`project_region_unavailable` failure sets `IsRegionalUnavailable`.

## Database And Environment Selection

Create, inspect, and clean up logical databases through the public database API:

```csharp
var database = await client.Databases.CreateAsync("orders-production", cancellationToken: cancellationToken);
var loadedDatabase = await client.Databases.GetAsync(database.DatabaseId, cancellationToken);
var collections = await client.Collections.ListAsync(loadedDatabase.DatabaseId, cancellationToken);

// Later, after repositories/data are no longer needed:
await client.Databases.DeleteAsync(
    loadedDatabase.DatabaseId,
    new TerraScaleDeleteDatabaseOptions(loadedDatabase.DatabaseId),
    cancellationToken);
```

Use project-scoped projects, databases, and environments when working through the live project metadata API:

```csharp
var regions = await client.Regions.ListAsync(cancellationToken);

var createdProject = await client.Projects.CreateAsync(
    organizationId,
    "orders",
    defaultPermissionKeys: ["auth:login"],
    regions:
    [
        TerraScaleRegions.Gru,
        TerraScaleRegions.Sfo,
        TerraScaleRegions.Fra
    ],
    cancellationToken);
var updatedProject = await client.Projects.UpdateAsync(
    createdProject.ProjectId,
    "orders-production",
    cancellationToken);
var project = await client.Projects.GetAsync(projectId, cancellationToken);

var database = await client.ProjectDatabases.CreateAsync(
    project.ProjectId,
    "orders-production",
    cancellationToken);

var environment = await client.Environments.CreateAsync(
    project.ProjectId,
    database.DatabaseId,
    "Preview",
    kind: "Preview",
    cancellationToken);

var usage = await client.ProjectUsage.GetAsync(project.ProjectId, cancellationToken);
var workload = await client.ProjectUsage.GetWorkloadAsync(
    project.ProjectId,
    range: "7d",
    cancellationToken);
var environments = await client.Environments.ListAsync(
    project.ProjectId,
    database.DatabaseId,
    cancellationToken: cancellationToken);
var loadedEnvironment = await client.Environments.GetAsync(
    project.ProjectId,
    database.DatabaseId,
    environment.EnvironmentId,
    cancellationToken);

var deletion = await client.Environments.DeleteAsync(
    project.ProjectId,
    database.DatabaseId,
    environment.EnvironmentId,
    cancellationToken);

// Deletion is durable: completion requires both values below.
var deletionCompleted = deletion.Deleted && deletion.Status == "Completed";
// "RetryScheduled" means it remains pending; RetryAt is the server's retry hint.
```

Physical database branches are a separate capability from logical environments. Inspect capability before requesting a clone; Platform currently fails clone requests closed with `TerraScaleApiException` (`503`, `TERRASCALE-5001`) until physical lifecycle provisioning is available:

```csharp
var branchCapability = await client.DatabaseBranches.GetCapabilityAsync(project.ProjectId, cancellationToken);

try
{
    await client.DatabaseBranches.RequestCloneAsync(
        project.ProjectId,
        new TerraScaleCreateDatabaseBranchRequest(database.DatabaseId),
        cancellationToken);
}
catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
{
    // No physical clone has been created; use the capability response and retry guidance.
}
```

Select a logical database and environment when creating repositories:

```csharp
var environmentClient = client.ForEnvironment(databaseId, environmentId);

var repository = await environmentClient.Repositories.CreateAsync(
    name: "orders",
    collection: "orders",
    schemaValidationEnabled: true,
    schema: """
    {
      "type": "object",
      "required": ["orderNumber", "status"],
      "properties": {
        "orderNumber": { "type": "string" },
        "status": { "type": "string" }
      }
    }
    """,
    cancellationToken: cancellationToken);
```

## Queues And Topics

Create a project queue and publish JSON messages through the public project API:

```csharp
var queue = await client.Queues.CreateAsync(
    projectId,
    new TerraScaleCreateProjectQueueRequest(
        "order-processing",
        TerraScaleQueueKinds.Queue,
        RetentionSeconds: 86_400,
        PartitionCount: 8,
        DuplicateDetectionSeconds: 120),
    cancellationToken);

var published = await client.Queues.PublishAsync(
    projectId,
    queue.QueueId,
    new TerraScalePublishProjectQueueMessageRequest<object>(
        new { orderId = "A1001", action = "capture-payment" },
        IdempotencyKey: "order-A1001-capture",
        PartitionKey: "A1001",
        TimeToLiveSeconds: 300),
    cancellationToken);

var pending = await client.Queues.PeekAsync(
    projectId,
    queue.QueueId,
    maxMessages: 10,
    cancellationToken);

var delivery = (await client.Queues.ReceiveAsync(
    projectId,
    queue.QueueId,
    new TerraScaleReceiveProjectQueueMessagesRequest(
        "payment-workers",
        MaxMessages: 1,
        WaitSeconds: 10,
        LockSeconds: 60),
    cancellationToken)).Single();

// Persist DeliveryReceipt with the work item when processing crosses process boundaries.
// It is opaque, time-limited, region-affine, and safe to retry for acknowledgements.
await client.Queues.CompleteAsync(
    projectId,
    queue.QueueId,
    delivery.DeliveryReceipt,
    cancellationToken);
```

Use `TerraScaleQueueKinds.Topic` when creating a publish/subscribe topic. Queue delivery is explicitly at-least-once; handlers should be idempotent. `RenewLockAsync`, `AbandonAsync`, and `DeadLetterAsync` accept the same regional receipt. Queue deletion and purge are available through `DeleteAsync` and `PurgeAsync`.

## Documents, Query, Batch, Schema, And Changes

```csharp
var created = await client.Documents.CreateAsync(
    repository.RepositoryId,
    new { orderNumber = "A1001", status = "new" },
    cancellationToken);

var patched = await client.Documents.PatchAsync(
    repository.RepositoryId,
    created.Id,
    new { status = "paid" },
    cancellationToken);

var paidOrders = TerraScaleQuery
    .From("orders")
    .WhereEqual("status", "paid")
    .OrderBy("orderNumber")
    .Take(25);

var page = await client.Tql.QueryAsync(
    repository.RepositoryId,
    paidOrders,
    cancellationToken);

var typedOrders = client.Repository<OrderDocument>(repository.RepositoryId);
var typedOrder = await typedOrders.CreateAsync(
    new OrderDocument("A1003", "new"),
    cancellationToken);
var typedPage = await typedOrders.QueryAsync(
    TerraScaleQuery.From("orders").WhereEqual("status", "new").Take(25),
    cancellationToken);
var typedBatch = await typedOrders.BatchAsync(
    [
        TerraScaleTypedDocumentBatchOperation.Create(new OrderDocument("A1004", "new")),
        TerraScaleTypedDocumentBatchOperation.Patch(typedOrder.Id, new { status = "processing" })
    ],
    cancellationToken);

var batch = await client.Documents.BatchAsync(
    repository.RepositoryId,
    [
        TerraScaleDocumentBatchOperation.Create(new { orderNumber = "A1002", status = "new" }),
        TerraScaleDocumentBatchOperation.Patch(created.Id, new { status = "shipped" })
    ],
    cancellationToken);

await client.Schemas.ValidateAsync(repository.RepositoryId, cancellationToken: cancellationToken);

await foreach (var change in client.Changes.SubscribeRepositoryAsync(
    repository.RepositoryId,
    limit: 10,
    cancellationToken))
{
    Console.WriteLine($"{change.Type}: {change.Id}");
}
```

```csharp
public sealed record OrderDocument(string OrderNumber, string Status);
```

## API Keys, M2M Applications, Notifications, And Backups

```csharp
var apiKey = await client.ApiKeys.CreateAsync(
    projectId,
    new TerraScaleCreateApiKeyRequest
    {
        Name = "Orders worker",
        Scopes = ["documents:read", "documents:write"],
        DatabaseId = databaseId,
        EnvironmentId = environmentId
    },
    cancellationToken);

var m2m = await client.M2MApplications.CreateAsync(
    projectId,
    new TerraScaleCreateM2MApplicationRequest
    {
        Name = "Orders sync",
        Scopes = ["documents:read"]
    },
    cancellationToken);

var notifications = await client.Notifications.ListAsync(
    unreadOnly: true,
    cancellationToken: cancellationToken);
await client.Notifications.MarkAllReadAsync(cancellationToken);

var policy = await client.Backups.ConfigurePolicyAsync(
    projectId,
    databaseId,
    new TerraScaleConfigureBackupPolicyRequest(
        FullBackupCron: "0 3 * * *",
        RetentionDays: 14),
    cancellationToken);

var backupPlan = await client.Backups.PlanAsync(
    projectId,
    databaseId,
    new TerraScaleBackupConfirmationPlanRequest(operationSessionId),
    cancellationToken);
var backupExecution = await client.Backups.ExecuteAsync(
    projectId,
    databaseId,
    new TerraScaleBackupConfirmationExecuteRequest(operationSessionId, backupPlan.ConfirmationToken),
    idempotencyKey: $"backup-{operationSessionId}",
    cancellationToken);
var backup = backupExecution.Job;
var restore = await client.Backups.StartRestoreAsync(
    projectId,
    databaseId,
    backup.BackupId,
    cancellationToken: cancellationToken);
```

API key and M2M create/rotate responses include secrets only in that response. Store them in your application secret boundary; the SDK does not persist them.

## Retries, Idempotency, Request IDs, And Tracing

Retries are opt-in. The SDK retries safe requests and mutating requests only when an idempotency key factory is configured.

```csharp
var client = new TerraScaleClient(
    new HttpClient(),
    new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.terrascale.tech"),
        ProjectId = projectId,
        ApiKey = apiKey,
        MaxRetryAttempts = 2,
        RetryDelay = TimeSpan.FromMilliseconds(250),
        IdempotencyKeyFactory = static () => $"sdk-{Guid.NewGuid():N}"
    });
```

Every request sends `X-Request-Id` by default and preserves API response request ids on `TerraScaleApiException.RequestId`. The SDK emits activities from `TerraScaleTelemetry.ActivitySourceName`; register that source in OpenTelemetry tracing.

## Entity Framework Core Source Integration

`TerraScale.EntityFrameworkCore` provides a TerraScale-backed entity source for
EF Core models. It does not replace a relational EF provider or run migrations;
it maps entity types to TerraScale repositories and uses the public
`TerraScale.DotNet` client for document operations.

```csharp
services.AddDbContext<OrdersContext>(options => options.UseTerraScaleSource());
services.AddTerraScaleEntityFrameworkCore<OrdersContext>(options =>
{
    options.BaseAddress = new Uri("https://api.terrascale.tech");
    options.ProjectId = projectId;
    options.ApiKey = apiKey;
    options.RepositoryNameResolver = _ => "orders";
});
```

Map an entity to a repository in `OnModelCreating`:

```csharp
modelBuilder.Entity<Order>(builder =>
{
    builder.HasKey(order => order.Id);
    builder.ToTerraScaleRepository("orders");
});
```

Use the TerraScale entity source or set helpers for repository-backed document
operations:

```csharp
var source = serviceProvider.GetRequiredService<TerraScaleEntitySource<OrdersContext>>();
var orders = await source.Set<Order>().ListAsync(take: 25);
```

## Quickstart Sample

The sample app uses the real TerraScale Platform API and is environment-gated:

```bash
export TERRASCALE_API_BASE_ADDRESS=https://api.terrascale.tech
export TERRASCALE_PROJECT_ID=01J00000000000000000000000
export TERRASCALE_API_KEY=replace-with-project-api-key
export TERRASCALE_DATABASE_ID=01J00000000000000000000001
export TERRASCALE_ENVIRONMENT_ID=01J00000000000000000000002

dotnet run --project samples/TerraScale.Samples.Quickstart/TerraScale.Samples.Quickstart.csproj
```

Use `TERRASCALE_BEARER_TOKEN` instead of `TERRASCALE_API_KEY` and `TERRASCALE_PROJECT_ID` for caller-provided OAuth or machine-to-machine bearer tokens.

## Running Tests

The test projects are E2E-first and intentionally environment-gated. Without
TerraScale Platform API configuration they verify local configuration handling and skip
networked success-path coverage instead of substituting a fake API.

```bash
dotnet test TerraScale.DotNet.slnx --configuration Release --no-progress
```

Set only the live API base URL to run the authentication-failure E2E path
against the real public API:

```bash
TERRASCALE_E2E_BASE_URL=https://api.terrascale.tech \
  dotnet test TerraScale.DotNet.slnx --configuration Release --no-progress
```

Set the full API-key configuration to run the database, repository, document,
query, batch, telemetry, and EF Core success-path E2E tests:

```bash
export TERRASCALE_E2E_BASE_URL=https://api.terrascale.tech
export TERRASCALE_E2E_API_KEY=replace-with-project-api-key
export TERRASCALE_E2E_PROJECT_ID=01J00000000000000000000000
export TERRASCALE_E2E_REPOSITORY=01J00000000000000000000001
dotnet test TerraScale.DotNet.slnx --configuration Release --no-progress
```

Route and DTO contract tests for the newer project, auth management, notification, and backups families run locally without fake HTTP servers. Full live-stack coverage for those API families belongs in the TerraScale.Platform integration test suite and is intentionally not duplicated here yet.

For bearer-token coverage, set `TERRASCALE_E2E_BEARER_TOKEN` with
`TERRASCALE_E2E_BASE_URL` and `TERRASCALE_E2E_REPOSITORY`.
