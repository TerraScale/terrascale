using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed partial class WebhookDeliveryWorker(AuthData data, AuthSchema schema, IHttpClientFactory clients,
    AuthOptions options, TimeProvider clock, ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        var pruneAt = clock.GetUtcNow();
        do
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

                await this.DisableFailedAsync(stoppingToken).ConfigureAwait(false);
                await this.ExpandAsync(stoppingToken).ConfigureAwait(false);
                var now = clock.GetUtcNow();
                var due = await data.ReadAsync((db, token) => db.WebhookDeliveries
                    .Where(row => (row.Status == "pending" || row.Status == "sending") && row.NextAttemptAt <= now)
                    .OrderBy(row => row.NextAttemptAt).Take(50).WithVersion().ToListAsync(token), stoppingToken).ConfigureAwait(false);
                await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken },
                    async (row, token) => await this.DeliverAsync(row, token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
            {
                DeliveryPassFailed(logger, failure.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task DisableFailedAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var cutoff = now.AddDays(-5);
        var endpoints = await data.ReadAsync((db, token) => db.WebhookEndpoints
            .Where(row => row.DisabledAt == null && row.FirstFailureAt <= cutoff && row.LeaseUntil <= now)
            .Take(50).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var endpoint in endpoints)
        {
            var row = endpoint.Value;
            await data.SaveAsync([
                Change.Update(endpoint, row with { DisabledAt = now }),
                Change.Insert(new WebhookOutboxRecord(row.ProjectId, row.ProjectId, "webhook.endpoint_disabled", AuthIds.New(), now,
                    row.Id, null, null, "pending", 0, now, EndpointId: row.Id)),
            ], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExpandAsync(CancellationToken cancellationToken)
    {
        var events = await data.ReadAsync((db, token) => db.WebhookOutbox.Where(row => row.Status == "pending")
            .OrderBy(row => row.EventId).Take(100).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var row in events)
        {
            var endpoints = await data.ReadAsync((db, token) => db.WebhookEndpoints
                .Where(endpoint => endpoint.ProjectId == row.Value.ProjectId && endpoint.DisabledAt == null && endpoint.CreatedAt <= row.Value.OccurredAt)
                .ToListAsync(token), cancellationToken).ConfigureAwait(false);
            List<Change> changes = [Change.Update(row, row.Value with { Status = "expanded" })];
            changes.AddRange(endpoints.Where(endpoint => endpoint.EventTypes.Contains(row.Value.EventType))
                .Select(endpoint => Change.Insert(WebhookHistory.NewDelivery(row.Value, endpoint.Id))));
            await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeliverAsync(Versioned<WebhookDeliveryRecord> current, CancellationToken cancellationToken)
    {
        var delivery = current.Value;
        var endpoint = await data.ReadAsync((db, token) => db.WebhookEndpoints.Where(row => row.Id == delivery.EndpointId && row.ProjectId == delivery.ProjectId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (endpoint is null || endpoint.Value.LeaseUntil > now)
        {
            return;
        }

        if (endpoint.Value.DisabledAt is not null || now >= delivery.StartedAt.AddHours(24))
        {
            await data.SaveAsync(Change.Update(current, delivery with { Status = endpoint.Value.DisabledAt is null ? "exhausted" : "disabled" }), cancellationToken).ConfigureAwait(false);
            return;
        }

        var evt = await data.ReadAsync((db, token) => db.WebhookOutbox.SingleOrDefaultAsync(row => row.EventId == delivery.EventId && row.ProjectId == delivery.ProjectId, token), cancellationToken).ConfigureAwait(false);
        if (evt is null)
        {
            return;
        }

        var leaseUntil = now.AddMinutes(2);
        var sending = delivery with { Status = "sending", Attempts = delivery.Attempts + 1, CycleAttempts = delivery.CycleAttempts + 1, NextAttemptAt = leaseUntil };
        var claim = Change.Update(current, sending);
        var attempt = new WebhookAttemptRecord(AuthIds.New(), delivery.Id, delivery.ProjectId, now, "sending", 0, null, null);
        List<Change> claims = [claim, Change.Update(endpoint, endpoint.Value with { LeaseUntil = leaseUntil }), Change.Insert(attempt)];
        var interrupted = await data.ReadAsync((db, token) => db.WebhookAttempts.Where(row => row.DeliveryId == delivery.Id && row.Status == "sending")
            .WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        claims.AddRange(interrupted.Select(row => Change.Update(row, row.Value with { Status = "interrupted" })));
        if (!await data.SaveAsync(claims, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var started = clock.GetTimestamp();
        var result = await this.SendAsync(endpoint.Value, evt, cancellationToken).ConfigureAwait(false);
        var finishedAt = clock.GetUtcNow();
        var latest = await data.ReadAsync((db, token) => db.WebhookEndpoints.Where(row => row.Id == endpoint.Value.Id && row.ProjectId == delivery.ProjectId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Value.LeaseUntil != leaseUntil)
        {
            return;
        }

        DateTimeOffset? failedSince = result.Success ? null : latest.Value.FirstFailureAt ?? now;
        var disabledAt = failedSince <= finishedAt.AddDays(-5) ? finishedAt : latest.Value.DisabledAt;
        var next = finishedAt + TimeSpan.FromSeconds(Math.Min(21600, 5 * Math.Pow(2, Math.Min(sending.CycleAttempts - 1, 13))) * (0.5 + Random.Shared.NextDouble()));
        var status = result.Success ? "delivered" : disabledAt is not null ? "disabled" : next >= delivery.StartedAt.AddHours(24) ? "exhausted" : "pending";
        var completed = attempt with { Status = result.Success ? "delivered" : "failed", LatencyMs = (int)clock.GetElapsedTime(started).TotalMilliseconds, ResponseCode = result.Code, ResponseBody = result.Body };
        var storedAttempt = await data.ReadAsync((db, token) => db.WebhookAttempts.Where(row => row.Id == attempt.Id).WithVersion().SingleAsync(token), cancellationToken).ConfigureAwait(false);
        List<Change> changes =
        [
            Change.Update(sending with { Status = status, NextAttemptAt = next }, claim.Version),
            Change.Update(storedAttempt, completed),
            Change.Update(latest, latest.Value with { LeaseUntil = finishedAt, FirstFailureAt = failedSince, DisabledAt = disabledAt }),
        ];
        if (disabledAt is not null && latest.Value.DisabledAt is null)
        {
            changes.Add(Change.Insert(new WebhookOutboxRecord(delivery.ProjectId, delivery.ProjectId, "webhook.endpoint_disabled", AuthIds.New(), finishedAt,
                endpoint.Value.Id, null, null, "pending", 0, finishedAt, EndpointId: endpoint.Value.Id)));
        }

        if (!await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
        {
            DeliveryCompletionConflict(logger, delivery.Id);
        }
    }

    private async Task<DeliveryResult> SendAsync(WebhookEndpointRecord endpoint, WebhookOutboxRecord evt, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(WebhookHistory.Body(evt), WebhooksJson.Default.WebhookEventBody);
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var secret = this.Open(endpoint, endpoint.SecretBox);
        var previous = endpoint.PreviousSecretBox is { } box && endpoint.PreviousSecretExpiresAt > clock.GetUtcNow() ? this.Open(endpoint, box) : null;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url);
        request.Headers.Add("webhook-id", evt.EventId);
        request.Headers.Add("webhook-timestamp", timestamp);
        var signed = $"{evt.EventId}.{timestamp}.{body}";
        request.Headers.Add("webhook-signature", Sign(secret, signed) + (previous is null ? "" : " " + Sign(previous, signed)));
        request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = clients.CreateClient(WebhookHttp.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[1024];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token).ConfigureAwait(false);
            var snippet = Encoding.UTF8.GetString(bytes, 0, count).Replace(secret, "[redacted]", StringComparison.Ordinal);
            if (previous is not null)
            {
                snippet = snippet.Replace(previous, "[redacted]", StringComparison.Ordinal);
            }

            return new(response.IsSuccessStatusCode, (int)response.StatusCode, snippet);
        }
        catch (HttpRequestException)
        {
            return new(false, null, null);
        }
        catch (IOException)
        {
            return new(false, null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, null);
        }
    }

    private string Open(WebhookEndpointRecord endpoint, string box) =>
        Encoding.UTF8.GetString(SecretBox.Open(options.MasterKey, box, WebhookEndpoints.Context(endpoint.ProjectId, endpoint.Id)));

    private static string Sign(string secret, string body) =>
        "v1," + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(secret[6..]), Encoding.UTF8.GetBytes(body)));

    private sealed record DeliveryResult(bool Success, int? Code, string? Body);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook delivery pass failed ({FailureType}).")]
    private static partial void DeliveryPassFailed(ILogger logger, string failureType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} changed before completion was recorded.")]
    private static partial void DeliveryCompletionConflict(ILogger logger, string deliveryId);
}
