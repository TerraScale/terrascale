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
    string SessionEncryptionKey,
    StripeCredentials? Stripe = null,
    string AuthManagementKey = "")
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
        if (Stripe is null || string.IsNullOrWhiteSpace(Stripe.SecretKey) || string.IsNullOrWhiteSpace(Stripe.WebhookSecret) ||
            !Stripe.SecretKey.StartsWith("sk_", StringComparison.Ordinal) || !Stripe.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal) ||
            Stripe.SecretKey.Any(char.IsControl) || Stripe.WebhookSecret.Any(char.IsControl))
            throw new IOException("Dashboard Stripe identity is invalid.");
        if (string.IsNullOrWhiteSpace(AuthManagementKey) || !AuthManagementKey.StartsWith("sk_", StringComparison.Ordinal) || AuthManagementKey.Any(char.IsControl))
            throw new IOException("Dashboard authentication management identity is invalid.");
    }

    public override string ToString() => "Dashboard credentials [REDACTED]";
}

// Vendor-issued credentials are delivered by OCI Vault in the same machine
// identity as service credentials. There are no payment feature toggles.
public sealed record StripeCredentials(string SecretKey, string WebhookSecret)
{
    public override string ToString() => "Stripe credentials [REDACTED]";
}
