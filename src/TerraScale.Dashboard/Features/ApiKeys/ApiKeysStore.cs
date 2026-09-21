using System.Collections.Immutable;

namespace TerraScale.Dashboard;

public sealed partial class DashboardStore
{
    public ImmutableArray<M2mCredential> M2mCredentials { get => World.M2mCredentials; private set => World.M2mCredentials = value; }

    /// <summary>Secret revealed once by create/rotate; consumed by the next page render.</summary>
    public (string CredentialId, string Name, string Secret)? PendingSecret { get => World.PendingSecret; private set => World.PendingSecret = value; }

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
