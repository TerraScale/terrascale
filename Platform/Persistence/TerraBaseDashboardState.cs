using System.Text.Json;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class TerraBaseDashboardState(IDashboardIdentity identity, IHttpClientFactory http) : IDashboardState
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var database = Database(credentials);
        var created = await database.CreateAsync(cancellationToken);
        if (created.ProjectId != credentials.StateProjectId || created.Name != DashboardStateSchema.Database || !TerraScaleIds.IsValid(created.Id))
            throw new IOException("Dashboard state database identity is invalid.");

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
        var database = await DatabaseAsync(cancellationToken);
        await Task.WhenAll(DashboardStateSchema.Collections.Select(async name =>
            ValidateCollection(await database.GetCollectionAsync(name, cancellationToken), name)));
    }

    public async Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(cancellationToken)).GetDocumentAsync(collection, id, cancellationToken);

    public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(cancellationToken)).PutDocumentAsync(collection, id, document, precondition, cancellationToken);

    public async Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(cancellationToken)).DeleteDocumentAsync(collection, id, TerraBasePrecondition.Matches(version), cancellationToken);

    private async Task<TerraBaseDatabase> DatabaseAsync(CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        return Database(credentials);
    }

    private TerraBaseDatabase Database(DashboardCredentials credentials)
    {
        if (!TerraScaleIds.IsValid(credentials.StateProjectId)) throw new IOException("Dashboard state project identity is invalid.");
        var client = new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions
        {
            EndpointResolver = new TerraBaseDiscovery(http.CreateClient("platform")),
            ApiKey = credentials.StateApiKey
        });
        return client.Database(credentials.StateProjectId, DashboardStateSchema.Database);
    }

    private static void ValidateCollection(TerraBaseCollection collection, string name)
    {
        if (collection.Name != name) throw new IOException("Dashboard state collection identity is invalid.");
    }
}
