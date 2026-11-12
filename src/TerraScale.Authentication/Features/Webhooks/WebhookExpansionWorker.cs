using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed partial class WebhookExpansionWorker(AuthData data, AuthSchema schema, TimeProvider clock,
    WebhookNotifications notifications, ILogger<WebhookExpansionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        var pruneAt = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (clock.GetUtcNow() >= pruneAt)
                {
                    var cutoff = clock.GetUtcNow().AddDays(-30);
                    await data.InBatchesAsync(100, (db, token) => db.WebhookOutbox.Where(row => row.OccurredAt <= cutoff)
                        .OrderBy(row => row.OccurredAt).Take(100).ExecuteDeleteAsync(token), stoppingToken).ConfigureAwait(false);
                    pruneAt = clock.GetUtcNow().AddHours(1);
                }

                await notifications.SendAsync(stoppingToken).ConfigureAwait(false);
                var events = await data.ReadAsync((db, token) => db.WebhookOutbox.Where(row => row.Status == "pending")
                    .OrderBy(row => row.EventId).Take(100).WithVersion().ToListAsync(token), stoppingToken).ConfigureAwait(false);
                foreach (var row in events)
                {
                    var endpoints = await data.ReadAsync((db, token) => db.WebhookEndpoints
                        .Where(endpoint => endpoint.ProjectId == row.Value.ProjectId && endpoint.CreatedAt <= row.Value.OccurredAt
                            && db.WebhookEndpointStates.Any(state => state.EndpointId == endpoint.Id && state.DisabledAt == null))
                        .ToListAsync(token), stoppingToken).ConfigureAwait(false);
                    List<Change> changes = [Change.Update(row, row.Value with { Status = "expanded" })];
                    changes.AddRange(endpoints.Where(endpoint => endpoint.EventTypes.Contains(row.Value.EventType))
                        .Select(endpoint => Change.Insert(WebhookHistory.NewDelivery(row.Value, endpoint.Id))));
                    await data.SaveAsync(changes, stoppingToken).ConfigureAwait(false);
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
            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook expansion failed ({FailureType}).")]
    private static partial void ExpansionFailed(ILogger logger, string failureType);
}
