using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TerraScale.TerraBase.Features.Replication;

namespace TerraScale.TerraBase.Configuration;

/// <summary>An invalid or incomplete <c>TERRABASE_*</c> configuration. The process must not start.</summary>
public sealed class TerraBaseConfigurationException(string message) : Exception(message);

/// <summary>PEM files of the peer listener, read again whenever one of them changes.</summary>
/// <param name="Certificate">This node's certificate. Peers use it both to serve and to connect, so it carries the server and client authentication usages.</param>
/// <param name="PrivateKey">The private key of <paramref name="Certificate"/>.</param>
/// <param name="Authority">The certificate authorities (one or more PEM certificates) every peer certificate must chain to.</param>
public sealed record PeerTlsFiles(string Certificate, string PrivateKey, string Authority)
{
    internal const int MaxPemFileBytes = 1024 * 1024;
    internal const int MaxAuthorities = 64;

    /// <summary>TLS server authentication extended key usage.</summary>
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>TLS client authentication extended key usage.</summary>
    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Reads the certificate with its key and every authority, and requires the certificate to chain to them
    /// for both server and client authentication, as peers check it. The caller owns what this returns and
    /// disposes it with <see cref="Dispose"/>; on failure nothing stays allocated.
    /// </summary>
    /// <exception cref="CryptographicException">A file holds no usable certificate or key, or the certificate does not chain.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public (X509Certificate2 Certificate, X509Certificate2Collection Authorities, X509Certificate2Collection Intermediates) Load()
    {
        var authorities = new X509Certificate2Collection();
        var intermediates = new X509Certificate2Collection();
        X509Certificate2? certificate = null;
        try
        {
            ImportCertificates(Authority, authorities, MaxAuthorities);
            var certificatePem = ImportCertificates(Certificate, intermediates, MaxAuthorities + 1);
            if (intermediates.Count == 0)
            {
                throw new CryptographicException("peer-certificate contains no certificate");
            }

            intermediates[0].Dispose();
            intermediates.RemoveAt(0);
            certificate = X509Certificate2.CreateFromPem(certificatePem, Encoding.UTF8.GetString(ReadPemBytes(PrivateKey)));
            return Chains(certificate, authorities, ServerAuthentication, intermediates) && Chains(certificate, authorities, ClientAuthentication, intermediates)
                ? (certificate, authorities, intermediates)
                : throw new CryptographicException("the peer certificate does not chain to the peer authority for server and client authentication");
        }
        catch
        {
            Dispose(certificate, authorities, intermediates);
            throw;
        }
    }

    private static string ImportCertificates(string path, X509Certificate2Collection certificates, int maximum)
    {
        // ImportFromPem uses the complete bundle from the bounded reader, rather than an unbounded file read.
        var pem = Encoding.UTF8.GetString(ReadPemBytes(path));
        var remaining = pem.AsSpan();
        var count = 0;
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            if (remaining[fields.Label].SequenceEqual("CERTIFICATE") && ++count > maximum)
            {
                throw new CryptographicException("peer-certificate or peer-client-ca contains too many certificates");
            }

            remaining = remaining[fields.Location.End..];
        }

        certificates.ImportFromPem(pem);
        return pem;
    }

    // Both loading and SHA-256 checks cap actual reads, including a file that grows during the read.
    internal static byte[] ReadPemBytes(string path)
    {
        using var file = File.OpenRead(path);
        var length = file.Length;
        if (length > MaxPemFileBytes)
        {
            throw new CryptographicException("peer-certificate, peer-private-key and peer-client-ca must each fit within 1 MiB");
        }

        var pem = new byte[(int)length];
        file.ReadExactly(pem);
        return file.ReadByte() < 0
            ? pem
            : throw new IOException("peer-certificate, peer-private-key or peer-client-ca changed while being read");
    }

    /// <summary>Disposes what <see cref="Load"/> returned.</summary>
    public static void Dispose(X509Certificate2? certificate, X509Certificate2Collection authorities, X509Certificate2Collection? intermediates = null)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        certificate?.Dispose();
        foreach (var intermediate in intermediates ?? [])
        {
            intermediate.Dispose();
        }

        foreach (var authority in authorities)
        {
            authority.Dispose();
        }
    }

    /// <summary>Whether <paramref name="certificate"/> chains to one of <paramref name="authorities"/> and allows <paramref name="usage"/>.</summary>
    public static bool Chains(X509Certificate2 certificate, X509Certificate2Collection authorities, string usage, X509Certificate2Collection? intermediates = null)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy = Policy(authorities, usage);
        if (intermediates is not null)
        {
            chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        }
        return chain.Build(certificate);
    }

    internal static X509ChainPolicy Policy(X509Certificate2Collection authorities, string usage)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
        };
        policy.CustomTrustStore.AddRange(authorities);
        policy.ApplicationPolicy.Add(new Oid(usage));
        return policy;
    }

}

/// <summary>
/// Validated process configuration.
/// Every setting is <c>TERRABASE_&lt;NAME&gt;</c> or the equivalent <c>--name=value</c> argument;
/// an argument wins over the environment. An empty value means "not set", so <c>--name=</c>
/// clears a setting inherited from the environment.
/// </summary>
public sealed record TerraBaseOptions : IDisposable
{
    /// <summary>Default port of the loopback administrative and metrics listener.</summary>
    public const int DefaultAdminPort = 9464;

    /// <summary>Default seconds shutdown waits for transactions to return their database connections.</summary>
    public const int DefaultShutdownGraceSeconds = 10;

    private const int MaxShutdownGraceSeconds = 3600;

    /// <summary>Default seconds completed readiness filesystem results are cached.</summary>
    public const int DefaultReadinessCacheSeconds = 1;

    private const int MaxReadinessCacheSeconds = 60;

    /// <summary>Default directory for SQLite database files, relative to the working directory.</summary>
    public const string DefaultDataDirectory = "data";

    private const string EnvironmentPrefix = "TERRABASE_";

    private const int MaxUserIdLength = 256;

    private const int MinManagementCredentialLength = 32;

    private static readonly string[] ClientTlsKeys = ["client-certificate", "client-private-key"];

    private static readonly string[] PeerTlsKeys = ["peer-certificate", "peer-private-key", "peer-client-ca"];

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        ["admin-port", "data-directory", "region", "node-name", "memory-budget", "client-port", .. ClientTlsKeys, "auth-issuer", "auth-audience", "management-credential-file", "management-legacy-owner", "peer-port", .. PeerTlsKeys, "peer-addresses", "shutdown-grace-seconds", "readiness-cache-seconds"]);

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

    /// <summary>Operator node identity; defaults to the machine name. This is not membership evidence.</summary>
    public string NodeName { get; init; } = Environment.MachineName;

    /// <summary>Memory pressure denominator in bytes; telemetry only until memory admission lands in #13.</summary>
    public long MemoryBudget { get; init; } = Math.Max(1024 * 1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);

    internal static bool IsRegion(string region) => region.Length is >= 1 and <= 63 && region[0] is >= 'a' and <= 'z'
        && region[^1] != '-' && region.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>
    /// Port of the client-facing HTTPS listener; <c>0</c> picks a free port.
    /// <see langword="null"/> disables the listener.
    /// </summary>
    public int? ClientPort { get; init; }

    /// <summary>Server certificate with its private key for the client listener.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>
    /// Issuer (<c>iss</c>) of the ts-auth access tokens that authenticate Management API users, an absolute HTTPS URL.
    /// Its signing keys come from <c>{issuer}/.well-known/openid-configuration</c>. <see langword="null"/> authenticates
    /// no user, so the Management API answers 401.
    /// </summary>
    public string? AuthIssuer { get; init; }

    /// <summary>Accepted audiences (<c>aud</c>) of those tokens; a token must name at least one. Set with <see cref="AuthIssuer"/>.</summary>
    public IReadOnlyList<string> AuthAudiences { get; init; } = [];

    /// <summary>
    /// SHA-256 of the management service credential, read from <c>management-credential-file</c>. It authorizes the Management
    /// API on the loopback administrative listener only, acting as the user named in <c>X-TerraBase-User-Id</c>. Empty
    /// disables it. The secret itself is not kept.
    /// </summary>
    public ReadOnlyMemory<byte> ManagementCredentialHash { get; init; }

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

    /// <summary>Certificate files of the peer listener, validated at startup; set exactly when <see cref="PeerPort"/> is.</summary>
    public PeerTlsFiles? PeerTls { get; init; }

    private PeerCredentials.Snapshot? _peerCredentialSnapshot;

    internal PeerCredentials.Snapshot? PeerCredentialSnapshot { get => _peerCredentialSnapshot; init => _peerCredentialSnapshot = value; }

    internal PeerCredentials.Snapshot? TakePeerCredentialSnapshot() => Interlocked.Exchange(ref _peerCredentialSnapshot, null);

    /// <summary>Releases startup certificates, including a peer snapshot not consumed before startup failed.</summary>
    public void Dispose()
    {
        TakePeerCredentialSnapshot()?.Dispose();
        ClientCertificate?.Dispose();
    }

    /// <summary>
    /// How long shutdown waits for transactions to return their database connections. When it expires, shutdown
    /// returns but keeps the data directory owned until they do (or the process exits); it never closes them.
    /// </summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(DefaultShutdownGraceSeconds);

    /// <summary>How long completed readiness filesystem results are cached; shutdown bypasses this cache.</summary>
    public TimeSpan ReadinessCacheTtl { get; init; } = TimeSpan.FromSeconds(DefaultReadinessCacheSeconds);

    /// <summary>
    /// Peer listeners (<c>https://host:port</c>) this node sends its committed changes to; empty
    /// disables sending (changes are captured either way). Requires <see cref="PeerPort"/>, whose
    /// certificate it presents.
    /// </summary>
    public IReadOnlyList<Uri> PeerAddresses { get; init; } = [];

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
        if (values.ContainsKey("region") && !IsRegion(region))
        {
            throw new TerraBaseConfigurationException("region must be a lowercase letter followed by lowercase letters, digits or hyphens, at most 63 characters, without a trailing hyphen");
        }

        var dataDirectory = values.GetValueOrDefault("data-directory", DefaultDataDirectory);
        var nodeName = values.GetValueOrDefault("node-name", Environment.MachineName);
        if (nodeName.Length is < 1 or > 128 || nodeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '+' or '-')))
        {
            throw new TerraBaseConfigurationException("node-name must be 1 to 128 ASCII letters, digits, dots, underscores, colons, pluses or hyphens");
        }

        long? memoryBudget = null;
        if (values.TryGetValue("memory-budget", out var rawBudget))
        {
            if (!long.TryParse(rawBudget, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes < 1024 * 1024)
            {
                throw new TerraBaseConfigurationException("memory-budget must be an integer of at least 1048576 bytes");
            }

            memoryBudget = bytes;
        }
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
        var (authIssuer, authAudiences) = ParseAuth(values, clientPort);

        var legacyOwner = values.GetValueOrDefault("management-legacy-owner");
        if (legacyOwner is not null && (string.IsNullOrWhiteSpace(legacyOwner) || legacyOwner.Length > MaxUserIdLength || legacyOwner.Any(char.IsControl)))
        {
            throw new TerraBaseConfigurationException(
                "management-legacy-owner must be a non-blank user id of at most 256 characters without control characters");
        }

        var peerPort = ParsePort(values, "peer-port");
        RequireGroup(values, "peer-port", peerPort, PeerTlsKeys);
        var peerTls = peerPort is null ? null : new PeerTlsFiles(values["peer-certificate"], values["peer-private-key"], values["peer-client-ca"]);
        var peerAddresses = ParsePeerAddresses(values);
        if (peerAddresses.Count > 0 && peerPort is null)
        {
            throw new TerraBaseConfigurationException("peer-addresses requires peer-port");
        }

        // Validate the remaining settings before allocating certificates.
        var adminPort = ParsePort(values, "admin-port") ?? DefaultAdminPort;
        var shutdownGrace = TimeSpan.FromSeconds(ParseShutdownGraceSeconds(values));
        var readinessCacheTtl = TimeSpan.FromSeconds(ParseReadinessCacheSeconds(values));
        X509Certificate2? clientCertificate = null;
        PeerCredentials.Snapshot? peerCredentialSnapshot = null;
        try
        {
            clientCertificate = clientPort is null ? null : LoadPem(
                () => X509Certificate2.CreateFromPemFile(values["client-certificate"], values["client-private-key"]),
                "client-certificate and client-private-key must name a readable PEM certificate and its private key");
            // Hand off this stable, validated pair; initialization must not reopen files already rotating.
            peerCredentialSnapshot = peerTls is null ? null : LoadPem(
                () => PeerCredentials.LoadStable(peerTls)
                    ?? throw new IOException("peer-certificate, peer-private-key or peer-client-ca kept changing while they were read"),
                "peer-certificate, peer-private-key and peer-client-ca must name readable, stable PEM files whose certificate chains to the authority for server and client authentication");

            return new TerraBaseOptions
            {
                AdminPort = adminPort,
                DataDirectory = dataDirectory,
                Region = region,
                NodeName = nodeName,
                MemoryBudget = memoryBudget ?? Math.Max(1024 * 1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4),
                ClientPort = clientPort,
                ClientCertificate = clientCertificate,
                AuthIssuer = authIssuer,
                AuthAudiences = authAudiences,
                ManagementCredentialHash = values.TryGetValue("management-credential-file", out var credentialFile)
                    ? ReadCredentialHash(credentialFile)
                    : ReadOnlyMemory<byte>.Empty,
                ManagementLegacyOwner = legacyOwner,
                PeerPort = peerPort,
                PeerTls = peerTls,
                PeerCredentialSnapshot = peerCredentialSnapshot,
                PeerAddresses = peerAddresses,
                ShutdownGrace = shutdownGrace,
                ReadinessCacheTtl = readinessCacheTtl,
            };
        }
        catch
        {
            clientCertificate?.Dispose();
            peerCredentialSnapshot?.Dispose();
            throw;
        }
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

    private static int ParseReadinessCacheSeconds(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("readiness-cache-seconds", out var raw))
        {
            return DefaultReadinessCacheSeconds;
        }

        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= MaxReadinessCacheSeconds
            ? seconds
            : throw new TerraBaseConfigurationException($"readiness-cache-seconds must be an integer between 1 and {MaxReadinessCacheSeconds}");
    }

    // ts-auth tokens authenticate Management API users, which only the client listener serves.
    private static (string? Issuer, string[] Audiences) ParseAuth(IReadOnlyDictionary<string, string> values, int? clientPort)
    {
        var issuer = values.GetValueOrDefault("auth-issuer");
        var audience = values.GetValueOrDefault("auth-audience");
        if (issuer is null && audience is null)
        {
            return (null, []);
        }

        if (issuer is null || audience is null)
        {
            throw new TerraBaseConfigurationException("auth-issuer and auth-audience must be set together");
        }

        if (clientPort is null)
        {
            throw new TerraBaseConfigurationException("auth-issuer requires client-port");
        }

        // iss is compared exactly, so whitespace Uri parsing would forgive (" https://…") is refused, not trimmed.
        if (issuer.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new TerraBaseConfigurationException(
                "auth-issuer must be an absolute https URL without whitespace, user information, query or fragment");
        }

        // Control characters are checked before trimming, which would drop them at the edges of an entry.
        var audiences = audience.Split(',', StringSplitOptions.TrimEntries);
        return audience.Any(char.IsControl) || audiences.Any(value => value.Length == 0)
            ? throw new TerraBaseConfigurationException("auth-audience must be a comma-separated list of non-empty audiences without control characters")
            : (issuer, audiences);
    }

    // The file holds the secret alone; surrounding whitespace (a trailing newline) is ignored.
    private static byte[] ReadCredentialHash(string path)
    {
        string secret;
        try
        {
            secret = File.ReadAllText(path).Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TerraBaseConfigurationException("management-credential-file must name a readable file");
        }

        return secret.Length < MinManagementCredentialLength
            ? throw new TerraBaseConfigurationException("management-credential-file must hold a secret of at least 32 characters")
            : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    // A comma-separated list of distinct https://host:port origins.
    private static List<Uri> ParsePeerAddresses(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("peer-addresses", out var raw))
        {
            return [];
        }

        var addresses = new List<Uri>();
        foreach (var entry in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var address)
                || address.Scheme != Uri.UriSchemeHttps
                || address.Port == 0
                || address.PathAndQuery != "/"
                || address.Fragment.Length > 0
                || address.UserInfo.Length > 0
                || addresses.Contains(address))
            {
                throw new TerraBaseConfigurationException("peer-addresses must be a comma-separated list of distinct https://host:port addresses");
            }

            addresses.Add(address);
        }

        return addresses;
    }

    // Messages name the settings, never the paths or file contents.
    private static T LoadPem<T>(Func<T> load, string message)
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
