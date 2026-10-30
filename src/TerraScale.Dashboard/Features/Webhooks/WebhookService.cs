using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts.Webhooks;

namespace TerraScale.Dashboard.Features.Webhooks
{
    // Each operation uses current project authority; webhook state belongs to TerraBase.
    public sealed class WebhookService(DatabaseService databases, IProjectAccessTokens tokens, TerraBaseRpcClient rpc)
    {
        public async Task<IReadOnlyList<TerraBaseChangeWebhook>> ListAsync(AccountContext context, string databaseId, string collection, CancellationToken ct)
        {
            var (client, project, database) = await DatabaseAsync(context, databaseId, collection, false, ct);
            return await client.ListChangeWebhooksAsync(project, database, collection, null, 100);
        }

        public async Task<TerraBaseCreatedChangeWebhook> CreateAsync(AccountContext context, string databaseId, string collection, string url, CancellationToken ct)
        {
            var (client, project, database) = await DatabaseAsync(context, databaseId, collection, true, ct);
            url = url.Trim();
            if (url is not { Length: > 0 and <= 2048 } || !Uri.TryCreate(url, UriKind.Absolute, out var receiver)
                || receiver.Scheme != Uri.UriSchemeHttps || receiver.Port != 443 || receiver.UserInfo.Length != 0 || receiver.Fragment.Length != 0)
            {
                throw new ArgumentException("Webhook URL must be HTTPS on port 443, without credentials or a fragment, at most 2048 characters.", nameof(url));
            }

            return await client.CreateChangeWebhookAsync(project, database, collection, url);
        }

        public async Task DeleteAsync(AccountContext context, string databaseId, string collection, string id, CancellationToken ct)
        {
            DataService.ValidDocumentId(id);
            var (client, project, database) = await DatabaseAsync(context, databaseId, collection, true, ct);
            await client.DeleteChangeWebhookAsync(project, database, collection, id);
        }

        public async Task<IReadOnlyList<TerraBaseChangeWebhookDelivery>> DeliveriesAsync(AccountContext context, string databaseId, string collection, string id, CancellationToken ct)
        {
            DataService.ValidDocumentId(id);
            var (client, project, database) = await DatabaseAsync(context, databaseId, collection, false, ct);
            return await client.ListChangeWebhookDeliveriesAsync(project, database, collection, id, null, 100);
        }

        private async Task<(IWebhooksService Client, string Project, string Database)> DatabaseAsync(AccountContext context, string databaseId, string collection, bool write, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            if (write && !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Write access is required.");
            }

            DataService.ValidCollection(collection);
            var database = DatabaseService.Database(context, databaseId);
            var scope = DatabaseService.Scope(context);
            if (PlatformDatabases.IsPlatform(scope.OrganizationId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            var home = await databases.HomeAsync(context, database, ct);
            var token = await tokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId,
                write ? ["documents:read", "documents:write"] : ["documents:read"], ct);
            return (rpc.User<IWebhooksService>(token, home.Id).WithCancellationToken(ct), scope.ProjectId, database.Name);
        }
    }
}
