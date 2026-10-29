namespace TerraScale.Dashboard.Platform.Persistence
{
    // Each secret arrives as an environment variable, or as a file named by <NAME>_FILE (ts-orch delivers OCI Vault
    // secrets as files). No default or fallback exists, and the values are never logged or echoed.
    public interface IDashboardIdentity
    {
        Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken);
    }

    public sealed class DashboardIdentity(Func<string, string?> environment) : IDashboardIdentity
    {
        // ts-auth seeds its dashboard client from the same-named secret on its own app.
        public const string AuthClientSecretName = "TS_AUTH_DASHBOARD_CLIENT_SECRET";
        public const string SessionEncryptionKeyName = "TS_DASHBOARD_SESSION_ENCRYPTION_KEY";
        public const string StripeSecretKeyName = "STRIPE_SECRET_KEY";
        public const string StripeWebhookSecretName = "STRIPE_WEBHOOK_SECRET";
        // TerraBase and ts-auth hold the same value; TerraBase verifies it on its internal listener.
        public const string TerraBasePeerSecretName = "TERRABASE_PEER_SECRET";

        public DashboardIdentity() : this(Environment.GetEnvironmentVariable) { }

        public Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken)
        {
            var credentials = new DashboardCredentials(Read(AuthClientSecretName), Read(SessionEncryptionKeyName),
                new(Read(StripeSecretKeyName), Read(StripeWebhookSecretName)), Read(TerraBasePeerSecretName));
            credentials.Validate();
            return Task.FromResult(credentials);
        }

        private string Read(string name)
        {
            return environment(name) is { Length: > 0 } value ? value
            : environment(name + "_FILE") is { Length: > 0 } path ? File.ReadAllText(path).TrimEnd('\r', '\n')
            : throw new IOException($"Dashboard secret {name} is not set.");
        }
    }

    public sealed record DashboardCredentials(string AuthClientSecret, string SessionEncryptionKey, StripeCredentials Stripe, string TerraBasePeerSecret)
    {
        // ts-auth fixes the dashboard client's ID in its code; only the secret is delivered.
        public string AuthClientId => "01M4EQ2FRJ8S8B29E8TQVWNW4J";

        public void Validate()
        {
            // ts-auth accepts 32-512 characters without whitespace or ';' for this secret.
            if (AuthClientSecret is not { Length: >= 32 and <= 512 } || AuthClientSecret.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == ';'))
            {
                throw new IOException("Dashboard authentication client secret is invalid.");
            }

            try
            {
                if (string.IsNullOrWhiteSpace(SessionEncryptionKey) || Convert.FromBase64String(SessionEncryptionKey).Length != 32)
                {
                    throw new IOException("Dashboard encryption key is invalid.");
                }
            }
            catch (FormatException)
            {
                throw new IOException("Dashboard encryption key is invalid.");
            }
            if (Stripe is null || string.IsNullOrWhiteSpace(Stripe.SecretKey) || string.IsNullOrWhiteSpace(Stripe.WebhookSecret) ||
                !(Stripe.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal) || Stripe.SecretKey.StartsWith("sk_live_", StringComparison.Ordinal) ||
                Stripe.SecretKey.StartsWith("rk_test_", StringComparison.Ordinal) || Stripe.SecretKey.StartsWith("rk_live_", StringComparison.Ordinal)) || !Stripe.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal) ||
                Stripe.SecretKey.Any(char.IsControl) || Stripe.WebhookSecret.Any(char.IsControl))
            {
                throw new IOException("Dashboard Stripe identity is invalid.");
            }
            // 32 random bytes as 64 hexadecimal characters, the format TerraBase and ts-auth accept.
            if (TerraBasePeerSecret is not { Length: 64 } || !TerraBasePeerSecret.All(char.IsAsciiHexDigit))
            {
                throw new IOException("Dashboard secret TERRABASE_PEER_SECRET must be 64 hexadecimal characters.");
            }
        }

        public override string ToString()
        {
            return "Dashboard credentials [REDACTED]";
        }
    }

    // Vendor-issued Stripe keys arrive through ts-orch's OCI Vault secret delivery.
    // There are no payment feature toggles.
    public sealed record StripeCredentials(string SecretKey, string WebhookSecret)
    {
        public bool IsLive => SecretKey.StartsWith("sk_live_", StringComparison.Ordinal) || SecretKey.StartsWith("rk_live_", StringComparison.Ordinal);

        public override string ToString()
        {
            return "Stripe credentials [REDACTED]";
        }
    }
}
