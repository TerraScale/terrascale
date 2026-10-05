using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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

    /// <summary>Default seconds shutdown waits for transactions to return their database connections.</summary>
    public const int DefaultShutdownGraceSeconds = 10;

    private const int MaxShutdownGraceSeconds = 3600;

    /// <summary>Default directory for SQLite database files, relative to the working directory.</summary>
    public const string DefaultDataDirectory = "data";

    private const string EnvironmentPrefix = "TERRABASE_";

    private const int MinDashboardCredentialLength = 32;

    private const int MaxUserIdLength = 256;

    private static readonly string[] ClientTlsKeys = ["client-certificate", "client-private-key"];

    private static readonly string[] PeerTlsKeys = ["peer-certificate", "peer-private-key", "peer-client-ca"];

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        ["admin-port", "data-directory", "region", "client-port", .. ClientTlsKeys, "dashboard-credential-file", "management-legacy-owner", "peer-port", .. PeerTlsKeys, "shutdown-grace-seconds"]);

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

    /// <summary>Node region. Startup requires it outside Development, where it defaults to <c>development</c>.</summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>
    /// Port of the client-facing HTTPS listener; <c>0</c> picks a free port.
    /// <see langword="null"/> disables the listener.
    /// </summary>
    public int? ClientPort { get; init; }

    /// <summary>Server certificate with its private key for the client listener.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>
    /// SHA-256 of the dashboard service credential that authorizes the Management API, read from
    /// <c>dashboard-credential-file</c>. Empty disables the Management API. The secret itself is not kept.
    /// </summary>
    public ReadOnlyMemory<byte> DashboardCredentialHash { get; init; }

    /// <summary>
    /// User id (from ts-auth) that becomes the creator and only member of organizations stored before the
    /// Management API had membership (catalog version 1). Read once, when such a catalog is migrated.
    /// </summary>
    public string? ManagementLegacyOwner { get; init; }

    /// <summary>
    /// Port of the inter-node gRPC listener (mutual TLS, HTTP/2); <c>0</c> picks a free port.
    /// <see langword="null"/> disables the listener.
    /// </summary>
    public int? PeerPort { get; init; }

    /// <summary>Server certificate with its private key for the peer listener.</summary>
    public X509Certificate2? PeerCertificate { get; init; }

    /// <summary>
    /// How long shutdown waits for transactions to return their database connections. When it expires, shutdown
    /// returns but keeps the data directory owned until they do (or the process exits); it never closes them.
    /// </summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(DefaultShutdownGraceSeconds);

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

        var region = values.GetValueOrDefault("region", string.Empty);
        if (values.ContainsKey("region") && (region.Length is < 1 or > 63 || region[0] is < 'a' or > 'z'
            || region[^1] == '-' || region.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
        {
            throw new TerraBaseConfigurationException("region must be a lowercase letter followed by lowercase letters, digits or hyphens, at most 63 characters, without a trailing hyphen");
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
        if (clientPort is null && values.ContainsKey("dashboard-credential-file"))
        {
            throw new TerraBaseConfigurationException("dashboard-credential-file requires client-port");
        }

        var legacyOwner = values.GetValueOrDefault("management-legacy-owner");
        if (legacyOwner is not null && (string.IsNullOrWhiteSpace(legacyOwner) || legacyOwner.Length > MaxUserIdLength || legacyOwner.Any(char.IsControl)))
        {
            throw new TerraBaseConfigurationException(
                "management-legacy-owner must be a non-blank user id of at most 256 characters without control characters");
        }

        var peerPort = ParsePort(values, "peer-port");
        RequireGroup(values, "peer-port", peerPort, PeerTlsKeys);

        return new TerraBaseOptions
        {
            AdminPort = ParsePort(values, "admin-port") ?? DefaultAdminPort,
            DataDirectory = dataDirectory,
            Region = region,
            ClientPort = clientPort,
            ClientCertificate = clientPort is null ? null : LoadPem(
                () => X509Certificate2.CreateFromPemFile(values["client-certificate"], values["client-private-key"]),
                "client-certificate and client-private-key must name a readable PEM certificate and its private key"),
            DashboardCredentialHash = values.TryGetValue("dashboard-credential-file", out var credentialFile)
                ? ReadDashboardCredentialHash(credentialFile)
                : ReadOnlyMemory<byte>.Empty,
            ManagementLegacyOwner = legacyOwner,
            PeerPort = peerPort,
            PeerCertificate = peerPort is null ? null : LoadPem(
                () => X509Certificate2.CreateFromPemFile(values["peer-certificate"], values["peer-private-key"]),
                "peer-certificate and peer-private-key must name a readable PEM certificate and its private key"),
            PeerClientCa = peerPort is null ? null : LoadPem(
                () => X509CertificateLoader.LoadCertificateFromFile(values["peer-client-ca"]),
                "peer-client-ca must name a readable PEM certificate"),
            ShutdownGrace = TimeSpan.FromSeconds(ParseShutdownGraceSeconds(values)),
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

    private static int ParseShutdownGraceSeconds(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("shutdown-grace-seconds", out var raw))
        {
            return DefaultShutdownGraceSeconds;
        }

        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= MaxShutdownGraceSeconds
            ? seconds
            : throw new TerraBaseConfigurationException($"shutdown-grace-seconds must be an integer between 0 and {MaxShutdownGraceSeconds}");
    }

    // The file holds the secret alone; surrounding whitespace (a trailing newline) is ignored.
    private static byte[] ReadDashboardCredentialHash(string path)
    {
        string secret;
        try
        {
            secret = File.ReadAllText(path).Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TerraBaseConfigurationException("dashboard-credential-file must name a readable file");
        }

        return secret.Length < MinDashboardCredentialLength
            ? throw new TerraBaseConfigurationException("dashboard-credential-file must hold a secret of at least 32 characters")
            : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
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
