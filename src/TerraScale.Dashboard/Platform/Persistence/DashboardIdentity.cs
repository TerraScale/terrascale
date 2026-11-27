using System.Globalization;
using System.Text;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed record DashboardCredentialsUnavailable(string ErrorType, string Message);

    public readonly union DashboardCredentialsOutcome(DashboardCredentials, DashboardCredentialsUnavailable);

    public interface IDashboardIdentity
    {
        DashboardCredentials Current { get; }

        Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken);

        Task<DashboardCredentialsOutcome> TryReadAsync(TimeSpan timeout, CancellationToken cancellationToken);
    }

    public sealed class DashboardIdentity(Func<string, string?> environment, TimeProvider time) : IDashboardIdentity
    {
        public const string AuthClientSecretName = "TS_AUTH_DASHBOARD_CLIENT_SECRET";
        public const string SessionEncryptionKeyName = "TS_DASHBOARD_SESSION_ENCRYPTION_KEY";
        public const string StripeSecretKeyName = "STRIPE_SECRET_KEY";
        public const string StripeWebhookSecretName = "STRIPE_WEBHOOK_SECRET";
        public const string TerraBasePeerSecretName = "TERRABASE_PEER_SECRET";
        private const string CallerKeyFileName = "TS_DASHBOARD_TERRABASE_KEY_FILE";
        private const string RequestProofKeyFileName = "TERRASCALE_REQUEST_PROOF_KEY_FILE";

        private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);
        private static readonly string[] SecretNames = [AuthClientSecretName, SessionEncryptionKeyName, StripeSecretKeyName, StripeWebhookSecretName, TerraBasePeerSecretName];
        private readonly Lock gate = new();
        private Snapshot? snapshot;
        private Task? refreshing;

        public DashboardIdentity() : this(Environment.GetEnvironmentVariable, TimeProvider.System) { }

        public DashboardCredentials Current =>
            Volatile.Read(ref snapshot)?.Credentials ?? throw new InvalidOperationException("Dashboard credentials are read at startup before any request is served.");

        public async Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken)
        {
            if (!Fresh(Volatile.Read(ref snapshot)))
            {
                await RefreshAsync().WaitAsync(cancellationToken);
            }

            return Current;
        }

        public async Task<DashboardCredentialsOutcome> TryReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                return new DashboardCredentialsUnavailable(nameof(TimeoutException), "Dashboard credentials were not read within the request deadline.");
            }

            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reading.CancelAfter(timeout);
            try
            {
                return await ReadAsync(reading.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DashboardCredentialsUnavailable(nameof(TimeoutException), "Dashboard credentials were not read within the request deadline.");
            }
            catch (IOException error)
            {
                return new DashboardCredentialsUnavailable(nameof(IOException), error.Message);
            }
            catch (UnauthorizedAccessException error)
            {
                return new DashboardCredentialsUnavailable(nameof(UnauthorizedAccessException), error.Message);
            }
        }

        private bool Fresh(Snapshot? current) => current is not null && time.GetUtcNow() - current.CheckedAt < Recheck;

        private Task RefreshAsync()
        {
            lock (gate)
            {
                if (refreshing is { IsCompleted: false } running)
                {
                    return running;
                }

                return Fresh(snapshot) ? Task.CompletedTask : refreshing = ReloadAsync();
            }
        }

        private async Task ReloadAsync()
        {
            await Task.Yield();
            var stamp = Stamp();
            var current = Volatile.Read(ref snapshot);
            var credentials = current is not null && current.Stamp == stamp ? current.Credentials : await LoadAsync();
            Volatile.Write(ref snapshot, new Snapshot(stamp, time.GetUtcNow(), credentials));
        }

        private async Task<DashboardCredentials> LoadAsync()
        {
            var credentials = new DashboardCredentials(await ReadAsync(AuthClientSecretName), await ReadAsync(SessionEncryptionKeyName),
                new(await ReadAsync(StripeSecretKeyName), await ReadAsync(StripeWebhookSecretName)), await ReadAsync(TerraBasePeerSecretName))
            {
                Caller = environment(CallerKeyFileName) is { Length: > 0 } callerPath
                    ? new CallerCredential("dashboard", await File.ReadAllTextAsync(callerPath), time) : null,
                RequestProofKey = environment(RequestProofKeyFileName) is { Length: > 0 } path
                    ? (await File.ReadAllTextAsync(path)).TrimEnd('\r', '\n')
                    : await OptionalAsync("TERRASCALE_REQUEST_PROOF_KEY") ?? await ReadAsync(TerraBasePeerSecretName),
            };
            credentials.Validate();
            return credentials;
        }

        private string Stamp()
        {
            var stamp = new StringBuilder();
            foreach (var path in SecretNames.Select(name => environment(name + "_FILE")).Append(environment(CallerKeyFileName)).Append(environment(RequestProofKeyFileName)))
            {
                if (path is { Length: > 0 })
                {
                    var file = new FileInfo(path);
                    stamp.Append(CultureInfo.InvariantCulture, $"{path}:{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}:{(file.Exists ? file.Length : -1)};");
                }
            }

            return stamp.ToString();
        }

        private async Task<string> ReadAsync(string name) => await OptionalAsync(name) ?? throw new IOException($"Dashboard secret {name} is not set.");

        private async Task<string?> OptionalAsync(string name)
        {
            return environment(name) is { Length: > 0 } value ? value
            : environment(name + "_FILE") is { Length: > 0 } path ? (await File.ReadAllTextAsync(path)).TrimEnd('\r', '\n')
            : null;
        }

        private sealed record Snapshot(string Stamp, DateTimeOffset CheckedAt, DashboardCredentials Credentials);
    }

    public sealed record DashboardCredentials(string AuthClientSecret, string SessionEncryptionKey, StripeCredentials Stripe, string TerraBasePeerSecret)
    {
        public CallerCredential? Caller { get; init; }
        public string RequestProofKey { get; init; } = TerraBasePeerSecret;

        public string AuthClientId => "01M4EQ2FRJ8S8B29E8TQVWNW4J";

        public void Validate()
        {
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

    public sealed record StripeCredentials(string SecretKey, string WebhookSecret)
    {
        public bool IsLive => SecretKey.StartsWith("sk_live_", StringComparison.Ordinal) || SecretKey.StartsWith("rk_live_", StringComparison.Ordinal);

        public override string ToString()
        {
            return "Stripe credentials [REDACTED]";
        }
    }
}
