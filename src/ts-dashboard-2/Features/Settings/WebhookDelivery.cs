namespace TerraScale.Dashboard2;

/// <summary>Port of WebhookDelivery: one event delivery stream with up to 5 retry attempts.</summary>
public sealed record WebhookDelivery(
    string Id,
    string WebhookId,
    string Event,
    string Status,
    int Attempt,
    int MaxAttempts,
    int? StatusCode,
    string CreatedAt,
    string DeliveredAt,
    string NextAttemptAt,
    string Error);
