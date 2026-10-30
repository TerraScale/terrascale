using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Webhooks
{
    /// <summary>The Webhooks REST API over RPC, with identical authorization and placement.</summary>
    public interface IWebhooksService : IService<IWebhooksService>
    {
        /// <summary>Registers an HTTPS receiver; the signing secret is returned only once.</summary>
        UnaryResult<TerraBaseCreatedChangeWebhook> CreateChangeWebhookAsync(string projectId, string database, string collection, string url);

        /// <summary>Lists receiver metadata in ID order, limit 1–100.</summary>
        UnaryResult<IReadOnlyList<TerraBaseChangeWebhook>> ListChangeWebhooksAsync(string projectId, string database, string collection, string? after, int? limit);

        /// <summary>Stops future attempts; an in-flight delivery may complete.</summary>
        UnaryResult DeleteChangeWebhookAsync(string projectId, string database, string collection, string webhookId);

        /// <summary>Lists retained delivery records in ID order, limit 1–100.</summary>
        UnaryResult<IReadOnlyList<TerraBaseChangeWebhookDelivery>> ListChangeWebhookDeliveriesAsync(string projectId, string database, string collection, string webhookId, string? after, int? limit);
    }
}
