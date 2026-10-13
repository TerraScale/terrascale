using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

// The dashboard's own dogfood database, reached through TerraBase's private
// listener without a credential. No customer key or token ever reaches it.
public sealed class TerraBaseDashboardState(IHttpClientFactory http, IHostEnvironment environment) : IDashboardState
{
    private readonly Uri origin = Origin(environment, Environment.GetEnvironmentVariable("FLY_APP_NAME"));

    // gru stays the single home until cross-region conditional writes are proven (integration plan G15).
    public static Uri Origin(IHostEnvironment environment, string? flyApp) =>
        flyApp is not null ? new("http://gru.ts-terrabase.internal:8081/")
        : environment.IsDevelopment() ? new("http://127.0.0.1:8081/")
        : throw new InvalidOperationException("Off Fly only Development may use the loopback TerraBase.");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // The database is seeded by TerraBase; a missing one fails here instead of being created.
        var database = Database();
        foreach (var name in DashboardStateSchema.Collections)
        {
            TerraBaseCollection collection;
            try { collection = await database.GetCollectionAsync(name, cancellationToken); }
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_collection")
            {
                try { collection = await database.CreateCollectionAsync(name, cancellationToken); }
                catch (TerraBaseConflictException conflict) when (conflict.ErrorCode == "name_taken")
                {
                    collection = await database.GetCollectionAsync(name, cancellationToken);
                }
            }
            ValidateCollection(collection, name);
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        var database = Database();
        await Task.WhenAll(DashboardStateSchema.Collections.Select(async name =>
            ValidateCollection(await database.GetCollectionAsync(name, cancellationToken), name)));
    }

    public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) =>
        Database().GetDocumentAsync(collection, id, cancellationToken);

    public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken) =>
        await Database().PutDocumentAsync(collection, id, document, precondition, cancellationToken);

    public async Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken) =>
        await Database().DeleteDocumentAsync(collection, id, TerraBasePrecondition.Matches(version), cancellationToken);

    private TerraBaseDatabase Database() =>
        new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions { BaseAddress = origin })
            .Database(DashboardStateSchema.ProjectId, DashboardStateSchema.Database);

    private static void ValidateCollection(TerraBaseCollection collection, string name)
    {
        if (collection.Name != name) throw new IOException("Dashboard state collection identity is invalid.");
    }
}
