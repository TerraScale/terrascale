using MagicOnion;
using MagicOnion.Server;
using TerraScale.TerraBase.Contracts.Webhooks;

namespace TerraScale.TerraBase.Rpc.Webhooks
{
    /// <summary>Serves <see cref="IWebhooksService"/> through TerraBase’s REST pipeline.</summary>
    public sealed class WebhooksService(RestApi api) : ServiceBase<IWebhooksService>, IWebhooksService
    {
        private static readonly WebhooksJson Json = WebhooksJson.Default;

        /// <inheritdoc />
        public async UnaryResult<TerraBaseCreatedChangeWebhook> CreateChangeWebhookAsync(string projectId, string database, string collection, string url)
        {
            return RestApi.Read(await api.SendAsync(Context, "POST", Path(projectId, database, collection), RestApi.Json(new TerraBaseCreateChangeWebhookRequest(url), Json.TerraBaseCreateChangeWebhookRequest)), Json.TerraBaseCreatedChangeWebhook);
        }

        /// <inheritdoc />
        public async UnaryResult<IReadOnlyList<TerraBaseChangeWebhook>> ListChangeWebhooksAsync(string projectId, string database, string collection, string? after, int? limit)
        {
            return RestApi.Read(await api.SendAsync(Context, "GET", Path(projectId, database, collection) + Query(after, limit)), Json.IReadOnlyListTerraBaseChangeWebhook);
        }

        /// <inheritdoc />
        public async UnaryResult DeleteChangeWebhookAsync(string projectId, string database, string collection, string webhookId)
        {
            RestApi.Ensure(await api.SendAsync(Context, "DELETE", Path(projectId, database, collection) + "/" + Uri.EscapeDataString(webhookId)));
        }

        /// <inheritdoc />
        public async UnaryResult<IReadOnlyList<TerraBaseChangeWebhookDelivery>> ListChangeWebhookDeliveriesAsync(string projectId, string database, string collection, string webhookId, string? after, int? limit)
        {
            return RestApi.Read(await api.SendAsync(Context, "GET", Path(projectId, database, collection) + "/" + Uri.EscapeDataString(webhookId) + "/deliveries" + Query(after, limit)), Json.IReadOnlyListTerraBaseChangeWebhookDelivery);
        }

        private static string Path(string projectId, string database, string collection)
        {
            return RestPaths.Collection(projectId, database, collection) + "/webhooks";
        }

        private static string Query(string? after, int? limit)
        {
            return RestPaths.Query(("after", after), ("limit", RestPaths.Number(limit)));
        }
    }
}
