using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace TerraBase.Configuration;

public readonly union OptionsResult(TerraBaseOptions, ConfigurationError);

public sealed record TerraBaseOptions
{
    public const int DefaultPort = 8080;

    public const int DefaultInternalPort = 8081;

    public const int DefaultMetricsPort = 9091;

    public const int DefaultPostgresPort = 5432;

    public const int DefaultQuicPort = 8443;

    public const int DefaultQueryBudgetSeconds = 5;

    public const string DefaultDataDirectory = "data";

    public const string LocalIdentity = "local";

    internal const long MinMemoryBudget = 1024 * 1024;

    private const int MaxQueryBudgetSeconds = 600;

    private static readonly string[] Keys =
    [
        "port", "internal-port", "metrics-port", "postgres-port", "quic-port", "data-directory", "memory-budget", "auth-issuer", "auth-audience",
        "query-budget-seconds", "storage-threads", "atlas-url", "atlas-publish-key-file", "control-key-file", "master-key", "master-keys", "platform-url",
        .. BackupSettings.Keys,
    ];

    public int Port { get; init; } = DefaultPort;

    public int InternalPort { get; init; } = DefaultInternalPort;

    public int MetricsPort { get; init; } = DefaultMetricsPort;

    public int PostgresPort { get; init; } = DefaultPostgresPort;

    public int QuicPort { get; init; } = DefaultQuicPort;

    public required string DataDirectory { get; init; }

    public required string EnvironmentName { get; init; }

    public string Region { get; init; } = LocalIdentity;

    public string MachineId { get; init; } = LocalIdentity;

    public string? PeersFile { get; init; }

    public string? AuthIssuer { get; init; }

    public IReadOnlyList<string> AuthAudiences { get; init; } = [];

    public TimeSpan QueryBudget { get; init; } = TimeSpan.FromSeconds(DefaultQueryBudgetSeconds);

    public required long MemoryBudget { get; init; }

    public required int StorageThreads { get; init; }

    public BackupSettings? Backup { get; init; }

    public KeyringSettings? DatabaseKeyring { get; init; }

    public Uri? AtlasUrl { get; init; }

    public Uri? PlatformUrl { get; init; }

    internal ECDsa? AtlasPublishKey { get; init; }

    internal ECDsa? ControlSnapshotKey { get; init; }

    public bool IsDevelopment => EnvironmentName == Environments.Development;

    public string StorageDirectory => Path.Combine(DataDirectory, "databases");

    public static string ResolveEnvironmentName(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.LastOrDefault(static argument => argument.StartsWith("--environment=", StringComparison.Ordinal))?[14..]
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;
    }

    public static bool IsRegion([NotNullWhen(true)] string? region) => region is { Length: >= 1 and <= 63 } && region[0] is >= 'a' and <= 'z'
        && region[^1] != '-' && region.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public static bool IsMachineId([NotNullWhen(true)] string? id) => id is { Length: >= 1 and <= 63 } && id[0] != '-' && id[^1] != '-'
        && id.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public static OptionsResult FromProcess(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (SettingValues.ReadSecretFiles(BackupSettings.TigrisVariables) is { } unreadable)
        {
            return unreadable;
        }

        var environmentName = ResolveEnvironmentName(args);
        var values = SettingValues.FromProcess(Keys, BackupSettings.TigrisVariables, args);
        var development = environmentName == Environments.Development;
        var (region, machineId, peersFile) = ClusterIdentity(values, development);
        var dataDirectory = DataDirectoryOf(values);
        var (issuer, audiences) = Auth(values);
        var keyring = KeyringSettings.Parse(values);
        var options = new TerraBaseOptions
        {
            Port = values.Port("port", DefaultPort),
            InternalPort = values.Port("internal-port", DefaultInternalPort),
            MetricsPort = values.Port("metrics-port", DefaultMetricsPort),
            PostgresPort = values.Port("postgres-port", DefaultPostgresPort),
            QuicPort = values.Port("quic-port", DefaultQuicPort),
            DataDirectory = dataDirectory,
            EnvironmentName = environmentName,
            Region = region,
            MachineId = machineId,
            PeersFile = peersFile,
            AuthIssuer = issuer,
            AuthAudiences = audiences,
            QueryBudget = TimeSpan.FromSeconds(values.Integer("query-budget-seconds", 1, MaxQueryBudgetSeconds, DefaultQueryBudgetSeconds)),
            MemoryBudget = values.Integer("memory-budget", MinMemoryBudget, long.MaxValue, DefaultMemoryBudget()),
            StorageThreads = (int)values.Integer("storage-threads", 1, 1024, Math.Clamp(Environment.ProcessorCount, 1, 1024)),
            Backup = BackupSettings.Parse(values),
            DatabaseKeyring = keyring,
            AtlasUrl = ParseServiceUrl(values, "atlas-url"),
            PlatformUrl = ParseServiceUrl(values, "platform-url"),
            AtlasPublishKey = values.Get("atlas-publish-key-file") is { } atlasKey ? PrivateKey(values, atlasKey, "TERRABASE_ATLAS_PUBLISH_KEY_FILE") : null,
            ControlSnapshotKey = values.Get("control-key-file") is { } controlKey ? PublicKey(values, controlKey) : null,
        };

        if (!development && issuer is null)
        {
            values.Fail("outside Development, TERRABASE_AUTH_ISSUER and TERRABASE_AUTH_AUDIENCE must name the ts-auth of this environment");
        }

        if (!development && keyring is null)
        {
            values.Fail("outside Development, TERRABASE_MASTER_KEY or TERRABASE_MASTER_KEYS_FILE must hold the database master keys");
        }

        if ((options.AtlasUrl is null) != (options.AtlasPublishKey is null))
        {
            values.Fail("TERRABASE_ATLAS_URL and TERRABASE_ATLAS_PUBLISH_KEY_FILE must be set together");
        }

        return values.Errors.Count > 0 ? new ConfigurationError(values.Errors[0]) : options;
    }

    private static long DefaultMemoryBudget() => Math.Max(MinMemoryBudget, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);

    private static string DataDirectoryOf(SettingValues values)
    {
        var configured = values.Get("data-directory") ?? DefaultDataDirectory;
        if (string.IsNullOrWhiteSpace(configured) || configured.Any(char.IsControl) || configured.Contains('\0', StringComparison.Ordinal))
        {
            values.Fail("data-directory must be a valid path");
            return DefaultDataDirectory;
        }

        return Path.GetFullPath(configured);
    }

    private static (string Region, string MachineId, string? PeersFile) ClusterIdentity(SettingValues values, bool development)
    {
        var region = Environment.GetEnvironmentVariable("TS_REGION");
        var node = Environment.GetEnvironmentVariable("TS_NODE");
        var peersFile = Environment.GetEnvironmentVariable("TS_PEERS_FILE");
        if (string.IsNullOrEmpty(region) && string.IsNullOrEmpty(node) && string.IsNullOrEmpty(peersFile))
        {
            return (LocalIdentity, LocalIdentity, null);
        }

        if (development && !string.IsNullOrEmpty(peersFile))
        {
            values.Fail("Development refuses TS_PEERS_FILE");
            return (LocalIdentity, LocalIdentity, null);
        }

        if (!IsRegion(region) || !IsMachineId(node) || (!development && (string.IsNullOrEmpty(peersFile) || !Path.IsPathFullyQualified(peersFile))))
        {
            values.Fail("TS_REGION, TS_NODE and TS_PEERS_FILE must name a region, a node and an absolute path together");
            return (LocalIdentity, LocalIdentity, null);
        }

        return (region, node, string.IsNullOrEmpty(peersFile) ? null : peersFile);
    }

    private static (string? Issuer, IReadOnlyList<string> Audiences) Auth(SettingValues values)
    {
        var issuer = values.Get("auth-issuer");
        var audience = values.Get("auth-audience");
        if (issuer is null && audience is null)
        {
            return (null, []);
        }

        if (issuer is null || audience is null)
        {
            values.Fail("auth-issuer and auth-audience must be set together");
            return (null, []);
        }

        if (issuer.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character))
            || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            values.Fail("auth-issuer must be an absolute https URL, or http on loopback, without whitespace, user information, query or fragment");
            return (null, []);
        }

        var audiences = audience.Split(',', StringSplitOptions.TrimEntries);
        if (audience.Any(char.IsControl) || audiences.Any(static value => value.Length == 0))
        {
            values.Fail("auth-audience must be a comma-separated list of non-empty audiences without control characters");
            return (null, []);
        }

        return (issuer, audiences);
    }

    private static Uri? ParseServiceUrl(SettingValues values, string key)
    {
        if (values.Get(key) is not { } url)
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && (address.Scheme != Uri.UriSchemeHttp || !address.IsLoopback))
            || address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0)
        {
            values.Fail(key + " must be an absolute https URL, or http on loopback, without user information, query or fragment");
            return null;
        }

        return address;
    }

    private static ECDsa? PrivateKey(SettingValues values, string path, string variable)
    {
        if (FileText.Read(path) is not { } pem || Pem.ImportP256(pem, requirePrivate: true) is not { } key)
        {
            values.Fail(variable + " must name a readable PKCS#8 PEM P-256 private key");
            return null;
        }

        return key;
    }

    private static ECDsa? PublicKey(SettingValues values, string path)
    {
        if (FileText.Read(path) is not { } pem || Pem.ImportP256(pem, requirePrivate: false) is not { } key)
        {
            values.Fail("TERRABASE_CONTROL_KEY_FILE must name a readable PEM P-256 public key");
            return null;
        }

        return key;
    }
}

internal static class Pem
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static ECDsa? ImportP256(string pem, bool requirePrivate)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (parameters.Curve.Oid.Value == P256Oid && (!requirePrivate || HasPrivate(key)))
            {
                return key;
            }
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
        }

        key.Dispose();
        return null;
    }

    private static bool HasPrivate(ECDsa key)
    {
        try
        {
            return key.ExportParameters(includePrivateParameters: true).D is { Length: > 0 };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
