using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Persistence;

// This file is the consumer boundary for the Vault-delivered machine identity.
// Provisioning/delivery is an external cutover gate, never a manual setup fallback.
public interface IDashboardIdentity
{
    Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken);
}

public sealed class DashboardIdentity : IDashboardIdentity
{
    private const string Path = "/run/secrets/terrascale/dashboard.json";

    public async Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path);
        if (stream.Length > 65536) throw new IOException("Dashboard machine identity is invalid.");
        var credentials = await JsonSerializer.DeserializeAsync<DashboardCredentials>(stream, cancellationToken: cancellationToken)
            ?? throw new IOException("Dashboard machine identity is unavailable.");
        credentials.Validate();
        return credentials;
    }
}

public sealed record DashboardCredentials(
    string StateProjectId,
    string StateApiKey,
    string ManagementCredential,
    string AuthInstanceSlug,
    string AuthClientId,
    string AuthClientSecret,
    string SessionEncryptionKey)
{
    public void Validate()
    {
        foreach (var value in new[] { StateProjectId, StateApiKey, ManagementCredential, AuthInstanceSlug, AuthClientId, AuthClientSecret })
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                throw new IOException("Dashboard machine identity is invalid.");
        try
        {
            if (string.IsNullOrWhiteSpace(SessionEncryptionKey) || Convert.FromBase64String(SessionEncryptionKey).Length != 32)
                throw new IOException("Dashboard encryption key is invalid.");
        }
        catch (FormatException)
        {
            throw new IOException("Dashboard encryption key is invalid.");
        }
    }

    public override string ToString() => "Dashboard credentials [REDACTED]";
}
