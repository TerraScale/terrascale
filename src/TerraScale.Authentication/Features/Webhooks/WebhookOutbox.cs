using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookOutboxRecord(string InstanceId, string ProjectId, string EventType, string EventId, DateTimeOffset OccurredAt,
    string UserId, string? Email, string? Provider, string Status, int Attempts, DateTimeOffset NextAttemptAt, string? SessionId = null, string? EndpointId = null);


public sealed class WebhookOutbox(TimeProvider clock)
{
    public const string PendingStatus = "pending";

    public Task<Change?> CreateAsync(string projectId, string eventType, string userId, string? email, string? provider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Tenancy.IsProject(projectId) ? Change.Insert(this.NewEvent(projectId, eventType, userId, email, provider)) : null);
    }

    public WebhookOutboxRecord NewEvent(string projectId, string eventType, string userId, string? email = null, string? provider = null,
        string? sessionId = null, string? endpointId = null)
    {
        var occurredAt = clock.GetUtcNow();
        return new(projectId, projectId, eventType, AuthIds.New(), occurredAt, userId, email, provider, PendingStatus, 0, occurredAt, sessionId, endpointId);
    }
}
