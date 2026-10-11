using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class TerraBaseDashboardState(IDashboardIdentity identity, IHttpClientFactory http) : IDashboardState
{
    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(cancellationToken);
        await database.ListCollectionsAsync(limit: 1, cancellationToken: cancellationToken);
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
        var client = new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions
        {
            EndpointResolver = new TerraBaseDiscovery(http.CreateClient("platform")),
            ApiKey = credentials.StateApiKey
        });
        return client.Database(credentials.StateProjectId, "dashboard");
    }
}
