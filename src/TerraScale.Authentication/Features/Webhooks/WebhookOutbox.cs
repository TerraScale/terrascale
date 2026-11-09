using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookOutboxRecord(string InstanceId, string ProjectId, string EventType, string EventId, DateTimeOffset OccurredAt,
    string UserId, string? Email, string? Provider, string Status, int Attempts, DateTimeOffset NextAttemptAt);


public sealed class WebhookOutbox(ProjectAuthSettings settings, TimeProvider clock)
{
    public const string PendingStatus = "pending";

    public Task<Change?> CreateAsync(string projectId, string eventType, string userId, string? email, string? provider, CancellationToken cancellationToken) =>
        this.CreateAsync(projectId, eventType, userId, email, provider, null, cancellationToken);

    public async Task<Change?> CreateAsync(string projectId, string eventType, string userId, string? email, string? provider,
        IReadOnlyList<string>? subscribed, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(projectId))
        {
            return null;
        }

        subscribed ??= (await settings.GetAsync(projectId, cancellationToken).ConfigureAwait(false)).SubscribedWebhookEvents;
        if (subscribed is null || !subscribed.Contains(eventType))
        {
            return null;
        }

        var eventId = AuthIds.New();
        var occurredAt = clock.GetUtcNow();
        var record = new WebhookOutboxRecord(projectId, projectId, eventType, eventId, occurredAt,
            userId, email, provider, PendingStatus, 0, occurredAt);
        return Change.Insert(record);
    }
}
