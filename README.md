# TerraScale.DotNet

Official C# SDK for TerraScale public API families.

The SDK calls verified TerraScale public `/api/v1` endpoints. It covers region catalog, database, repository, document, schema, TQL, changes, billing, project, project database, environment, usage, project auth API key, machine-to-machine application, notification, backup, restore-point, and restore operations. It does not call TerraBase storage directly and does not cover admin, user-management, organization-management, auth-provider, invitation, audit, or Dashboard-internal test endpoints.

## Install

Use the project reference in this checkout, or install the package once it is published:

```bash
dotnet add package TerraScale.DotNet
```

The repo also contains the EF Core source integration package:

```bash
dotnet add package TerraScale.EntityFrameworkCore
```

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

## Database And Environment Selection

Create, inspect, and clean up logical databases through the public database API:

```csharp
var database = await client.Databases.CreateAsync("orders-production", cancellationToken: cancellationToken);
var loadedDatabase = await client.Databases.GetAsync(database.DatabaseId, cancellationToken);
var collections = await client.Collections.ListAsync(loadedDatabase.DatabaseId, cancellationToken);

// Later, after repositories/data are no longer needed:
await client.Databases.DeleteAsync(loadedDatabase.DatabaseId, cancellationToken: cancellationToken);
```

Use project-scoped projects, databases, and environments when working through the live project metadata API:

```csharp
var regions = await client.Regions.ListAsync(cancellationToken);

var createdProject = await client.Projects.CreateAsync(
    organizationId,
    "orders",
    defaultPermissionKeys: ["auth:login"],
    cancellationToken);
var updatedProject = await client.Projects.UpdateAsync(
    createdProject.ProjectId,
    "orders-production",
    cancellationToken);
var project = await client.Projects.GetAsync(projectId, cancellationToken);

var database = await client.ProjectDatabases.CreateAsync(
    project.ProjectId,
    "orders-production",
    regions:
    [
        TerraScaleRegions.Gru,
        TerraScaleRegions.Sfo,
        TerraScaleRegions.Fra
    ],
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

var backup = await client.Backups.TriggerAsync(projectId, databaseId, cancellationToken: cancellationToken);
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

The sample app uses the real TerraScale API and is environment-gated:

```bash
export TERRASCALE_API_BASE_ADDRESS=http://localhost:5294
export TERRASCALE_PROJECT_ID=01J00000000000000000000000
export TERRASCALE_API_KEY=tsauth_...
export TERRASCALE_DATABASE_ID=01J00000000000000000000001
export TERRASCALE_ENVIRONMENT_ID=01J00000000000000000000002

dotnet run --project samples/TerraScale.Samples.Quickstart/TerraScale.Samples.Quickstart.csproj
```

Use `TERRASCALE_BEARER_TOKEN` instead of `TERRASCALE_API_KEY` and `TERRASCALE_PROJECT_ID` for caller-provided OAuth or machine-to-machine bearer tokens.

## Running Tests

The test projects are E2E-first and intentionally environment-gated. Without
TerraScale API configuration they verify local configuration handling and skip
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
export TERRASCALE_E2E_API_KEY=tsauth_...
export TERRASCALE_E2E_PROJECT_ID=01J00000000000000000000000
export TERRASCALE_E2E_REPOSITORY=01J00000000000000000000001
dotnet test TerraScale.DotNet.slnx --configuration Release --no-progress
```

Route and DTO contract tests for the newer project, auth management, notification, and backups families run locally without fake HTTP servers. Full live-stack coverage for those API families belongs in the TerraScale.API integration test suite and is intentionally not duplicated here yet.

For bearer-token coverage, set `TERRASCALE_E2E_BEARER_TOKEN` with
`TERRASCALE_E2E_BASE_URL` and `TERRASCALE_E2E_REPOSITORY`.
