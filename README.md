# TerraScale.DotNet

Official C# SDK for TerraScale database and repository APIs.

The SDK only calls TerraScale public `/api/v1` database and repository endpoints. It does not manage users, organizations, API keys, auth providers, Dashboard internals, or TerraBase storage directly.

## Install

Use the project reference in this checkout, or install the package once it is published:

```bash
dotnet add package TerraScale.DotNet
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

## Database And Environment Selection

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
