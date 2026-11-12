using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookDeliveryRecord(string Id, string ProjectId, string EndpointId, string EventId, string Status,
    int Attempts, DateTimeOffset NextAttemptAt, DateTimeOffset StartedAt, int CycleAttempts = 0);

public sealed record WebhookAttemptRecord(string Id, string DeliveryId, string ProjectId, DateTimeOffset CreatedAt,
    string Status, int LatencyMs, int? ResponseCode, string? ResponseBody);

public sealed record WebhookEventBody(string Id, string Type, DateTimeOffset Timestamp, WebhookEventData Data);

public sealed record WebhookEventData(string UserId, string? Email, string? Provider, string? SessionId, string? EndpointId);

public sealed record WebhookEventPage(IReadOnlyList<WebhookEventBody> Items, string? Next);

public sealed record WebhookDeliveryBody(string Id, string EventId, string Status, int Attempts, DateTimeOffset NextAttemptAt,
    IReadOnlyList<WebhookAttemptRecord> RecentAttempts);

public sealed record WebhookDeliveryPage(IReadOnlyList<WebhookDeliveryBody> Items, string? Next);

public sealed class WebhookHistory(AuthData data, WebhookOutbox outbox, TimeProvider clock)
{
    public static WebhookEventBody Body(WebhookOutboxRecord row) => new(row.EventId, row.EventType, row.OccurredAt,
        new(row.UserId, row.Email, row.Provider, row.SessionId, row.EndpointId));

    public async Task<WebhookEventPage> EventsAsync(string projectId, string? after, int limit, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(after) && !TerraScaleIds.IsValid(after))
        {
            throw new ApiException(400, "invalid_request", "after must be an event ULID.");
        }

        var cutoff = clock.GetUtcNow().AddDays(-30);
        var rows = await data.ReadAsync((db, token) => db.WebhookOutbox
            .Where(row => row.ProjectId == projectId && row.OccurredAt > cutoff && (after == null || after == "" || string.Compare(row.EventId, after) > 0))
            .OrderBy(row => row.EventId).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new([.. rows.Take(limit).Select(Body)], rows.Count > limit ? rows[limit - 1].EventId : null);
    }

    public async Task<WebhookDeliveryPage> DeliveriesAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        await this.EndpointAsync(projectId, endpointId, cancellationToken).ConfigureAwait(false);
        var rows = await data.ReadAsync((db, token) => db.WebhookDeliveries.Where(row => row.ProjectId == projectId && row.EndpointId == endpointId)
            .OrderByDescending(row => row.Id).Take(20).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        List<WebhookDeliveryBody> items = [];
        foreach (var row in rows)
        {
            var attempts = await data.ReadAsync((db, token) => db.WebhookAttempts.Where(attempt => attempt.DeliveryId == row.Id && attempt.ProjectId == projectId)
                .OrderByDescending(attempt => attempt.Id).Take(10).ToListAsync(token), cancellationToken).ConfigureAwait(false);
            items.Add(new(row.Id, row.EventId, row.Status, row.Attempts, row.NextAttemptAt, attempts));
        }

        return new(items, null);
    }

    public async Task ReplayAsync(string projectId, string endpointId, string deliveryId, CancellationToken cancellationToken)
    {
        var endpoint = await this.EndpointAsync(projectId, endpointId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (endpoint.Value.DisabledAt is not null)
        {
            throw new ApiException(409, "webhook_endpoint_disabled", "Register a working endpoint before sending more events.");
        }

        var row = await data.ReadAsync((db, token) => db.WebhookDeliveries
            .Where(row => row.Id == deliveryId && row.ProjectId == projectId && row.EndpointId == endpointId).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false)
            ?? throw new ApiException(404, "not_found", "No such delivery.");
        if (endpoint.Value.LeaseUntil > now || row.Value.Status == "sending")
        {
            throw new ApiException(409, "delivery_in_progress", "The endpoint is delivering an event.");
        }

        if (!await data.SaveAsync([Change.Assert(endpoint), Change.Update(row, row.Value with { Status = "pending", NextAttemptAt = now, StartedAt = now, CycleAttempts = 0 })], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The delivery changed concurrently.");
        }
    }

    public async Task<WebhookEventBody> TestAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        var endpoint = await this.EndpointAsync(projectId, endpointId, cancellationToken).ConfigureAwait(false);
        if (endpoint.Value.DisabledAt is not null)
        {
            throw new ApiException(409, "webhook_endpoint_disabled", "Register a working endpoint before sending more events.");
        }

        var row = outbox.NewEvent(projectId, "webhook.test", endpointId, endpointId: endpointId) with { Status = "expanded" };
        if (!await data.SaveAsync([Change.Assert(endpoint), Change.Insert(row), Change.Insert(NewDelivery(row, endpointId))], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The endpoint changed concurrently.");
        }

        return Body(row);
    }

    internal static WebhookDeliveryRecord NewDelivery(WebhookOutboxRecord row, string endpointId) =>
        new(AuthIds.New(), row.ProjectId, endpointId, row.EventId, "pending", 0, row.OccurredAt, row.OccurredAt);

    internal async Task<Versioned<WebhookEndpointRecord>> EndpointAsync(string projectId, string endpointId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.WebhookEndpoints.Where(row => row.ProjectId == projectId && row.Id == endpointId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false)
        ?? throw new ApiException(404, "not_found", "No such endpoint.");
}
