using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TerraScale.TerraBase.Configuration;

/// <summary>An invalid or incomplete <c>TERRABASE_*</c> configuration. The process must not start.</summary>
public sealed class TerraBaseConfigurationException(string message) : Exception(message);

/// <summary>
/// Validated process configuration.
/// Every setting is <c>TERRABASE_&lt;NAME&gt;</c> or the equivalent <c>--name=value</c> argument;
/// an argument wins over the environment.
/// </summary>
public sealed record TerraBaseOptions
{
    /// <summary>Default port of the loopback administrative and metrics listener.</summary>
    public const int DefaultAdminPort = 9464;

    private const string EnvironmentPrefix = "TERRABASE_";

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal, "admin-port", "peer-port", "peer-certificate", "peer-private-key", "peer-client-ca");

    private static readonly string[] PeerTlsKeys = ["peer-certificate", "peer-private-key", "peer-client-ca"];

    /// <summary>Port of the loopback administrative listener; <c>0</c> picks a free port.</summary>
    public required int AdminPort { get; init; }

    /// <summary>
    /// Port of the inter-node gRPC listener (mutual TLS, HTTP/2); <c>0</c> picks a free port.
    /// <see langword="null"/> disables the listener.
    /// </summary>
    public int? PeerPort { get; init; }

    /// <summary>Server certificate with its private key for the peer listener.</summary>
    public X509Certificate2? PeerCertificate { get; init; }

    /// <summary>Certificate authority that every connecting peer's client certificate must chain to.</summary>
    public X509Certificate2? PeerClientCa { get; init; }

    /// <summary>Reads and validates the configuration of this process.</summary>
    public static TerraBaseOptions FromProcess(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentPrefix + key.Replace('-', '_').ToUpperInvariant());
            if (!string.IsNullOrEmpty(value))
            {
                values[key] = value;
            }
        }

        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (!arg.StartsWith("--", StringComparison.Ordinal) || separator < 3)
            {
                throw new TerraBaseConfigurationException($"unrecognized argument '{arg.Split('=')[0]}'; use --name=value");
            }

            values[arg[2..separator]] = arg[(separator + 1)..];
        }

        return FromValues(values);
    }

    /// <summary>Validates settings keyed by their argument name.</summary>
    public static TerraBaseOptions FromValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var key in values.Keys)
        {
            if (!Keys.Contains(key))
            {
                throw new TerraBaseConfigurationException($"unknown setting '{key}'");
            }
        }

        var adminPort = ParsePort(values, "admin-port") ?? DefaultAdminPort;
        var peerPort = ParsePort(values, "peer-port");
        if (peerPort is null)
        {
            if (PeerTlsKeys.Any(values.ContainsKey))
            {
                throw new TerraBaseConfigurationException("peer-certificate, peer-private-key and peer-client-ca require peer-port");
            }

            return new TerraBaseOptions { AdminPort = adminPort };
        }

        if (!PeerTlsKeys.All(values.ContainsKey))
        {
            throw new TerraBaseConfigurationException("peer-port requires peer-certificate, peer-private-key and peer-client-ca");
        }

        return new TerraBaseOptions
        {
            AdminPort = adminPort,
            PeerPort = peerPort,
            PeerCertificate = LoadPem(() => X509Certificate2.CreateFromPemFile(values["peer-certificate"], values["peer-private-key"]),
                "peer-certificate and peer-private-key must name a readable PEM certificate and its private key"),
            PeerClientCa = LoadPem(() => X509CertificateLoader.LoadCertificateFromFile(values["peer-client-ca"]),
                "peer-client-ca must name a readable PEM certificate"),
        };
    }

    private static int? ParsePort(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > ushort.MaxValue)
        {
            throw new TerraBaseConfigurationException($"{key} must be an integer between 0 and 65535");
        }

        return port;
    }

    // Messages name the settings, never the paths or file contents.
    private static X509Certificate2 LoadPem(Func<X509Certificate2> load, string message)
    {
        try
        {
            return load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new TerraBaseConfigurationException(message);
        }
    }
}
