using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookOutboxRecord(string InstanceId, string ProjectId, string EventType, string EventId, DateTimeOffset OccurredAt,
    WebhookEventPayload Payload, string Status, int Attempts, DateTimeOffset NextAttemptAt);

public sealed record WebhookEventPayload(string UserId, string? Email = null, string? Provider = null);

public sealed class WebhookOutbox(WebhookEndpoints endpoints, TimeProvider clock)
{
    public const string PendingStatus = "pending";

    public async Task<StoreOperation?> CreateAsync(string projectId, string eventType, string userId, string? email, string? provider, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(projectId))
        {
            return null;
        }

        var registered = await endpoints.ListAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!registered.Any(endpoint => endpoint.EventTypes.Contains(eventType, StringComparer.Ordinal)))
        {
            return null;
        }

        var eventId = AuthStore.NewId();
        var occurredAt = clock.GetUtcNow();
        var record = new WebhookOutboxRecord(projectId, projectId, eventType, eventId, occurredAt,
            new WebhookEventPayload(userId, email, provider), PendingStatus, 0, occurredAt);
        return AuthStore.Create(AuthStore.WebhookOutboxTable, projectId, eventId,
            JsonSerializer.SerializeToElement(record, WebhookOutboxJson.Default.WebhookOutboxRecord));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebhookOutboxRecord))]
internal sealed partial class WebhookOutboxJson : JsonSerializerContext;
