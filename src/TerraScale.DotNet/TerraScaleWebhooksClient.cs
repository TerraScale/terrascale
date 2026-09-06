using TerraScale.DotNet.Generated;

#pragma warning disable CS1591
namespace TerraScale.DotNet;

/// <summary>Durable webhook endpoints, one-time signing secrets, and delivery operations.</summary>
public sealed class TerraScaleWebhooksClient(TerraScaleClient client)
{
    public Task<WebhookList> ListAsync(string database, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, Route(database), TerraScaleContractJsonContext.Default.WebhookList, cancellationToken);

    public Task<WebhookSecretResponse> CreateAsync(string database, string endpoint, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (idempotencyKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return client.SendJsonAsync(HttpMethod.Post, Route(database), new CreateWebhook(endpoint), TerraScaleContractJsonContext.Default.CreateWebhook,
            TerraScaleContractJsonContext.Default.WebhookSecretResponse, cancellationToken, idempotencyKey ?? string.Empty);
    }

    public Task<WebhookSecretResponse> RotateSecretAsync(string database, string webhookId, uint overlapSeconds = 300, CancellationToken cancellationToken = default)
    {
        if (overlapSeconds > 3600) throw new ArgumentOutOfRangeException(nameof(overlapSeconds));
        return client.SendNonRetryableJsonAsync(HttpMethod.Post, Endpoint(database, webhookId) + "/rotate-secret", new RotateWebhookSecret(overlapSeconds),
            TerraScaleContractJsonContext.Default.RotateWebhookSecret, TerraScaleContractJsonContext.Default.WebhookSecretResponse, cancellationToken);
    }

    public Task<WebhookEndpoint> DeleteAsync(string database, string webhookId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Delete, Endpoint(database, webhookId), TerraScaleContractJsonContext.Default.WebhookEndpoint, cancellationToken, string.Empty);

    public Task<DeliveryLog> ListDeliveriesAsync(string database, string webhookId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Get, Endpoint(database, webhookId) + "/deliveries", TerraScaleContractJsonContext.Default.DeliveryLog, cancellationToken);

    public Task<DeliveryRecord> ReplayAsync(string database, string webhookId, string deliveryId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync(HttpMethod.Post, Endpoint(database, webhookId) + "/deliveries/" + TerraScaleManagementPaths.Escape(deliveryId) + "/replay",
            TerraScaleContractJsonContext.Default.DeliveryRecord, cancellationToken, string.Empty);

    private static string Route(string database) => TerraScaleManagementPaths.Database(database) + "/webhooks";
    private static string Endpoint(string database, string webhookId) => Route(database) + "/" + TerraScaleManagementPaths.Escape(webhookId);
}
