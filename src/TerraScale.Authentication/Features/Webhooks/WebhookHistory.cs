using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookDeliveryRecord(string Id, string ProjectId, string EndpointId, string EventId, string Status,
    int Attempts, DateTimeOffset NextAttemptAt, DateTimeOffset StartedAt, int CycleAttempts = 0)
{
    public string? Version { get; init; }
}

public sealed record WebhookAttemptRecord(string Id, string DeliveryId, string ProjectId, DateTimeOffset CreatedAt,
    string Status, int LatencyMs, int? ResponseCode, string? ResponseBody)
{
    public string? Version { get; init; }
}

public sealed record WebhookEventBody(string Id, string Type, DateTimeOffset Timestamp, WebhookEventData Data);

public sealed record WebhookEventData(string UserId, string? Email, string? Provider, string? SessionId, string? EndpointId, string? ActorId = null, string? Reason = null, string? OrganizationId = null, string? OrganizationRole = null, [property: JsonPropertyName("agent_id")] string? AgentId = null, [property: JsonPropertyName("agent_key_id")] string? AgentKeyId = null);

public sealed record WebhookEventPage(IReadOnlyList<WebhookEventBody> Items, string? Next);

public sealed record WebhookDeliveryBody(string Id, string EventId, string Status, int Attempts, DateTimeOffset NextAttemptAt,
    IReadOnlyList<WebhookAttemptRecord> RecentAttempts);

public sealed record WebhookDeliveryPage(IReadOnlyList<WebhookDeliveryBody> Items, string? Next);

public sealed class WebhookHistory(AuthData data, WebhookOutbox outbox, TimeProvider clock)
{
    public static WebhookEventBody Body(WebhookOutboxRecord row) => new(row.EventId, row.EventType, row.OccurredAt,
        new(row.UserId, row.Email, row.Provider, row.SessionId, row.EndpointId, row.ActorId, row.Reason, row.OrganizationId, row.OrganizationRole, row.AgentId, row.AgentKeyId));

    public async Task<Valid<WebhookEventPage>> EventsAsync(string projectId, string? after, int limit, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(after) && !TerraScaleIds.IsValid(after))
        {
            return new Validation("after must be an event ULID.", "invalid_request");
        }

        var now = clock.GetUtcNow();
        var (oldest, newest, take) = (now.AddDays(-30), now.AddSeconds(-5), limit + 1);
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<WebhookOutboxRecord>(
            """
            SELECT * FROM webhook_outbox
            WHERE occurred_at > @oldest AND occurred_at <= @newest AND project_id = @projectId AND (@after IS NULL OR @after = '' OR event_id > @after)
            ORDER BY event_id LIMIT @take
            """, new { oldest, newest, projectId, after, take }), cancellationToken)).AsList();
        return new WebhookEventPage([.. rows.Take(limit).Select(Body)], rows.Count > limit ? rows[limit - 1].EventId : null);
    }

    public async Task<WebhookDeliveryPage> DeliveriesAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        await this.EndpointAsync(projectId, endpointId, cancellationToken);
        var deliveries = (await data.ReadAsync(connection => connection.QueryAsync<WebhookDeliveryRecord>(
            "SELECT * FROM webhook_deliveries WHERE project_id = @projectId AND endpoint_id = @endpointId ORDER BY id DESC LIMIT 20", new { projectId, endpointId }),
            cancellationToken)).AsList();
        var ids = deliveries.Select(delivery => delivery.Id).ToList();
        var attempts = (await data.ReadAsync(connection => connection.QueryAsync<WebhookAttemptRecord>(
            "SELECT * FROM webhook_attempts WHERE project_id = @projectId AND delivery_id IN @ids", new { projectId, ids }), cancellationToken))
            .ToLookup(attempt => attempt.DeliveryId, StringComparer.Ordinal);
        var items = deliveries.Select(row => new WebhookDeliveryBody(row.Id, row.EventId, row.Status, row.Attempts, row.NextAttemptAt,
            [.. attempts[row.Id].OrderByDescending(attempt => attempt.Id).Take(10)])).ToList();

        return new WebhookDeliveryPage(items, null);
    }

    public async Task ReplayAsync(string projectId, string endpointId, string deliveryId, CancellationToken cancellationToken)
    {
        var endpoint = await this.EndpointAsync(projectId, endpointId, cancellationToken);
        var now = clock.GetUtcNow();
        if (endpoint.Value.DisabledAt is not null)
        {
            throw new ApiException(409, "webhook_endpoint_disabled", "Register a working endpoint before sending more events.");
        }

        var row = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WebhookDeliveryRecord>(
            "SELECT * FROM webhook_deliveries WHERE id = @deliveryId AND project_id = @projectId AND endpoint_id = @endpointId", new { deliveryId, projectId, endpointId }),
            cancellationToken)) ?? throw new ApiException(404, "not_found", "No such delivery.");
        if (endpoint.Value.LeaseUntil > now || row.Value.Status == "sending")
        {
            throw new ApiException(409, "delivery_in_progress", "The endpoint is delivering an event.");
        }

        if (!await data.SaveAsync([Change.Assert(endpoint), Change.Update(row, row.Value with { Status = "pending", NextAttemptAt = now, StartedAt = now, CycleAttempts = 0 })], cancellationToken))
        {
            throw new ApiException(409, "version_conflict", "The delivery changed concurrently.");
        }
    }

    public async Task<WebhookEventBody> TestAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        var endpoint = await this.EndpointAsync(projectId, endpointId, cancellationToken);
        if (endpoint.Value.DisabledAt is not null)
        {
            throw new ApiException(409, "webhook_endpoint_disabled", "Register a working endpoint before sending more events.");
        }

        var row = outbox.NewEvent(projectId, "webhook.test", "", endpointId: endpointId) with { Status = "expanded" };
        if (!await data.SaveAsync([Change.Insert(row), Change.Insert(NewDelivery(row, endpointId))], cancellationToken))
        {
            throw new ApiException(409, "version_conflict", "The endpoint changed concurrently.");
        }

        return Body(row);
    }

    internal static WebhookDeliveryRecord NewDelivery(WebhookOutboxRecord row, string endpointId) =>
        new(AuthIds.New(), row.ProjectId, endpointId, row.EventId, "pending", 0, row.OccurredAt, row.OccurredAt);

    internal async Task<Versioned<WebhookEndpointStateRecord>> EndpointAsync(string projectId, string endpointId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WebhookEndpointStateRecord>(
            "SELECT * FROM webhook_endpoint_states WHERE project_id = @projectId AND endpoint_id = @endpointId", new { projectId, endpointId }), cancellationToken))
        ?? throw new ApiException(404, "not_found", "No such endpoint.");
}
