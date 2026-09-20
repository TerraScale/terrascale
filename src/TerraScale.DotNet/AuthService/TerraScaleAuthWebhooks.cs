using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Outbound webhook endpoints: registration, test delivery, and dead-letter
/// inspection with replay.
/// </summary>
public sealed class TerraScaleAuthWebhooksClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Registers a webhook endpoint for https URLs (http only for loopback
    /// hosts). The returned signing secret is shown exactly once and is the
    /// HMAC key for verifying deliveries with
    /// <see cref="TerraScaleAuthWebhookVerifier"/>.
    /// </summary>
    public Task<TerraScaleAuthCreatedWebhook> CreateAsync(
        string url,
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(events);
        return client.SendJsonAsync<TerraScaleAuthCreateWebhook, TerraScaleAuthCreatedWebhook>(
            HttpMethod.Post,
            client.ManagementRoute("/webhooks"),
            new TerraScaleAuthCreateWebhook(url, events),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateWebhook,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreatedWebhook,
            cancellationToken);
    }

    /// <summary>Lists every webhook endpoint; listings never include the signing secret.</summary>
    public Task<IReadOnlyList<TerraScaleAuthWebhook>> ListAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/webhooks"),
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthWebhook,
            cancellationToken);

    /// <summary>Deletes one webhook endpoint.</summary>
    public Task DeleteAsync(string webhookId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(HttpMethod.Delete, WebhookRoute(webhookId), cancellationToken);

    /// <summary>Queues a test event delivery to one endpoint.</summary>
    public Task<TerraScaleAuthWebhookTest> SendTestEventAsync(string webhookId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthEmptyBody, TerraScaleAuthWebhookTest>(
            HttpMethod.Post,
            WebhookRoute(webhookId) + "/test",
            TerraScaleAuthEmptyBody.Instance,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthEmptyBody,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthWebhookTest,
            cancellationToken);

    /// <summary>Lists dead deliveries (endpoint never accepted the event with 2xx), newest update first.</summary>
    public Task<IReadOnlyList<TerraScaleAuthDeadDelivery>> ListDeadLetterAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/webhooks/dead-letter"),
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthDeadDelivery,
            cancellationToken);

    /// <summary>Requeues one dead delivery to its endpoint.</summary>
    public Task<TerraScaleAuthReplayResult> ReplayDeadDeliveryAsync(string deliveryId, CancellationToken cancellationToken = default) =>
        client.PostJsonAsync(
            client.ManagementRoute("/webhooks/dead-letter/" + TerraScaleAuthPaths.Escape(deliveryId) + "/replay"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthReplayResult,
            cancellationToken);

    private string WebhookRoute(string webhookId) =>
        client.ManagementRoute("/webhooks/" + TerraScaleAuthPaths.Escape(webhookId));
}

/// <summary>A freshly created webhook; the one-time signing secret appears only here.</summary>
public sealed record TerraScaleAuthCreatedWebhook(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("events")] IReadOnlyList<string> Events,
    [property: JsonPropertyName("signing_secret")] string SigningSecret)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthCreatedWebhook).Name} {{ Id = {Id}, Url = {Url}, Events = {Events.Count}, SigningSecret = [REDACTED] }}";
}

/// <summary>A registered webhook endpoint; the signing secret is never included.</summary>
public sealed record TerraScaleAuthWebhook(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("events")] IReadOnlyList<string> Events,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>The outcome of queueing a test delivery.</summary>
public sealed record TerraScaleAuthWebhookTest(
    [property: JsonPropertyName("queued")] bool Queued);

/// <summary>One dead delivery: an event whose endpoint never returned 2xx.</summary>
public sealed record TerraScaleAuthDeadDelivery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("endpoint_id")] string EndpointId,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("updated_at")] long UpdatedAt);

/// <summary>The outcome of a dead-delivery replay.</summary>
public sealed record TerraScaleAuthReplayResult(
    [property: JsonPropertyName("replayed")] bool Replayed);

internal sealed record TerraScaleAuthCreateWebhook(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("events")] IReadOnlyList<string> Events);
