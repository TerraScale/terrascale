using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TerraBase.Configuration;

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
    /// <summary>Port of the public listener, behind the ts-orch edge.</summary>
    public const int DefaultPort = 8080;

    /// <summary>Port of the internal listener on the private mesh.</summary>
    public const int DefaultInternalPort = 8081;

    /// <summary>Port of the metrics listener.</summary>
    public const int DefaultMetricsPort = 9091;

    /// <summary>Port of the PostgreSQL wire listener.</summary>
    public const int DefaultPostgresPort = 5432;

    /// <summary>Port of the TerraBase wire protocol listener (docs/terrabase/wire-protocol.md).</summary>
    public const int DefaultWirePort = 7464;

    /// <summary>Default seconds shutdown waits for transactions to return their database connections.</summary>
    public const int DefaultShutdownGraceSeconds = 10;

    private const int MaxShutdownGraceSeconds = 3600;

    /// <summary>Default seconds completed readiness filesystem results are cached.</summary>
    public const int DefaultReadinessCacheSeconds = 1;

    private const int MaxReadinessCacheSeconds = 60;

    /// <summary>Default seconds of Turso execution one query may spend before it is refused as too expensive.</summary>
    public const int DefaultQueryBudgetSeconds = 5;

    private const int MaxQueryBudgetSeconds = 600;

    /// <summary>Default milliseconds a write waits for its active peers' durable acknowledgment (docs/decisions/0010).</summary>
    public const int DefaultWriteWaitMilliseconds = 2000;

    /// <summary>Default directory for Turso database files, relative to the working directory.</summary>
    public const string DefaultDataDirectory = "data";

    private const string EnvironmentPrefix = "TERRABASE_";


    private const int DefaultStorageQueue = 1024;
    internal const long MinMemoryBudget = 1024 * 1024;

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        ["port", "internal-port", "metrics-port", "postgres-port", "wire-port", "data-directory", "memory-budget", "auth-issuer", "auth-audience", "write-wait-ms", "shutdown-grace-seconds", "readiness-cache-seconds", "query-budget-seconds", "storage-threads", "storage-queue", .. BackupSettings.Keys, .. FileStorageSettings.Keys, .. ShapeStorageSettings.Keys, .. NatsSettings.Keys]);

    /// <summary>Every setting name, in <c>--name=value</c> form without the dashes.</summary>
    public static IReadOnlySet<string> SettingNames => Keys;

    // Generic-host arguments that WebApplicationFactory passes to the entry point. They are not
    // TerraBase settings and change nothing: the host is built without args.
    private static readonly FrozenSet<string> HostArguments = FrozenSet.Create(
        StringComparer.Ordinal, "environment", "applicationName", "contentRoot");

    /// <summary>
    /// Port of the public listener: plain HTTP behind the ts-orch edge, which terminates TLS. Tests (<c>0</c> picks a free port)
    /// and a second service on a shared host change it.
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Port of the internal listener: HTTP/2 without TLS on the private mesh, peers authenticated by the peer secret. Every
    /// member of a cell uses the same one. Tests (<c>0</c> picks a free port) and a second service on a shared host change it.
    /// </summary>
    public int InternalPort { get; init; } = DefaultInternalPort;

    /// <summary>Port of the metrics listener; tests (<c>0</c> picks a free port) and a second service on a shared host change it.</summary>
    public int MetricsPort { get; init; } = DefaultMetricsPort;

    /// <summary>
    /// Port of the PostgreSQL wire listener: plaintext behind the platform edge, which terminates TLS. Tests (<c>0</c> picks a
    /// free port) and a second service on a shared host change it.
    /// </summary>
    public int PostgresPort { get; init; } = DefaultPostgresPort;

    /// <summary>
    /// Port of the TerraBase wire protocol listener: plaintext behind the platform edge, which terminates TLS. Tests (<c>0</c>
    /// picks a free port) and a second service on a shared host change it.
    /// </summary>
    public int WirePort { get; init; } = DefaultWirePort;

    /// <summary>Directory holding one Turso file per database.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>This node's region: <c>TS_REGION</c> from ts-orch, or <c>local</c> outside a cluster.</summary>
    public string Region { get; init; } = LocalIdentity;

    /// <summary>This node's name: <c>TS_NODE</c> from ts-orch, or <c>local</c> outside a cluster. It names the member in the cell.</summary>
    public string MachineId { get; init; } = LocalIdentity;

    /// <summary>The file ts-orch keeps listing the cell's members (<c>TS_PEERS_FILE</c>); <see langword="null"/> outside a cluster.</summary>
    public string? PeersFile { get; init; }

    private const string LocalIdentity = "local";

    internal static bool IsRegion([NotNullWhen(true)] string? region) => region is { Length: >= 1 and <= 63 } && region[0] is >= 'a' and <= 'z'
        && region[^1] != '-' && region.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>A node name: lowercase letters, digits and inner hyphens, at most 63 characters.</summary>
    internal static bool IsMachineId([NotNullWhen(true)] string? id) => id is { Length: >= 1 and <= 63 } && id[0] != '-' && id[^1] != '-'
        && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>
    /// The issuer of every platform token ts-auth signs: its public origin plus <c>/terrascale</c>, the same in every region
    /// (there is no regional issuer). On <c>ts-terrabase</c>, <see cref="AuthIssuer"/> may only be this.
    /// </summary>
    public const string PlatformAuthIssuer = "https://auth.terrascale.tech/terrascale";

    /// <summary>
    /// The audience ts-auth puts on every token meant for TerraBase, the same in every region. On <c>ts-terrabase</c>,
    /// <see cref="AuthAudiences"/> must include it.
    /// </summary>
    public const string PlatformAuthAudience = "https://api.terrascale.tech";

    /// <summary>
    /// Issuer (<c>iss</c>) of the ts-auth access tokens that authenticate Management API users, an absolute HTTPS URL.
    /// Its signing keys come from <c>{issuer}/.well-known/openid-configuration</c>. <see langword="null"/> authenticates
    /// no user, so the Management API answers 401.
    /// </summary>
    public string? AuthIssuer { get; init; }

    /// <summary>Accepted audiences (<c>aud</c>) of those tokens; a token must name at least one. Set with <see cref="AuthIssuer"/>.</summary>
    public IReadOnlyList<string> AuthAudiences { get; init; } = [];

    /// <summary>
    /// How long shutdown waits for transactions to return their database connections. When it expires, shutdown
    /// returns but keeps the data directory owned until they do (or the process exits); it never closes them.
    /// </summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(DefaultShutdownGraceSeconds);

    /// <summary>How long completed readiness filesystem results are cached; shutdown bypasses this cache.</summary>
    public TimeSpan ReadinessCacheTtl { get; init; } = TimeSpan.FromSeconds(DefaultReadinessCacheSeconds);

    /// <summary>
    /// The time half of the engine's query work budget: Turso execution time one query may spend, excluding queueing and
    /// consumer time between rows. The 5 million VM instruction half is fixed.
    /// </summary>
    public TimeSpan QueryBudget
    {
        get;
        // Programmatic configuration gets the same bounds as the setting: a zero budget would refuse every query.
        init => field = value >= TimeSpan.FromSeconds(1) && value <= TimeSpan.FromSeconds(MaxQueryBudgetSeconds)
            ? value
            : throw new TerraBaseConfigurationException($"query-budget-seconds must be between 1 and {MaxQueryBudgetSeconds} seconds");
    } = TimeSpan.FromSeconds(DefaultQueryBudgetSeconds);

    /// <summary>
    /// How long a client write waits, after its commit, for every active peer of its region to durably
    /// acknowledge it before answering; on expiry it answers anyway (docs/decisions/0010). Zero answers right after the commit.
    /// </summary>
    public TimeSpan WriteWait { get; init; } = TimeSpan.FromMilliseconds(DefaultWriteWaitMilliseconds);

    /// <summary>
    /// Bytes of memory TerraBase plans for caches (<c>docs/memory.md</c>). Defaults to a quarter of the memory
    /// available to the process (container limits included).
    /// </summary>
    public required long MemoryBudget
    {
        get;
        init => field = ValidateInteger(value, "memory-budget", MinMemoryBudget, long.MaxValue);
    }

    /// <summary>Dedicated threads that run Turso calls; defaults to the processor count.</summary>
    public required int StorageThreads
    {
        get;
        init => field = (int)ValidateInteger(value, "storage-threads", 1, 1024);
    }

    /// <summary>Most storage transactions open at once; more are rejected as busy (HTTP 503).</summary>
    public required int StorageQueue
    {
        get;
        init => field = (int)ValidateInteger(value, "storage-queue", 1, 1_000_000);
    }

    /// <summary>Continuous backup to object storage (TS-633); <see langword="null"/> turns it off.</summary>
    public BackupSettings? Backup { get; init; }

    /// <summary>Independent customer-file identity delivered by OCI Vault; never the backup identity.</summary>
    public FileStorageSettings? FileStorage { get; init; }

    internal ShapeStorageSettings? ShapeStorage { get; init; }

    internal NatsSettings? Nats { get; init; }

    /// <summary>The master key wrapping database keys, independently of continuous backup.</summary>
    public byte[]? DatabaseMasterKey { get; init; }

    /// <summary>
    /// The host environment: <c>--environment=</c> (WebApplicationFactory's argument override), then <c>DOTNET_ENVIRONMENT</c>,
    /// then <c>ASPNETCORE_ENVIRONMENT</c>, as the slim host reads them; Production when none is set.
    /// </summary>
    public static string EnvironmentName(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.LastOrDefault(argument => argument.StartsWith("--environment=", StringComparison.Ordinal))?[14..]
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;
    }

    /// <summary>
    /// Replaces every secret variable (<c>TERRABASE_*</c> and the Tigris bucket variables) that has a <c>&lt;NAME&gt;_FILE</c>
    /// companion with that file's contents, trailing newline trimmed: a host that mounts secrets as files (OCI Vault on gru)
    /// delivers them this way. The file wins over the variable. Call before <see cref="FromProcess"/>.
    /// </summary>
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
                        // Missing file credentials make only file transfers unavailable. Never retain a stale value.
                        Environment.SetEnvironmentVariable(name, null);
                        continue;
                    }
                    // Names the variable, never the file's contents.
                    throw new TerraBaseConfigurationException($"{key} must name a readable file");
                }
            }
        }
    }

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

        // Only the whole set Fly sets for an attached bucket: a lone AWS_REGION from unrelated tooling is not a backup bucket.
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

    /// <summary>
    /// Validates settings keyed by their argument name. <paramref name="production"/>: the host is not Development, so a node
    /// without <c>auth-issuer</c> trusts the platform's tokens.
    /// </summary>
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

        // Outside Development only the platform pair: another issuer would let its holder mint tokens TerraBase trusts, and
        // a regional one would turn every platform token away with 401. Tests run their own ts-auth in Development.
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
            WirePort = ParsePort(values, "wire-port") ?? DefaultWirePort,
            DataDirectory = dataDirectory,
            Region = region,
            MachineId = machineId,
            PeersFile = peersFile,
            MemoryBudget = ParseInteger(values, "memory-budget", MinMemoryBudget, long.MaxValue) ?? DefaultMemoryBudget(),
            StorageThreads = (int)(ParseInteger(values, "storage-threads", 1, 1024) ?? Math.Clamp(Environment.ProcessorCount, 1, 1024)),
            StorageQueue = (int)(ParseInteger(values, "storage-queue", 1, 1_000_000) ?? DefaultStorageQueue),
            AuthIssuer = authIssuer,
            AuthAudiences = authAudiences,
            WriteWait = TimeSpan.FromMilliseconds(ParseInteger(values, "write-wait-ms", 0, 60_000) ?? DefaultWriteWaitMilliseconds),
            ShutdownGrace = TimeSpan.FromSeconds(ParseShutdownGraceSeconds(values)),
            ReadinessCacheTtl = TimeSpan.FromSeconds(ParseReadinessCacheSeconds(values)),
            QueryBudget = TimeSpan.FromSeconds(ParseInteger(values, "query-budget-seconds", 1, MaxQueryBudgetSeconds) ?? DefaultQueryBudgetSeconds),
            Backup = BackupSettings.Parse(values),
            FileStorage = FileStorageSettings.Parse(values),
            ShapeStorage = ShapeStorageSettings.Parse(values),
            Nats = NatsSettings.Parse(values),
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

    // ts-auth tokens authenticate Management API users.
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

    // ts-orch sets these on every task. A process elsewhere (development, tests) is a cell of its own, named local.
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

    // An http://host:port origin of another member's internal listener.
    internal static bool TryParsePeerAddress(string value, [NotNullWhen(true)] out Uri? address) =>
        Uri.TryCreate(value, UriKind.Absolute, out address)
        && address.Scheme == Uri.UriSchemeHttp
        && address.Port != 0
        && address.PathAndQuery == "/"
        && address.Fragment.Length == 0
        && address.UserInfo.Length == 0;
}
