using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed partial class WebhookDeliveryWorker(AuthData data, AuthSchema schema, IHttpClientFactory clients,
    AuthOptions options, TimeProvider clock, ILogger<WebhookDeliveryWorker> logger, WebhookNotifications notifications) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => this.RunLaneAsync(stoppingToken)));
    }

    private async Task RunLaneAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                var endpoints = (await data.ReadAsync(connection => connection.QueryAsync<string>(
                    """
                    SELECT s.endpoint_id FROM webhook_endpoint_states s
                    WHERE s.disabled_at IS NULL AND s.lease_until <= @now
                        AND EXISTS (SELECT 1 FROM webhook_deliveries d WHERE d.endpoint_id = s.endpoint_id AND d.status IN ('pending', 'sending') AND d.next_attempt_at <= @now)
                    ORDER BY (SELECT max(p.last_served_at) FROM webhook_endpoint_states p WHERE p.project_id = s.project_id), s.project_id, s.last_served_at, s.endpoint_id
                    LIMIT 1
                    """, new { now }), cancellationToken)).AsList();
                if (endpoints.Count == 0)
                {
                    await timer.WaitForNextTickAsync(cancellationToken);
                    continue;
                }

                foreach (var endpointId in endpoints)
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                    for (var count = 0; count < 10 && !budget.IsCancellationRequested; count++)
                    {
                        now = clock.GetUtcNow();
                        var current = Versioned.Of(await data.ReadAsync(connection => connection.QueryFirstOrDefaultAsync<WebhookDeliveryRecord>(
                            """
                            SELECT * FROM webhook_deliveries WHERE endpoint_id = @endpointId AND status IN ('pending', 'sending') AND next_attempt_at <= @now
                            ORDER BY event_id, id LIMIT 1
                            """, new { endpointId, now }), cancellationToken));
                        if (current is null || !await this.DeliverAsync(current, budget.Token, cancellationToken))
                        {
                            break;
                        }
                    }
                }

                continue;
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                DeliveryPassFailed(logger, failure.GetType().Name);
            }
            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    private async Task<bool> DeliverAsync(Versioned<WebhookDeliveryRecord> current, CancellationToken budgetToken, CancellationToken cancellationToken)
    {
        var delivery = current.Value;
        var (endpointId, projectId, eventId) = (delivery.EndpointId, delivery.ProjectId, delivery.EventId);
        var state = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WebhookEndpointStateRecord>(
            "SELECT * FROM webhook_endpoint_states WHERE endpoint_id = @endpointId AND project_id = @projectId", new { endpointId, projectId }), cancellationToken));
        var endpoint = state is null ? null : await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WebhookEndpointRecord>(
            "SELECT * FROM webhook_endpoints WHERE id = @endpointId AND project_id = @projectId", new { endpointId, projectId }), cancellationToken);
        var evt = endpoint is null ? null : await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WebhookOutboxRecord>(
            "SELECT * FROM webhook_outbox WHERE event_id = @eventId AND project_id = @projectId", new { eventId, projectId }), cancellationToken);
        var now = clock.GetUtcNow();
        if (state is null || endpoint is null || evt is null || state.Value.LeaseUntil > now)
        {
            return false;
        }
        if (state.Value.DisabledAt is not null)
        {
            await data.SaveAsync(Change.Update(current, delivery with { Status = "disabled" }), cancellationToken);
            return false;
        }

        var leaseUntil = now.AddMinutes(2);
        var cycleStartedAt = delivery.CycleAttempts == 0 ? now : delivery.StartedAt;
        var sending = delivery with { StartedAt = cycleStartedAt, Status = "sending", Attempts = delivery.Attempts + 1, CycleAttempts = delivery.CycleAttempts + 1, NextAttemptAt = leaseUntil };
        var claim = Change.Update(current, sending);
        var attempt = new WebhookAttemptRecord(AuthIds.New(), delivery.Id, delivery.ProjectId, now, "sending", 0, null, null);
        var leased = state.Value with { LeaseUntil = leaseUntil, LastServedAt = now };
        var stateClaim = Change.Update(state, leased);
        var attemptClaim = Change.Insert(attempt);
        List<Change> claims = [claim, stateClaim, attemptClaim];
        if (delivery.Status == "sending")
        {
            var deliveryId = delivery.Id;
            var interrupted = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<WebhookAttemptRecord>(
                "SELECT * FROM webhook_attempts WHERE delivery_id = @deliveryId AND status = 'sending'", new { deliveryId }), cancellationToken));
            claims.AddRange(interrupted.Select(row => Change.Update(row, row.Value with { Status = "interrupted" })));
        }
        if (!await data.SaveAsync(claims, cancellationToken))
        {
            return false;
        }

        var started = clock.GetTimestamp();
        var result = await this.SendAsync(endpoint, evt, budgetToken, cancellationToken);
        var finishedAt = clock.GetUtcNow();
        var latencyMs = (int)clock.GetElapsedTime(started).TotalMilliseconds;
        WebhookAttemptRecord? previousAttempt = null;
        if (!result.Success)
        {
            var attemptId = attempt.Id;
            previousAttempt = await data.ReadAsync(connection => connection.QueryFirstOrDefaultAsync<WebhookAttemptRecord>(
                """
                SELECT a.* FROM webhook_deliveries AS d
                JOIN webhook_attempts AS a ON a.delivery_id = d.id
                WHERE d.endpoint_id = @endpointId AND d.project_id = @projectId
                  AND a.id = (
                    SELECT max(previous.id) FROM webhook_attempts AS previous
                    WHERE previous.delivery_id = d.id AND previous.id <> @attemptId)
                ORDER BY a.id DESC
                LIMIT 1
                """, new { endpointId, projectId, attemptId }), cancellationToken);
        }

        DateTimeOffset? failedSince = result.Success ? null : (previousAttempt is { Status: "failed" } && previousAttempt.CreatedAt >= now.AddHours(-10) ? leased.FirstFailureAt : null) ?? now;
        var disabledAt = failedSince <= finishedAt.AddDays(-5) ? finishedAt : leased.DisabledAt;
        var next = finishedAt + TimeSpan.FromSeconds(Math.Min(21600, 5 * Math.Pow(2, Math.Min(sending.CycleAttempts - 1, 13))) * (0.5 + Random.Shared.NextDouble()));
        next = next > cycleStartedAt.AddHours(24) ? cycleStartedAt.AddHours(24) : next;
        var status = result.Success ? "delivered" : disabledAt is not null ? "disabled" : finishedAt >= cycleStartedAt.AddHours(24) ? "exhausted" : "pending";
        var completed = attempt with { Status = result.Success ? "delivered" : "failed", LatencyMs = latencyMs, ResponseCode = result.Code, ResponseBody = result.Body };
        List<Change> changes =
        [
            Change.Update(sending with { Status = status, NextAttemptAt = next }, claim.Version),
            Change.Update(completed, attemptClaim.Version),
            Change.Update(leased with { LeaseUntil = finishedAt, FirstFailureAt = failedSince, DisabledAt = disabledAt }, stateClaim.Version),
        ];
        if (disabledAt is not null && leased.DisabledAt is null)
        {
            changes.Add(Change.Insert(new WebhookOutboxRecord(delivery.ProjectId, delivery.ProjectId, "webhook.endpoint_disabled", AuthIds.New(), finishedAt,
                "", null, null, "pending", 0, finishedAt, EndpointId: endpoint.Id)));
        }

        if (disabledAt is not null && leased.DisabledAt is null)
        {
            changes.AddRange(await notifications.ReserveAsync(delivery.ProjectId, endpoint.Id, cancellationToken));
        }

        if (!await data.SaveAsync(changes, cancellationToken))
        {
            DeliveryCompletionConflict(logger, delivery.Id);
            return false;
        }

        return result.Success;
    }

    private async Task<DeliveryResult> SendAsync(WebhookEndpointRecord endpoint, WebhookOutboxRecord evt, CancellationToken budgetToken, CancellationToken cancellationToken)
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        int? responseCode = null;
        try
        {
            using var client = clients.CreateClient(WebhookHttp.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            responseCode = (int)response.StatusCode;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[1024];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token);
            var snippet = Encoding.UTF8.GetString(bytes, 0, count).Replace(secret, "[redacted]", StringComparison.Ordinal);
            if (previous is not null)
            {
                snippet = snippet.Replace(previous, "[redacted]", StringComparison.Ordinal);
            }

            return new(response.IsSuccessStatusCode, (int)response.StatusCode, snippet);
        }
        catch (HttpRequestException)
        {
            return new(false, responseCode, null);
        }
        catch (IOException)
        {
            return new(false, responseCode, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, responseCode, null);
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
