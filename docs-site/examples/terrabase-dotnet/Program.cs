using System.Text.Json;
using TerraScale.DotNet.TerraBase;

public static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var endpoint = Required("TERRASCALE_API_URL");
            var token = Required("TERRASCALE_ACCESS_TOKEN");
            var project = Required("TERRASCALE_PROJECT_ID");
            var database = Required("TERRASCALE_DATABASE_NAME");
            var collection = Required("TERRASCALE_COLLECTION_NAME");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var http = new HttpClient(TerraBaseHttp.CreateHandler());
            await RunAsync(http, new Uri(endpoint), token, project, database, collection, deadline.Token);
            Console.WriteLine("PASS_TERRABASE_CRUD_QUERY");
            return 0;
        }
        catch (TerraBaseException error)
        {
            Console.Error.WriteLine($"TerraBase rejected the request: HTTP {(int?)error.StatusCode} {error.ErrorCode ?? "no_code"}. Do not blindly replay writes.");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Starter failed: {error.GetType().Name}: {error.Message} An interrupted write may need reconciliation.");
            return 1;
        }
    }

    // Creates, reads, updates, queries and deletes one document, then checks stale-version rejection.
    public static async Task RunAsync(HttpClient http, Uri endpoint, string token,
        string project, string databaseName, string collection, CancellationToken cancellationToken)
    {
        var client = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = endpoint,
            ApiKey = token, // SDK property name; the serving contract determines the accepted credential.
            RequestTimeout = TimeSpan.FromSeconds(15)
        });
        var database = client.Database(project, databaseName);
        try
        {
            await database.CreateCollectionAsync(collection, cancellationToken);
        }
        catch (TerraBaseConflictException)
        {
            // Expected after the first run: the collection already exists.
        }
        var run = Guid.NewGuid().ToString("N");
        var created = await database.CreateDocumentAsync(collection,
            JsonSerializer.SerializeToElement(new { title = "Hello TerraBase", run }), cancellationToken);
        var current = await database.GetDocumentAsync(collection, created.Id, cancellationToken)
            ?? throw new InvalidOperationException("Created document is missing.");
        if (current.State != "value" || current.Version != created.Version ||
            current.Document.GetProperty("title").GetString() != "Hello TerraBase")
            throw new InvalidOperationException("Read differs from the created document.");

        var updated = await database.PatchDocumentAsync(collection,
            created.Id, JsonSerializer.SerializeToElement(new { title = "Updated" }),
            TerraBasePrecondition.Matches(current.Version), cancellationToken);
        try
        {
            await database.PatchDocumentAsync(collection, created.Id,
                JsonSerializer.SerializeToElement(new { title = "Stale write" }),
                TerraBasePrecondition.Matches(current.Version), cancellationToken);
            throw new InvalidOperationException("A stale version was accepted.");
        }
        catch (TerraBasePreconditionFailedException)
        {
            // Expected: the opaque version protects against replacing a newer value.
        }
        var page = await database.QueryAsync(collection, new TerraBaseQuery
        {
            Filter = JsonSerializer.SerializeToElement(new { eq = new[] { "/run", run } }),
            Limit = 20
        }, cancellationToken);
        if (page.Items.Count != 1 || page.Items[0].Id != created.Id ||
            page.Items[0].Document.GetProperty("title").GetString() != "Updated")
            throw new InvalidOperationException("Query differs from the updated document.");

        await database.DeleteDocumentAsync(collection, created.Id,
            TerraBasePrecondition.Matches(updated.Version), cancellationToken);
        if (await database.GetDocumentAsync(collection, created.Id, cancellationToken) is not null)
            throw new InvalidOperationException("Deleted document is still visible.");
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing starter configuration.");
}
