using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

/// <summary>API Keys feature data: the machine-client credential list the
/// original loads from the auth service (full column parity with API keys),
/// plus the one-time secret channel for create/rotate reveals.</summary>
public sealed record M2mCredential(
    string ClientId,
    string ProjectId,
    string Name,
    string Prefix,
    string LastFour,
    string[] Scopes,
    string DatabaseId,        // "" = Project
    string CreatedByEmail,
    string CreatedAt,
    string? ExpiresAt,
    string? LastUsedAt,
    string? RevokedAt)
{
    public string Status =>
        RevokedAt is not null ? "Revoked"
        : ExpiresAt is { } e && DateTimeOffset.TryParse(e, out var exp) && (exp - DateTimeOffset.Now).TotalDays < 14 ? "Expiring"
        : "Active";
}

public sealed partial class DashboardStore
{
    public ImmutableArray<M2mCredential> M2mCredentials { get; private set; } =
    [
        new("m2m_01JC9Q8A2C4E6G8J0L2N4Q7ST", "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA", "ingest-worker",
            "tsm_live_4e1", "7ab2", ["documents:write"], "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC",
            "mario@terrascale.tech", "2026-02-18T08:00:00Z", null, "2026-09-18T04:12:00Z", null),
        new("m2m_01JC9R9B3D5F7H9K1M3P5S8UV", "prj_01JC9R2X4P6T8V1Z3B5D7F9HC", "edge-sync",
            "tsm_live_b90", "3f17", ["documents:read", "documents:write"], "",
            "ana@terrascale.tech", "2026-03-05T13:30:00Z", "2026-09-30T00:00:00Z", "2026-09-18T02:55:00Z", null),
    ];

    /// <summary>Secret revealed once by create/rotate; consumed by the next page render.</summary>
    public (string CredentialId, string Name, string Secret)? PendingSecret { get; private set; }

    /// <summary>Port of credentialStatus: revoked wins, then expiring-within-14-days.</summary>
    public static string CredentialStatus(string? revokedAt, string? expiresAt) =>
        new M2mCredential("", "", "", "", "", [], "", "", "", expiresAt, null, revokedAt).Status;

    public void SavePendingSecret(string credentialId, string name, string secret) =>
        PendingSecret = (credentialId, name, secret);

    public (string CredentialId, string Name, string Secret)? ConsumePendingSecret()
    {
        var pending = PendingSecret;
        PendingSecret = null;
        return pending;
    }

    public string CreateM2mCredential(string name, string[] scopes, string databaseId, string? expiresAt)
    {
        var secret = $"tsm_live_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant()}";
        M2mCredentials = M2mCredentials.Add(new(
            $"m2m_{Guid.NewGuid():N}"[..20], ActiveProjectId, name, secret[..12], secret[^4..],
            scopes, databaseId ?? "", CurrentUser.Email, DateTimeOffset.Now.ToString("O"),
            expiresAt, null, null));
        return secret;
    }

    public string RotateApiKey(string keyId)
    {
        var secret = $"tsk_live_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant()}";
        ApiKeys = [.. ApiKeys.Select(k => k.KeyId == keyId
            ? k with { Prefix = secret[..12], LastFour = secret[^4..], RevokedAt = null, ExpiresAt = DateTimeOffset.Now.AddMonths(6).ToString("O") }
            : k)];
        return secret;
    }

    public string RotateM2mCredential(string clientId)
    {
        var secret = $"tsm_live_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant()}";
        M2mCredentials = [.. M2mCredentials.Select(c => c.ClientId == clientId
            ? c with { Prefix = secret[..12], LastFour = secret[^4..], RevokedAt = null }
            : c)];
        return secret;
    }

    public void RevokeM2mCredential(string clientId) =>
        M2mCredentials = [.. M2mCredentials.Select(c => c.ClientId == clientId ? c with { RevokedAt = DateTimeOffset.Now.ToString("O") } : c)];
}
