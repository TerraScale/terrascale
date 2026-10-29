using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TerraBase.Configuration;

public sealed class TerraBaseConfigurationException(string message) : Exception(message);

public sealed record TerraBaseOptions
{
    public const int DefaultPort = 8080;

    public const int DefaultInternalPort = 8081;

    public const int DefaultMetricsPort = 9091;

    public const int DefaultPostgresPort = 5432;

    public const int DefaultShutdownGraceSeconds = 10;

    private const int MaxShutdownGraceSeconds = 3600;

    public const int DefaultReadinessCacheSeconds = 1;

    private const int MaxReadinessCacheSeconds = 60;

    public const int DefaultQueryBudgetSeconds = 5;

    private const int MaxQueryBudgetSeconds = 600;


    public const string DefaultDataDirectory = "data";

    private const string EnvironmentPrefix = "TERRABASE_";


    private const int DefaultStorageQueue = 1024;
    internal const long MinMemoryBudget = 1024 * 1024;

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        ["port", "internal-port", "metrics-port", "postgres-port", "data-directory", "memory-budget", "auth-issuer", "auth-audience", "shutdown-grace-seconds", "readiness-cache-seconds", "query-budget-seconds", "storage-threads", "storage-queue", .. BackupSettings.Keys, .. FileStorageSettings.Keys]);

    public static IReadOnlySet<string> SettingNames => Keys;

    private static readonly FrozenSet<string> HostArguments = FrozenSet.Create(
        StringComparer.Ordinal, "environment", "applicationName", "contentRoot");

    public int Port { get; init; } = DefaultPort;

    public int InternalPort { get; init; } = DefaultInternalPort;

    public int MetricsPort { get; init; } = DefaultMetricsPort;

    public int PostgresPort { get; init; } = DefaultPostgresPort;

    public required string DataDirectory { get; init; }

    public string Region { get; init; } = LocalIdentity;

    public string MachineId { get; init; } = LocalIdentity;

    public string? PeersFile { get; init; }

    private const string LocalIdentity = "local";

    internal static bool IsRegion([NotNullWhen(true)] string? region) => region is { Length: >= 1 and <= 63 } && region[0] is >= 'a' and <= 'z'
        && region[^1] != '-' && region.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    internal static bool IsMachineId([NotNullWhen(true)] string? id) => id is { Length: >= 1 and <= 63 } && id[0] != '-' && id[^1] != '-'
        && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public const string PlatformAuthIssuer = "https://auth.terrascale.tech/terrascale";

    public const string PlatformAuthAudience = "https://api.terrascale.tech";

    public string? AuthIssuer { get; init; }

    public IReadOnlyList<string> AuthAudiences { get; init; } = [];

    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(DefaultShutdownGraceSeconds);

    public TimeSpan ReadinessCacheTtl { get; init; } = TimeSpan.FromSeconds(DefaultReadinessCacheSeconds);

    public TimeSpan QueryBudget
    {
        get;
        init => field = value >= TimeSpan.FromSeconds(1) && value <= TimeSpan.FromSeconds(MaxQueryBudgetSeconds)
            ? value
            : throw new TerraBaseConfigurationException($"query-budget-seconds must be between 1 and {MaxQueryBudgetSeconds} seconds");
    } = TimeSpan.FromSeconds(DefaultQueryBudgetSeconds);


    public required long MemoryBudget
    {
        get;
        init => field = ValidateInteger(value, "memory-budget", MinMemoryBudget, long.MaxValue);
    }

    public required int StorageThreads
    {
        get;
        init => field = (int)ValidateInteger(value, "storage-threads", 1, 1024);
    }

    public required int StorageQueue
    {
        get;
        init => field = (int)ValidateInteger(value, "storage-queue", 1, 1_000_000);
    }

    public BackupSettings? Backup { get; init; }

    public FileStorageSettings? FileStorage { get; init; }

    public byte[]? DatabaseMasterKey { get; init; }

    public static string EnvironmentName(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.LastOrDefault(argument => argument.StartsWith("--environment=", StringComparison.Ordinal))?[14..]
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;
    }

    public static void ReadSecretFiles()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.EndsWith("_FILE", StringComparison.Ordinal) && entry.Value is string { Length: > 0 } path
                && key[..^"_FILE".Length] is var name
                && (name.StartsWith(EnvironmentPrefix, StringComparison.Ordinal) || BackupSettings.TigrisVariables.Any(variable => variable.Variable == name)))
            {
                try
                {
                    Environment.SetEnvironmentVariable(name, File.ReadAllText(path).TrimEnd('\r', '\n'));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    if (name.StartsWith("TERRABASE_FILES_S3_", StringComparison.Ordinal))
                    {
                        Environment.SetEnvironmentVariable(name, null);
                        continue;
                    }
                    throw new TerraBaseConfigurationException($"{key} must name a readable file");
                }
            }
        }
    }

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

        if (BackupSettings.TigrisVariables.All(variable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable.Variable))))
        {
            foreach (var (variable, key) in BackupSettings.TigrisVariables)
            {
                values[key] = Environment.GetEnvironmentVariable(variable)!;
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

        return FromValues(values, EnvironmentName(args) != Environments.Development);
    }

    public static TerraBaseOptions FromValues(IReadOnlyDictionary<string, string> values, bool production = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var key in values.Keys)
        {
            if (!Keys.Contains(key))
            {
                throw new TerraBaseConfigurationException($"unknown setting '{key}'");
            }
        }

        var (region, machineId, peersFile) = ClusterIdentity();
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

        var (authIssuer, authAudiences) = ParseAuth(values);
        if (production && authIssuer is null)
        {
            authIssuer = PlatformAuthIssuer;
            authAudiences = [PlatformAuthAudience];
        }

        if (production && (authIssuer != PlatformAuthIssuer || !authAudiences.Contains(PlatformAuthAudience)))
        {
            throw new TerraBaseConfigurationException(
                $"outside Development, auth-issuer must be {PlatformAuthIssuer} and auth-audience must include {PlatformAuthAudience}: ts-auth issues every region's platform tokens with that pair");
        }

        return new TerraBaseOptions
        {
            Port = ParsePort(values, "port") ?? DefaultPort,
            InternalPort = ParsePort(values, "internal-port") ?? DefaultInternalPort,
            MetricsPort = ParsePort(values, "metrics-port") ?? DefaultMetricsPort,
            PostgresPort = ParsePort(values, "postgres-port") ?? DefaultPostgresPort,
            DataDirectory = dataDirectory,
            Region = region,
            MachineId = machineId,
            PeersFile = peersFile,
            MemoryBudget = ParseInteger(values, "memory-budget", MinMemoryBudget, long.MaxValue) ?? DefaultMemoryBudget(),
            StorageThreads = (int)(ParseInteger(values, "storage-threads", 1, 1024) ?? Math.Clamp(Environment.ProcessorCount, 1, 1024)),
            StorageQueue = (int)(ParseInteger(values, "storage-queue", 1, 1_000_000) ?? DefaultStorageQueue),
            AuthIssuer = authIssuer,
            AuthAudiences = authAudiences,
            ShutdownGrace = TimeSpan.FromSeconds(ParseShutdownGraceSeconds(values)),
            ReadinessCacheTtl = TimeSpan.FromSeconds(ParseReadinessCacheSeconds(values)),
            QueryBudget = TimeSpan.FromSeconds(ParseInteger(values, "query-budget-seconds", 1, MaxQueryBudgetSeconds) ?? DefaultQueryBudgetSeconds),
            Backup = BackupSettings.Parse(values),
            FileStorage = FileStorageSettings.Parse(values),
            DatabaseMasterKey = values.TryGetValue("master-key", out var masterKey) ? BackupSettings.ParseMasterKey(masterKey) : null,
        };
    }

    private static long DefaultMemoryBudget() => Math.Max(MinMemoryBudget, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);

    private static long? ParseInteger(IReadOnlyDictionary<string, string> values, string key, long min, long max)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new TerraBaseConfigurationException($"{key} must be an integer between {min} and {max}");
        }

        return ValidateInteger(value, key, min, max);
    }

    private static long ValidateInteger(long value, string key, long min, long max) =>
        value >= min && value <= max
            ? value
            : throw new TerraBaseConfigurationException($"{key} must be an integer between {min} and {max}");

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

    private static (string? Issuer, string[] Audiences) ParseAuth(IReadOnlyDictionary<string, string> values)
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

        if (issuer.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new TerraBaseConfigurationException(
                "auth-issuer must be an absolute https URL without whitespace, user information, query or fragment");
        }

        var audiences = audience.Split(',', StringSplitOptions.TrimEntries);
        return audience.Any(char.IsControl) || audiences.Any(value => value.Length == 0)
            ? throw new TerraBaseConfigurationException("auth-audience must be a comma-separated list of non-empty audiences without control characters")
            : (issuer, audiences);
    }

    private static (string Region, string MachineId, string? PeersFile) ClusterIdentity()
    {
        var region = Environment.GetEnvironmentVariable("TS_REGION");
        var node = Environment.GetEnvironmentVariable("TS_NODE");
        var peersFile = Environment.GetEnvironmentVariable("TS_PEERS_FILE");
        if (string.IsNullOrEmpty(region) && string.IsNullOrEmpty(node) && string.IsNullOrEmpty(peersFile))
        {
            return (LocalIdentity, LocalIdentity, null);
        }

        if (!IsRegion(region) || !IsMachineId(node) || string.IsNullOrEmpty(peersFile) || !Path.IsPathFullyQualified(peersFile))
        {
            throw new TerraBaseConfigurationException("TS_REGION, TS_NODE and TS_PEERS_FILE must name a region, a node and an absolute path together");
        }

        return (region, node, peersFile);
    }

    internal static bool TryParsePeerAddress(string value, [NotNullWhen(true)] out Uri? address) =>
        Uri.TryCreate(value, UriKind.Absolute, out address)
        && address.Scheme == Uri.UriSchemeHttp
        && address.Port != 0
        && address.PathAndQuery == "/"
        && address.Fragment.Length == 0
        && address.UserInfo.Length == 0;
}
