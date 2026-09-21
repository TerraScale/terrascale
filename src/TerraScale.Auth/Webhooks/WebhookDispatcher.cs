namespace TerraScale.Auth.Webhooks;

using System.Collections.Concurrent;
using System.Text;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;

/// <summary>Delivers audit events to matching enabled webhook endpoints:
/// HMAC-signed payloads, at-least-once, with durable checkpoints and bounded retries. One instance per
/// application, started from <see cref="WebhooksModule.Start"/> (App.cs is
/// shared, so there is no hosted-service registration).</summary>
public sealed class WebhookDispatcher(AuthStore store, ILogger<WebhookDispatcher> logger, byte[] masterKey)
{
    private const int PollIntervalSeconds = 2;
    private const int MaxAttempts = 3;
    private const int BatchSize = 200;

    private static readonly HttpClient Http = new(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ConcurrentDictionary<string, long> cursors = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> delivered = new();
    // Lazy so a registration race cannot start two poll loops (a discarded Lazy never runs).
    private readonly ConcurrentDictionary<string, Lazy<Task>> pollers = new();

    /// <summary>Registers an instance and restores its durable delivery cursor.
    /// A first sighting starts at now so endpoint creation does not backfill old
    /// audit events; subsequent processes resume from the persisted checkpoint.</summary>
    public async Task EnsureInstanceAsync(string instanceId, CancellationToken ct)
    {
        if (cursors.ContainsKey(instanceId))
            return;

        var cursor = await store.GetWebhookCursorAsync(instanceId, ct);
        var position = cursor?.LastCreatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (cursors.TryAdd(instanceId, position) && cursor is null)
        {
            await store.UpsertWebhookCursorAsync(new WebhookCursor
            {
                Id = instanceId,
                InstanceId = instanceId,
                LastCreatedAt = position,
                LastEventId = string.Empty,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
        }
    }

    /// <summary>The supervisor: discovers instances and starts one poll loop per
    /// instance; runs until <paramref name="stopping"/> fires and never throws.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        var discovered = false;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (!discovered)
                {
                    // Restart semantics: instances that already hold webhook endpoints must be
                    // re-registered, or a restart silently stops all deliveries. New instances
                    // start at "now"; existing instances restore their durable checkpoint.
                    foreach (var instanceId in await store.ListWebhookInstanceIdsAsync(stopping))
                        await EnsureInstanceAsync(instanceId, stopping);
                    discovered = true;
                }
                // One loop per instance, each isolated: a stalled endpoint (timeouts x
                // retries per event) head-of-line blocks only its own instance's later
                // deliveries, never another instance's.
                foreach (var instanceId in cursors.Keys)
                    _ = pollers.GetOrAdd(instanceId, id => new Lazy<Task>(() => PollInstanceAsync(id, stopping))).Value;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "webhook dispatch supervision failed");
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

    /// <summary>Replays one dead delivery after an operator has corrected the endpoint.</summary>
    public async Task<bool> ReplayAsync(string instanceId, string deliveryId, CancellationToken ct)
    {
        var delivery = await store.GetWebhookDeliveryAsync(deliveryId, ct);
        if (delivery is null || delivery.InstanceId != instanceId || delivery.Status != "dead")
            return false;
        var endpoint = await store.GetWebhookEndpointAsync(delivery.EndpointId, ct);
        var auditEvent = await store.GetAuditEventAsync(delivery.EventId, ct);
        if (endpoint is null || endpoint.InstanceId != instanceId || auditEvent is null)
            return false;
        await store.UpsertWebhookDeliveryAsync(delivery with { Status = "pending", Attempts = 0, LastError = null }, ct);
        return await DeliverAsync(endpoint, auditEvent, ct);
    }

    /// <summary>Per-instance poll loop: tick, sleep, repeat. Errors (including engine
    /// outages) are logged and retried on the next interval; the cursor advances only
    /// after a fully settled tick, so nothing is skipped.</summary>
    private async Task PollInstanceAsync(string instanceId, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await TickInstanceAsync(instanceId, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "webhook dispatch tick failed for {InstanceId}", instanceId);
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

    private async Task TickInstanceAsync(string instanceId, CancellationToken ct)
    {
        var endpoints = await store.ListWebhookEndpointsAsync(instanceId, ct);
        var enabled = endpoints.Where(e => e.Enabled).ToList();
        if (enabled.Count == 0)
            return;

        var events = await store.ListAuditEventsSinceAsync(instanceId, cursors[instanceId], BatchSize, ct);
        // Audit events only have unix-second timestamps, so the cursor can land in the
        // middle of a busy second; the delivered-id set dedupes those ties.
        var seen = delivered.GetOrAdd(instanceId, static _ => []);
        var fresh = events.Where(e => !seen.Contains(e.Id)).ToList();
        if (fresh.Count == 0)
            return;

        // Endpoints deliver concurrently (FIFO order preserved per endpoint): one
        // stalled endpoint must not delay the instance's other endpoints either.
        var completed = await Task.WhenAll(enabled.Select(endpoint => DeliverMatchingAsync(endpoint, fresh, ct)));
        if (completed.Any(success => !success))
            return;

        var checkpoint = fresh[^1];
        foreach (var auditEvent in fresh)
            seen.Add(auditEvent.Id);
        cursors[instanceId] = checkpoint.CreatedAt;
        await store.UpsertWebhookCursorAsync(new WebhookCursor
        {
            Id = instanceId,
            InstanceId = instanceId,
            LastCreatedAt = checkpoint.CreatedAt,
            LastEventId = checkpoint.Id,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        // ponytail: delivered grows per instance unbounded; clear at 1000 (at-least-once tolerates the rare redelivery of a tie).
        if (seen.Count > 1000)
            seen.Clear();
    }

    /// <summary>Delivers the batch to one endpoint, preserving per-endpoint event order.</summary>
    private async Task<bool> DeliverMatchingAsync(WebhookEndpoint endpoint, IReadOnlyList<Store.AuditEvent> events, CancellationToken ct)
    {
        var success = true;
        foreach (var auditEvent in events)
            if (Matches(endpoint.Events, auditEvent.Type))
                success &= await DeliverAsync(endpoint, auditEvent, ct);
        return success;
    }

    private async Task<bool> DeliverAsync(WebhookEndpoint endpoint, Store.AuditEvent auditEvent, CancellationToken ct)
    {
        var deliveryId = $"{endpoint.Id}:{auditEvent.Id}";
        var delivery = await store.GetWebhookDeliveryAsync(deliveryId, ct);
        if (delivery is { Status: "succeeded" or "dead" })
            return true;

        var payload = JsonSerializer.Serialize(new
        {
            id = auditEvent.Id,
            type = auditEvent.Type,
            created_at = auditEvent.CreatedAt,
            data = auditEvent.Data,
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var secret = Encoding.UTF8.GetString(SecretBox.Decrypt(masterKey, endpoint.SigningSecretBox));
        var signature = Sign(secret, timestamp, payload);

        var firstAttempt = (delivery?.Attempts ?? 0) + 1;
        string? lastError = delivery?.LastError;
        for (var attempt = firstAttempt; ; attempt++)
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
                {
                    await store.UpsertWebhookDeliveryAsync(new WebhookDelivery
                    {
                        Id = deliveryId,
                        InstanceId = endpoint.InstanceId,
                        EndpointId = endpoint.Id,
                        EventId = auditEvent.Id,
                        Status = "succeeded",
                        Attempts = attempt,
                        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    }, ct);
                    return true;
                }
                logger.LogWarning("webhook {EndpointId} delivery of {EventType} returned {Status}", endpoint.Id, auditEvent.Type, (int)response.StatusCode);
                lastError = $"HTTP {(int)response.StatusCode}";
            }
            // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException
            // subclass) — those must retry like any transport error; only genuine token
            // cancellation escapes, or one timeout would abort the whole tick and silently
            // drop the event (its id is already marked delivered) for every endpoint.
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "webhook {EndpointId} delivery of {EventType} failed", endpoint.Id, auditEvent.Type);
                lastError = exception.Message;
            }

            await store.UpsertWebhookDeliveryAsync(new WebhookDelivery
            {
                Id = deliveryId,
                InstanceId = endpoint.InstanceId,
                EndpointId = endpoint.Id,
                EventId = auditEvent.Id,
                Status = attempt >= MaxAttempts ? "dead" : "pending",
                Attempts = attempt,
                LastError = lastError,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);

            if (attempt >= MaxAttempts)
            {
                logger.LogError("webhook {EndpointId} gave up delivering {EventType} after {Attempts} attempts", endpoint.Id, auditEvent.Type, attempt);
                return false;
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
