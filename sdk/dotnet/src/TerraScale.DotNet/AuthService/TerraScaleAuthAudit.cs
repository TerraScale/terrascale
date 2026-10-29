using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Instance audit event listing: a hash-chained event log.</summary>
public sealed class TerraScaleAuthAuditClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Lists audit events newest first. <paramref name="limit"/> defaults to 50
    /// on the server and must be between 1 and 100; there is no cursor.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleAuthAuditEvent>> ListAsync(
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/audit-events") + TerraScaleAuthPaths.Query(limit, null),
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthAuditEvent,
            cancellationToken);
}

/// <summary>
/// One integrity-chained audit event. <c>actor_id</c> is omitted from the wire
/// for system-triggered events, and <c>integrity_signature</c> and
/// <c>integrity_key_fingerprint</c> are null when the audit signing key is
/// unset; both deserialize as null. <c>integrity_hash</c> and
/// <c>integrity_version</c> are always present.
/// </summary>
public sealed record TerraScaleAuthAuditEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("actor_id")] string? ActorId,
    [property: JsonPropertyName("data")] JsonElement Data,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("integrity_version")] int IntegrityVersion,
    [property: JsonPropertyName("integrity_hash")] string IntegrityHash,
    [property: JsonPropertyName("integrity_signature")] string? IntegritySignature,
    [property: JsonPropertyName("integrity_key_fingerprint")] string? IntegrityKeyFingerprint);
