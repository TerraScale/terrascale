using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A registered collection change webhook. Listings never contain the signing secret.</summary>
/// <param name="Id">Canonical webhook ULID.</param>
/// <param name="Url">HTTPS receiver URL.</param>
/// <param name="CreatedAt">Registration time.</param>
/// <param name="Status">active or feed_gap; a gap requires reconciliation and recreation.</param>
public sealed record TerraBaseChangeWebhook(string Id, string Url, DateTimeOffset CreatedAt, string Status)
{
    /// <summary>Canonical webhook ULID.</summary>
    public string Id { get; init; } = TerraScaleIds.IsValid(Id) ? Id : throw new JsonException("Invalid webhook ULID.");
}

/// <summary>A newly registered webhook. Store Secret now: it cannot be retrieved again.</summary>
/// <param name="Id">Canonical webhook ULID.</param>
/// <param name="Url">HTTPS receiver URL.</param>
/// <param name="CreatedAt">Registration time.</param>
/// <param name="Status">active.</param>
/// <param name="Secret">Base64url signing key; decode to bytes before HMAC verification. Never log it.</param>
public sealed record TerraBaseCreatedChangeWebhook(string Id, string Url, DateTimeOffset CreatedAt, string Status, string Secret)
{
    /// <summary>Canonical webhook ULID.</summary>
    public string Id { get; init; } = TerraScaleIds.IsValid(Id) ? Id : throw new JsonException("Invalid webhook ULID.");

    /// <summary>Omits the one-time signing secret from diagnostics.</summary>
    public override string ToString() => $"TerraBaseCreatedChangeWebhook {{ Id = {Id}, Secret = [REDACTED] }}";
}

/// <summary>One of the latest 100 retained delivery records; contains no receiver body or signing material.</summary>
/// <param name="Id">Canonical delivery ULID, stable across retries.</param>
/// <param name="CreatedAt">Capture time.</param>
/// <param name="Status">pending or delivered.</param>
/// <param name="Attempts">Number of attempts.</param>
/// <param name="LastAttemptAt">Last attempt, or null.</param>
/// <param name="NextAttemptAt">Next scheduled attempt, or null.</param>
/// <param name="HttpStatus">Receiver HTTP status, or null.</param>
/// <param name="ErrorCode">null, http_error, transport_error or target_rejected.</param>
public sealed record TerraBaseChangeWebhookDelivery(string Id, DateTimeOffset CreatedAt, string Status, long Attempts,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, int? HttpStatus, string? ErrorCode)
{
    /// <summary>Canonical delivery ULID.</summary>
    public string Id { get; init; } = TerraScaleIds.IsValid(Id) ? Id : throw new JsonException("Invalid delivery ULID.");
}

internal sealed record TerraBaseCreateChangeWebhookRequest(string Url);

public sealed partial class TerraBaseDatabase
{
    /// <summary>Registers an HTTPS receiver at the current feed head. Requires documents:read and documents:write; sent once. Store the returned secret immediately.</summary>
    public async Task<TerraBaseCreatedChangeWebhook> CreateChangeWebhookAsync(string collection, string url, CancellationToken cancellationToken = default)
    {
        if (url is not { Length: > 0 and <= 2048 } || !Uri.TryCreate(url, UriKind.Absolute, out var receiver)
            || receiver.Scheme != Uri.UriSchemeHttps || receiver.Port != 443 || receiver.UserInfo.Length != 0 || receiver.Fragment.Length != 0)
            throw new ArgumentException("Webhook URL must be HTTPS on port 443, without credentials or a fragment, at most 2048 characters.", nameof(url));
        return (await connection.SendAsync(HttpMethod.Post, ChangeWebhooksPath(collection),
            TerraBaseConnection.Json(new TerraBaseCreateChangeWebhookRequest(url), Json.TerraBaseCreateChangeWebhookRequest),
            Json.TerraBaseCreatedChangeWebhook, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Lists webhook metadata by ascending ID. Requires documents:read. Limit 1–100, default 50; continue with the final ID until an empty list.</summary>
    public async Task<IReadOnlyList<TerraBaseChangeWebhook>> ListChangeWebhooksAsync(string collection, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, ChangeWebhooksPath(collection) + ChangeWebhookQuery(after, limit),
            null, Json.IReadOnlyListTerraBaseChangeWebhook, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Stops future attempts. Requires documents:read and documents:write; sent once. An in-flight delivery may still complete; unknown IDs return 404.</summary>
    public Task DeleteChangeWebhookAsync(string collection, string webhookId, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, ChangeWebhooksPath(collection) + "/" + TerraScaleIds.Validate(webhookId), null, null, cancellationToken);

    /// <summary>Lists retained delivery records by ascending ID. Requires documents:read. Limit 1–100, default 50; continue with the final ID until an empty list.</summary>
    public async Task<IReadOnlyList<TerraBaseChangeWebhookDelivery>> ListChangeWebhookDeliveriesAsync(string collection, string webhookId,
        string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, ChangeWebhooksPath(collection) + "/" + TerraScaleIds.Validate(webhookId) + "/deliveries" + ChangeWebhookQuery(after, limit),
            null, Json.IReadOnlyListTerraBaseChangeWebhookDelivery, null, cancellationToken).ConfigureAwait(false)).Value;

    private string ChangeWebhooksPath(string collection) => CollectionPath(collection) + "/webhooks";

    private static string ChangeWebhookQuery(string? after, int? limit)
    {
        if (after is not null) TerraScaleIds.Validate(after);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Webhook page limit must be 1–100.");
        return TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit)));
    }
}
