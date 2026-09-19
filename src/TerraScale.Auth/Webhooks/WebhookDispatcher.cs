namespace TerraScale.Auth.Webhooks;

using System.Collections.Concurrent;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;

/// <summary>Delivers audit events to matching enabled webhook endpoints:
/// HMAC-signed payloads, at-least-once, with bounded retries. One instance per
/// application, started from <see cref="WebhookEndpointsModule.Map"/> (App.cs is
/// shared, so there is no hosted-service registration).</summary>
public sealed class WebhookDispatcher(AuthStore store, ILogger<WebhookDispatcher> logger)
{
    private const int PollIntervalSeconds = 2;
    private const int MaxAttempts = 3;
    private const int BatchSize = 200;

    private static readonly HttpClient Http = new(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(10) };

    // ponytail: in-memory cursors — persist them once exactly-once delivery matters.
    private readonly ConcurrentDictionary<string, long> cursors = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> delivered = new();

    /// <summary>Registers an instance with the dispatcher. The cursor starts at
    /// "now", so events that predate the first sighting are never backfilled.</summary>
    public void EnsureInstance(string instanceId) =>
        cursors.GetOrAdd(instanceId, static _ => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>The poll loop; runs until <paramref name="stopping"/> fires and never throws.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "webhook dispatch tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), stopping);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        foreach (var (instanceId, cursor) in cursors)
        {
            var endpoints = await store.ListWebhookEndpointsAsync(instanceId, ct);
            var enabled = endpoints.Where(e => e.Enabled).ToList();
            if (enabled.Count == 0)
                continue;

            var events = await store.ListAuditEventsSinceAsync(instanceId, cursor, BatchSize, ct);
            // Audit events only have unix-second timestamps, so the cursor can land in the
            // middle of a busy second; the delivered-id set dedupes those ties.
            var seen = delivered.GetOrAdd(instanceId, static _ => []);
            var fresh = events.Where(e => seen.Add(e.Id)).ToList();
            if (fresh.Count == 0)
                continue;

            foreach (var auditEvent in fresh)
                foreach (var endpoint in enabled)
                    if (Matches(endpoint.Events, auditEvent.Type))
                        await DeliverAsync(endpoint, auditEvent, ct);

            cursors[instanceId] = fresh.Max(e => e.CreatedAt);
            // ponytail: delivered grows per instance unbounded; clear at 1000 (at-least-once tolerates the rare redelivery of a tie).
            if (seen.Count > 1000)
                seen.Clear();
        }
    }

    private async Task DeliverAsync(WebhookEndpoint endpoint, Store.AuditEvent auditEvent, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = auditEvent.Id,
            type = auditEvent.Type,
            created_at = auditEvent.CreatedAt,
            data = auditEvent.Data,
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = Sign(endpoint.SigningSecret, timestamp, payload);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
                {
                    Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-TsAuth-Event", auditEvent.Type);
                request.Headers.Add("X-TsAuth-Timestamp", timestamp);
                request.Headers.Add("X-TsAuth-Signature", signature);

                using var response = await Http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    return;
                logger.LogWarning("webhook {EndpointId} delivery of {EventType} returned {Status}", endpoint.Id, auditEvent.Type, (int)response.StatusCode);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "webhook {EndpointId} delivery of {EventType} failed", endpoint.Id, auditEvent.Type);
            }

            if (attempt >= MaxAttempts)
            {
                logger.LogError("webhook {EndpointId} gave up delivering {EventType} after {Attempts} attempts", endpoint.Id, auditEvent.Type, attempt);
                return;
            }
            await Task.Delay(attempt == 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(4), ct);
        }
    }

    /// <summary>True when the event filter matches: exact type, a "prefix.*" wildcard, or "*".</summary>
    internal static bool Matches(string[] events, string type) =>
        events.Contains(type) || events.Contains("*")
        || events.Any(e => e.EndsWith(".*", StringComparison.Ordinal) && type.StartsWith(e[..^1], StringComparison.Ordinal));

    /// <summary>"sha256=" + hex(HMACSHA256(secret, timestamp + "." + payloadBody)).</summary>
    internal static string Sign(string secret, string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Encodings.HexEncode(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(timestamp + "." + payload)));
    }
}
