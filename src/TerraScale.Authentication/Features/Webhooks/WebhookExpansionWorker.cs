using Dapper;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed partial class WebhookExpansionWorker(AuthData data, AuthSchema schema, TimeProvider clock,
    WebhookNotifications notifications, ILogger<WebhookExpansionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        var pruneAt = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (clock.GetUtcNow() >= pruneAt)
                {
                    var cutoff = clock.GetUtcNow().AddDays(-30);
                    await data.InBatchesAsync(100, connection => connection.ExecuteAsync(
                        "DELETE FROM webhook_outbox WHERE event_id IN (SELECT event_id FROM webhook_outbox WHERE occurred_at <= @cutoff ORDER BY occurred_at LIMIT 100)",
                        new { cutoff }), stoppingToken);
                    pruneAt = clock.GetUtcNow().AddHours(1);
                }

                await notifications.SendAsync(stoppingToken);
                var events = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<WebhookOutboxRecord>(
                    "SELECT * FROM webhook_outbox WHERE status = 'pending' ORDER BY event_id LIMIT 100"), stoppingToken));
                foreach (var row in events)
                {
                    var (projectId, occurredAt) = (row.Value.ProjectId, row.Value.OccurredAt);
                    var endpoints = await data.ReadAsync(connection => connection.QueryAsync<WebhookEndpointRecord>(
                        """
                        SELECT * FROM webhook_endpoints e WHERE e.project_id = @projectId AND e.created_at <= @occurredAt
                            AND EXISTS (SELECT 1 FROM webhook_endpoint_states s WHERE s.endpoint_id = e.id AND s.disabled_at IS NULL)
                        """, new { projectId, occurredAt }), stoppingToken);
                    List<Change> changes = [Change.Update(row, row.Value with { Status = "expanded" })];
                    changes.AddRange(endpoints.Where(endpoint => endpoint.EventTypes.Contains(row.Value.EventType))
                        .Select(endpoint => Change.Insert(WebhookHistory.NewDelivery(row.Value, endpoint.Id))));
                    await data.SaveAsync(changes, stoppingToken);
                }

                if (events.Count > 0)
                {
                    continue;
                }
            }
            catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
            {
                ExpansionFailed(logger, failure.GetType().Name);
            }
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook expansion failed ({FailureType}).")]
    private static partial void ExpansionFailed(ILogger logger, string failureType);
}
