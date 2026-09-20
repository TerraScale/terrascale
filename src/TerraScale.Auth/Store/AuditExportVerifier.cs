using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Auth.Store;

/// <summary>Verification outcome for an exported audit-event collection.</summary>
public sealed record AuditVerificationResult(
    int EventCount,
    int ValidIntegrityCount,
    int ValidAuthenticityCount,
    IReadOnlyList<string> InvalidIntegrityIds,
    IReadOnlyList<string> InvalidAuthenticityIds)
{
    public bool IsValid => InvalidIntegrityIds.Count == 0;
    public bool IsAuthentic => IsValid && InvalidAuthenticityIds.Count == 0;
}

/// <summary>Verifies the signed metadata returned by the audit export API without
/// requiring a TerraBase connection or access to the auth service.</summary>
public static class AuditExportVerifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AuditVerificationResult VerifyJson(ReadOnlySpan<byte> json, IEnumerable<byte[]> verificationKeys)
    {
        ArgumentNullException.ThrowIfNull(verificationKeys);
        var events = JsonSerializer.Deserialize<List<ExportedAuditEvent>>(json, Json)
            ?? throw new JsonException("audit export must be a JSON array");
        return Verify(events.Select(ToAuditEvent), verificationKeys);
    }

    public static AuditVerificationResult Verify(IEnumerable<AuditEvent> events, IEnumerable<byte[]> verificationKeys)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(verificationKeys);
        var keys = verificationKeys.Where(key => key is not null).Select(key => key.ToArray()).ToArray();
        var invalidIntegrity = new List<string>();
        var invalidAuthenticity = new List<string>();
        var count = 0;
        var validIntegrity = 0;
        var validAuthenticity = 0;
        foreach (var auditEvent in events)
        {
            count++;
            if (!auditEvent.HasValidIntegrity())
            {
                invalidIntegrity.Add(auditEvent.Id);
                invalidAuthenticity.Add(auditEvent.Id);
                continue;
            }
            validIntegrity++;
            if (keys.Any(key => auditEvent.HasAuthenticIntegrity(key))) validAuthenticity++;
            else invalidAuthenticity.Add(auditEvent.Id);
        }
        return new AuditVerificationResult(count, validIntegrity, validAuthenticity,
            invalidIntegrity, invalidAuthenticity);
    }

    private static AuditEvent ToAuditEvent(ExportedAuditEvent value) => new()
    {
        Id = value.Id ?? string.Empty,
        InstanceId = value.InstanceId ?? string.Empty,
        Type = value.Type ?? string.Empty,
        ActorId = value.ActorId,
        Data = value.Data ?? [],
        CreatedAt = value.CreatedAt,
        IntegrityVersion = value.IntegrityVersion,
        IntegrityHash = value.IntegrityHash,
        IntegritySignature = value.IntegritySignature,
        IntegrityKeyFingerprint = value.IntegrityKeyFingerprint,
    };

    private sealed record ExportedAuditEvent(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("instance_id")] string? InstanceId,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("actor_id")] string? ActorId,
        [property: JsonPropertyName("data")] Dictionary<string, object>? Data,
        [property: JsonPropertyName("created_at")] long CreatedAt,
        [property: JsonPropertyName("integrity_version")] int? IntegrityVersion,
        [property: JsonPropertyName("integrity_hash")] string? IntegrityHash,
        [property: JsonPropertyName("integrity_signature")] string? IntegritySignature,
        [property: JsonPropertyName("integrity_key_fingerprint")] string? IntegrityKeyFingerprint);
}
