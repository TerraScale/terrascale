using MessagePack;

namespace TerraScale.TerraBase.Contracts.Webhooks
{
    /// <summary>A registered collection change webhook. Listings never contain the signing secret.</summary>
    /// <param name="Id">Canonical webhook ULID.</param>
    /// <param name="Url">HTTPS receiver URL.</param>
    /// <param name="CreatedAt">Registration time.</param>
    /// <param name="Status">active or feed_gap; a gap requires reconciliation and recreation.</param>
    [MessagePackObject(true)]
    public sealed record TerraBaseChangeWebhook(string Id, string Url, DateTimeOffset CreatedAt, string Status);

    /// <summary>A newly registered webhook. Store Secret now: it cannot be retrieved again.</summary>
    /// <param name="Id">Canonical webhook ULID.</param>
    /// <param name="Url">HTTPS receiver URL.</param>
    /// <param name="CreatedAt">Registration time.</param>
    /// <param name="Status">active.</param>
    /// <param name="Secret">Base64url signing key; decode to bytes before HMAC verification. Never log it.</param>
    [MessagePackObject(true)]
    public sealed record TerraBaseCreatedChangeWebhook(string Id, string Url, DateTimeOffset CreatedAt, string Status, string Secret)
    {
        /// <summary>Omits the one-time signing secret from diagnostics.</summary>
        public override string ToString()
        {
            return $"TerraBaseCreatedChangeWebhook {{ Id = {Id}, Secret = [REDACTED] }}";
        }
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
    [MessagePackObject(true)]
    public sealed record TerraBaseChangeWebhookDelivery(string Id, DateTimeOffset CreatedAt, string Status, long Attempts,
        DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, int? HttpStatus, string? ErrorCode);

    /// <summary>The REST body that registers a receiver.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCreateChangeWebhookRequest(string Url);
}
