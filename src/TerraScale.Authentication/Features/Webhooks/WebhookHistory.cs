using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookDeliveryRecord(string Id, string ProjectId, string EndpointId, string EventId, string Status,
    int Attempts, DateTimeOffset NextAttemptAt, DateTimeOffset StartedAt, int CycleAttempts = 0);

public sealed record WebhookAttemptRecord(string Id, string DeliveryId, string ProjectId, DateTimeOffset CreatedAt,
    string Status, int LatencyMs, int? ResponseCode, string? ResponseBody);

public sealed record WebhookEventBody(string Id, string Type, DateTimeOffset Timestamp, WebhookEventData Data);

public readonly union WebhookMutation(Done, NotFound, Conflict);

public readonly union WebhookEvent(WebhookEventBody, Conflict);

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

        var rows = await data.ReadAsync((db, token) => db.WebhookOutbox
            .FromSqlRaw("""
                SELECT * FROM webhook_outbox
                WHERE occurred_at > strftime('%Y-%m-%d %H:%M:%f', 'now', '-30 days')
                  AND occurred_at <= strftime('%Y-%m-%d %H:%M:%f', 'now', '-5 seconds')
                """)
            .Where(row => row.ProjectId == projectId && (after == null || after == "" || string.Compare(row.EventId, after) > 0))
            .OrderBy(row => row.EventId).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new WebhookEventPage([.. rows.Take(limit).Select(Body)], rows.Count > limit ? rows[limit - 1].EventId : null);
    }

    public async Task<WebhookDeliveryPage> DeliveriesAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        _ = await this.EndpointAsync(projectId, endpointId, cancellationToken).ConfigureAwait(false);
        var rows = await data.ReadAsync((db, token) =>
            (from delivery in db.WebhookDeliveries.Where(row => row.ProjectId == projectId && row.EndpointId == endpointId)
                .OrderByDescending(row => row.Id).Take(20)
             join attempt in db.WebhookAttempts.Where(row => row.ProjectId == projectId) on delivery.Id equals attempt.DeliveryId into attempts
             from attempt in attempts.DefaultIfEmpty()
             select new { Delivery = delivery, Attempt = attempt }).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        var items = rows.GroupBy(row => row.Delivery.Id).OrderByDescending(group => group.Key).Select(group =>
        {
            var row = group.First().Delivery;
            return new WebhookDeliveryBody(row.Id, row.EventId, row.Status, row.Attempts, row.NextAttemptAt,
                [.. group.Select(item => item.Attempt).OfType<WebhookAttemptRecord>().OrderByDescending(attempt => attempt.Id).Take(10)]);
        }).ToList();

        return new WebhookDeliveryPage(items, null);
    }

    public async Task<WebhookMutation> ReplayAsync(string projectId, string endpointId, string deliveryId, CancellationToken cancellationToken)
    {
        var endpoint = await this.EndpointAsync(projectId, endpointId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (endpoint.Value.DisabledAt is not null)
        {
            return new Conflict("Register a working endpoint before sending more events.", "webhook_endpoint_disabled");
        }

        if (await data.ReadAsync((db, token) => db.WebhookDeliveries
            .Where(row => row.Id == deliveryId && row.ProjectId == projectId && row.EndpointId == endpointId).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return new NotFound("No such delivery.");
        }

        if (endpoint.Value.LeaseUntil > now || row.Value.Status == "sending")
        {
            return new Conflict("The endpoint is delivering an event.", "delivery_in_progress");
        }

        if (!await data.SaveAsync([Change.Assert(endpoint), Change.Update(row, row.Value with { Status = "pending", NextAttemptAt = now, StartedAt = now, CycleAttempts = 0 })], cancellationToken).ConfigureAwait(false))
        {
            return new Conflict("The delivery changed concurrently.", "version_conflict");
        }

        return new Done();
    }

    public async Task<WebhookEvent> TestAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        var endpoint = await this.EndpointAsync(projectId, endpointId, cancellationToken).ConfigureAwait(false);
        if (endpoint.Value.DisabledAt is not null)
        {
            return new Conflict("Register a working endpoint before sending more events.", "webhook_endpoint_disabled");
        }

        var row = outbox.NewEvent(projectId, "webhook.test", "", endpointId: endpointId) with { Status = "expanded" };
        if (!await data.SaveAsync([Change.Insert(row), Change.Insert(NewDelivery(row, endpointId))], cancellationToken).ConfigureAwait(false))
        {
            return new Conflict("The endpoint changed concurrently.", "version_conflict");
        }

        return Body(row);
    }

    internal static WebhookDeliveryRecord NewDelivery(WebhookOutboxRecord row, string endpointId) =>
        new(AuthIds.New(), row.ProjectId, endpointId, row.EventId, "pending", 0, row.OccurredAt, row.OccurredAt);

    internal async Task<Versioned<WebhookEndpointStateRecord>> EndpointAsync(string projectId, string endpointId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.WebhookEndpointStates.Where(row => row.ProjectId == projectId && row.EndpointId == endpointId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false)
        ?? throw ToException(new NotFound("No such endpoint."));

    private static ApiException ToException(NotFound failure) => new(StatusCodes.Status404NotFound, failure.Code, failure.Message ?? "No such resource.");
}
