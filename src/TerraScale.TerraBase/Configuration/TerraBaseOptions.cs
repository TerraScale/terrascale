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
/// an argument wins over the environment. An empty value means "not set", so <c>--name=</c>
/// clears a setting inherited from the environment.
/// </summary>
public sealed record TerraBaseOptions
{
    /// <summary>Default port of the loopback administrative and metrics listener.</summary>
    public const int DefaultAdminPort = 9464;

    /// <summary>Default directory for SQLite database files, relative to the working directory.</summary>
    public const string DefaultDataDirectory = "data";

    private const string EnvironmentPrefix = "TERRABASE_";

    private static readonly string[] ClientTlsKeys = ["client-certificate", "client-private-key"];

    private static readonly string[] PeerTlsKeys = ["peer-certificate", "peer-private-key", "peer-client-ca"];

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        ["admin-port", "data-directory", "client-port", .. ClientTlsKeys, "peer-port", .. PeerTlsKeys]);

    /// <summary>Every setting name, in <c>--name=value</c> form without the dashes.</summary>
    public static IReadOnlySet<string> SettingNames => Keys;

    // Generic-host arguments that WebApplicationFactory passes to the entry point. They are not
    // TerraBase settings and change nothing: the host is built without args.
    private static readonly FrozenSet<string> HostArguments = FrozenSet.Create(
        StringComparer.Ordinal, "environment", "applicationName", "contentRoot");

    /// <summary>Port of the loopback administrative listener; <c>0</c> picks a free port.</summary>
    public required int AdminPort { get; init; }

    /// <summary>Directory holding one SQLite file per database.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// Port of the client-facing HTTPS listener; <c>0</c> picks a free port.
    /// <see langword="null"/> disables the listener.
    /// </summary>
    public int? ClientPort { get; init; }

    /// <summary>Server certificate with its private key for the client listener.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

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

            var key = arg[2..separator];
            var value = arg[(separator + 1)..];
            if (HostArguments.Contains(key))
            {
                continue;
            }

            if (value.Length == 0)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
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

        var dataDirectory = values.GetValueOrDefault("data-directory", DefaultDataDirectory);
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new TerraBaseConfigurationException("data-directory must not be empty");
        }

        try
        {
            dataDirectory = Path.GetFullPath(dataDirectory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            throw new TerraBaseConfigurationException("data-directory must be a valid path");
        }

        var clientPort = ParsePort(values, "client-port");
        RequireGroup(values, "client-port", clientPort, ClientTlsKeys);
        var peerPort = ParsePort(values, "peer-port");
        RequireGroup(values, "peer-port", peerPort, PeerTlsKeys);

        return new TerraBaseOptions
        {
            AdminPort = ParsePort(values, "admin-port") ?? DefaultAdminPort,
            DataDirectory = dataDirectory,
            ClientPort = clientPort,
            ClientCertificate = clientPort is null ? null : LoadPem(
                () => X509Certificate2.CreateFromPemFile(values["client-certificate"], values["client-private-key"]),
                "client-certificate and client-private-key must name a readable PEM certificate and its private key"),
            PeerPort = peerPort,
            PeerCertificate = peerPort is null ? null : LoadPem(
                () => X509Certificate2.CreateFromPemFile(values["peer-certificate"], values["peer-private-key"]),
                "peer-certificate and peer-private-key must name a readable PEM certificate and its private key"),
            PeerClientCa = peerPort is null ? null : LoadPem(
                () => X509CertificateLoader.LoadCertificateFromFile(values["peer-client-ca"]),
                "peer-client-ca must name a readable PEM certificate"),
        };
    }

    // A listener port and its TLS settings are all set or all absent.
    private static void RequireGroup(IReadOnlyDictionary<string, string> values, string portKey, int? port, string[] tlsKeys)
    {
        var tls = string.Join(", ", tlsKeys);
        if (port is null && tlsKeys.Any(values.ContainsKey))
        {
            throw new TerraBaseConfigurationException($"{tls} require {portKey}");
        }

        if (port is not null && !tlsKeys.All(values.ContainsKey))
        {
            throw new TerraBaseConfigurationException($"{portKey} requires {tls}");
        }
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
