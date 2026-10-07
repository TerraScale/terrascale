using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TerraBase.Configuration;

/// <summary>
/// The connection to ts-orchestrator's NATS, the external persistence TerraBase uses for leases (TS-633 backup shippers)
/// and, later, Anna membership (<c>docs/design/anna-core.md</c> §6). <c>nats-url</c> and <c>nats-credentials-file</c>
/// enable it; <c>nats-certificate</c>, <c>nats-private-key</c> and <c>nats-ca</c> add mutual TLS, all or none.
/// </summary>
public sealed class NatsSettings
{
    /// <summary>Every NATS setting.</summary>
    internal static readonly string[] Keys = ["nats-url", "nats-credentials-file", .. TlsKeys];

    private static string[] TlsKeys => ["nats-certificate", "nats-private-key", "nats-ca"];

    /// <summary>Comma-separated <c>tls://host:port</c> (or <c>nats://host:port</c>) servers of the cell.</summary>
    public required string Url { get; init; }

    /// <summary>NATS user credentials (<c>.creds</c>: user JWT and nkey seed), read by the client; never logged.</summary>
    public required string CredentialsFile { get; init; }

    /// <summary>PEM client certificate for mutual TLS, or <see langword="null"/>.</summary>
    public string? CertificateFile { get; init; }

    /// <summary>PEM private key of <see cref="CertificateFile"/>.</summary>
    public string? PrivateKeyFile { get; init; }

    /// <summary>PEM CA bundle the servers' certificates chain to.</summary>
    public string? CaFile { get; init; }

    /// <summary>Names the servers only: credentials stay out of logs.</summary>
    public override string ToString() => $"NATS at {Url}";

    /// <summary>Reads the settings, or <see langword="null"/> when <c>nats-url</c> is not set.</summary>
    internal static NatsSettings? Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("nats-url", out var url))
        {
            return Keys.Any(values.ContainsKey) ? throw new TerraBaseConfigurationException("nats-* settings require nats-url") : null;
        }

        var servers = url.Split(',', StringSplitOptions.TrimEntries);
        if (servers.Any(server => !Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("tls" or "nats")
            || uri.Host.Length == 0 || uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.PathAndQuery != "/" || uri.Fragment.Length > 0))
        {
            throw new TerraBaseConfigurationException("nats-url must be comma-separated tls://host:port or nats://host:port servers");
        }

        if (!values.TryGetValue("nats-credentials-file", out var credentials) || !IsCredentials(credentials))
        {
            throw new TerraBaseConfigurationException("nats-url requires nats-credentials-file naming a readable NATS user credentials file");
        }

        if (TlsKeys.Any(values.ContainsKey) && !TlsKeys.All(values.ContainsKey))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", TlsKeys)} must be set together");
        }

        if (!values.ContainsKey("nats-certificate"))
        {
            return new() { Url = string.Join(',', servers), CredentialsFile = credentials };
        }

        try
        {
            using var certificate = X509Certificate2.CreateFromPemFile(values["nats-certificate"], values["nats-private-key"]);
            var authorities = new X509Certificate2Collection();
            authorities.ImportFromPemFile(values["nats-ca"]);
            var empty = authorities.Count == 0;
            foreach (var authority in authorities)
            {
                authority.Dispose();
            }

            if (empty)
            {
                throw new CryptographicException();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new TerraBaseConfigurationException("nats-certificate, nats-private-key and nats-ca must name a readable PEM certificate, its private key and a CA bundle");
        }

        return new()
        {
            Url = string.Join(',', servers),
            CredentialsFile = credentials,
            CertificateFile = values["nats-certificate"],
            PrivateKeyFile = values["nats-private-key"],
            CaFile = values["nats-ca"],
        };
    }

    // A .creds file holds a user JWT and an nkey seed between dashed markers.
    private static bool IsCredentials(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return text.Contains("BEGIN NATS USER JWT", StringComparison.Ordinal) && text.Contains("BEGIN USER NKEY SEED", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
