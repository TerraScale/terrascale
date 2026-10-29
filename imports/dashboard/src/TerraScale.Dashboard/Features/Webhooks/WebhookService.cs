using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Webhooks;

// Each operation uses current project authority; webhook state belongs to TerraBase.
public sealed class WebhookService(DatabaseService databases, IProjectAccessTokens tokens, IHttpClientFactory http)
{
    public async Task<IReadOnlyList<TerraBaseChangeWebhook>> ListAsync(AccountContext context, string databaseId, string collection, CancellationToken ct) =>
        await (await DatabaseAsync(context, databaseId, collection, false, ct)).ListChangeWebhooksAsync(collection, limit: 100, cancellationToken: ct);

    public async Task<TerraBaseCreatedChangeWebhook> CreateAsync(AccountContext context, string databaseId, string collection, string url, CancellationToken ct) =>
        await (await DatabaseAsync(context, databaseId, collection, true, ct)).CreateChangeWebhookAsync(collection, url.Trim(), ct);

    public async Task DeleteAsync(AccountContext context, string databaseId, string collection, string id, CancellationToken ct)
    {
        DataService.ValidDocumentId(id);
        await (await DatabaseAsync(context, databaseId, collection, true, ct)).DeleteChangeWebhookAsync(collection, id, ct);
    }

    public async Task<IReadOnlyList<TerraBaseChangeWebhookDelivery>> DeliveriesAsync(AccountContext context, string databaseId, string collection, string id, CancellationToken ct)
    {
        DataService.ValidDocumentId(id);
        return await (await DatabaseAsync(context, databaseId, collection, false, ct)).ListChangeWebhookDeliveriesAsync(collection, id, limit: 100, cancellationToken: ct);
    }

    private async Task<TerraBaseDatabase> DatabaseAsync(AccountContext context, string databaseId, string collection, bool write, CancellationToken ct)
    {
        context = await databases.CurrentAsync(context, ct);
        if (write && !context.CanWriteProjectData) throw new AccountAccessException("Write access is required.");
        DataService.ValidCollection(collection);
        var database = DatabaseService.Database(context, databaseId);
        var scope = DatabaseService.Scope(context);
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) throw PlatformDatabases.ReadOnly();
        var home = await databases.HomeAsync(context, database, ct);
        var token = await tokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId,
            write ? ["documents:read", "documents:write"] : ["documents:read"], ct);
        var client = new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = home.Api,
            Region = home.Id,
            AccessTokenProvider = _ => ValueTask.FromResult(token)
        });
        return client.Database(scope.ProjectId, database.Name);
    }
}
