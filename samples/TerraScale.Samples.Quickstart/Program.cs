using TerraScale.DotNet;

var configuration = TerraScaleSampleConfiguration.FromEnvironment();
if (!configuration.IsConfigured)
{
    Console.Error.WriteLine(configuration.MissingConfigurationMessage);
    return 2;
}

using var httpClient = new HttpClient
{
    BaseAddress = configuration.BaseAddress
};

var client = new TerraScaleClient(
    httpClient,
    new TerraScaleClientOptions
    {
        BaseAddress = configuration.BaseAddress,
        ApiKey = configuration.ApiKey,
        BearerToken = configuration.BearerToken,
        ProjectId = configuration.ProjectId,
        DatabaseId = configuration.DatabaseId,
        EnvironmentId = configuration.EnvironmentId,
        MaxRetryAttempts = 2,
        IdempotencyKeyFactory = static () => $"quickstart-{Guid.NewGuid():N}"
    });

var target = configuration.EnvironmentId is { Length: > 0 }
    ? client.ForEnvironment(configuration.DatabaseId!, configuration.EnvironmentId)
    : configuration.DatabaseId is { Length: > 0 }
        ? client.ForDatabase(configuration.DatabaseId)
        : client;

var suffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var repository = await target.Repositories.CreateAsync(
    name: $"quickstart-orders-{suffix}",
    collection: "orders",
    schemaValidationEnabled: true,
    schema: """
    {
      "type": "object",
      "required": ["orderNumber", "status"],
      "properties": {
        "orderNumber": { "type": "string" },
        "status": { "type": "string" },
        "total": { "type": "number" }
      }
    }
    """);

Console.WriteLine($"Created repository {repository.Name} ({repository.RepositoryId})");

try
{
    await client.Schemas.ValidateAsync(repository.RepositoryId);

    using var changeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var firstChange = ReadFirstChangeAsync(
        client.Changes.SubscribeRepositoryAsync(
            repository.RepositoryId,
            limit: 1,
            changeTimeout.Token),
        changeTimeout.Token);

    var created = await client.Documents.CreateAsync(
        repository.RepositoryId,
        new
        {
            orderNumber = $"QS-{suffix}",
            status = "new",
            total = 42.50m
        });

    var fetched = await client.Documents.GetAsync(repository.RepositoryId, created.Id);
    var patched = await client.Documents.PatchAsync(
        repository.RepositoryId,
        created.Id,
        new
        {
            status = "paid"
        });

    var paidOrders = TerraScaleQuery
        .From("orders")
        .WhereEqual("status", "paid")
        .OrderBy("orderNumber")
        .Take(10);

    var page = await client.Tql.QueryAsync(repository.RepositoryId, paidOrders);

    var batch = await client.Documents.BatchAsync(
        repository.RepositoryId,
        [
            TerraScaleDocumentBatchOperation.Create(new
            {
                orderNumber = $"QS-{suffix}-batch",
                status = "new",
                total = 19.99m
            }),
            TerraScaleDocumentBatchOperation.Patch(created.Id, new
            {
                status = "fulfilled"
            })
        ]);

    Console.WriteLine($"Created document {created.Id}");
    Console.WriteLine($"Fetched change vector: {fetched.ChangeVector ?? "none"}");
    Console.WriteLine($"Patched status: {patched.Data.GetProperty("status").GetString()}");
    Console.WriteLine($"Query returned {page.Items.Count} document(s)");
    Console.WriteLine($"Batch returned {batch.Results.Count} result(s)");

    try
    {
        var change = await firstChange;
        Console.WriteLine($"Observed SSE change: {change.Type} {change.Id}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("No SSE change arrived before the sample timeout.");
    }
}
finally
{
    await client.Repositories.DeleteAsync(repository.RepositoryId, deleteDocuments: true);
    Console.WriteLine($"Deleted repository {repository.RepositoryId}");
}

return 0;

static async Task<TerraScaleRepositoryChange> ReadFirstChangeAsync(
    IAsyncEnumerable<TerraScaleRepositoryChange> changes,
    CancellationToken cancellationToken)
{
    await foreach (var change in changes.WithCancellation(cancellationToken))
    {
        return change;
    }

    throw new OperationCanceledException(cancellationToken);
}

internal sealed record TerraScaleSampleConfiguration(
    Uri BaseAddress,
    string? ApiKey,
    string? BearerToken,
    string? ProjectId,
    string? DatabaseId,
    string? EnvironmentId)
{
    public bool IsConfigured =>
        BaseAddress.IsAbsoluteUri &&
        (!string.IsNullOrWhiteSpace(BearerToken) ||
            (!string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ProjectId)));

    public string MissingConfigurationMessage =>
        "Set TERRASCALE_API_BASE_ADDRESS plus either TERRASCALE_BEARER_TOKEN or both TERRASCALE_API_KEY and TERRASCALE_PROJECT_ID.";

    public static TerraScaleSampleConfiguration FromEnvironment()
    {
        var baseAddress = Environment.GetEnvironmentVariable("TERRASCALE_API_BASE_ADDRESS");
        return new TerraScaleSampleConfiguration(
            string.IsNullOrWhiteSpace(baseAddress)
                ? new Uri("http://localhost:5294", UriKind.Absolute)
                : new Uri(baseAddress, UriKind.Absolute),
            Normalize(Environment.GetEnvironmentVariable("TERRASCALE_API_KEY")),
            Normalize(Environment.GetEnvironmentVariable("TERRASCALE_BEARER_TOKEN")),
            Normalize(Environment.GetEnvironmentVariable("TERRASCALE_PROJECT_ID")),
            Normalize(Environment.GetEnvironmentVariable("TERRASCALE_DATABASE_ID")),
            Normalize(Environment.GetEnvironmentVariable("TERRASCALE_ENVIRONMENT_ID")));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
