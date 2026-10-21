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
    /// <summary>Port of the public listener, behind Fly Proxy.</summary>
    public const int DefaultPort = 8080;

    /// <summary>Port of the internal listener on Fly's private network.</summary>
    public const int DefaultInternalPort = 8081;

    /// <summary>Port of the metrics listener Fly scrapes (<c>[[metrics]]</c> in <c>fly.toml</c>).</summary>
    public const int DefaultMetricsPort = 9091;

    /// <summary>Default seconds shutdown waits for transactions to return their database connections.</summary>
    public const int DefaultShutdownGraceSeconds = 10;

    private const int MaxShutdownGraceSeconds = 3600;

    /// <summary>Default seconds completed readiness filesystem results are cached.</summary>
    public const int DefaultReadinessCacheSeconds = 1;

    private const int MaxReadinessCacheSeconds = 60;

    /// <summary>Default seconds of SQLite execution one query may spend before it is refused as too expensive.</summary>
    public const int DefaultQueryBudgetSeconds = 5;

    private const int MaxQueryBudgetSeconds = 600;

    /// <summary>Default milliseconds a write waits for its active peers' durable acknowledgment (docs/decisions/0010).</summary>
    public const int DefaultWriteWaitMilliseconds = 2000;

    /// <summary>Default directory for SQLite database files, relative to the working directory.</summary>
    public const string DefaultDataDirectory = "data";

    private const string EnvironmentPrefix = "TERRABASE_";


    private const int DefaultStorageQueue = 1024;
    internal const long MinMemoryBudget = 1024 * 1024;

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        ["port", "internal-port", "metrics-port", "data-directory", "memory-budget", "auth-issuer", "auth-audience", "write-wait-ms", "shutdown-grace-seconds", "readiness-cache-seconds", "query-budget-seconds", "storage-threads", "storage-queue", .. BackupSettings.Keys]);

    /// <summary>Every setting name, in <c>--name=value</c> form without the dashes.</summary>
    public static IReadOnlySet<string> SettingNames => Keys;

    // Generic-host arguments that WebApplicationFactory passes to the entry point. They are not
    // TerraBase settings and change nothing: the host is built without args.
    private static readonly FrozenSet<string> HostArguments = FrozenSet.Create(
        StringComparer.Ordinal, "environment", "applicationName", "contentRoot");

    /// <summary>
    /// Port of the public listener: plain HTTP behind Fly Proxy, which terminates TLS. Only tests change it (<c>0</c> picks a
    /// free port).
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Port of the internal listener: HTTP/2 without TLS on Fly's private network (<c>fly-local-6pn</c>), trusted as the
    /// platform operator. Only tests change it (<c>0</c> picks a free port).
    /// </summary>
    public int InternalPort { get; init; } = DefaultInternalPort;

    /// <summary>Port of the metrics listener; only tests change it (<c>0</c> picks a free port).</summary>
    public int MetricsPort { get; init; } = DefaultMetricsPort;

    /// <summary>Directory holding one SQLite file per database.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>This machine's region: <c>FLY_REGION</c>, or <c>local</c> off Fly.</summary>
    public string Region { get; init; } = LocalIdentity;

    /// <summary>This machine's ID: <c>FLY_MACHINE_ID</c>, or <c>local</c> off Fly. It names the member in the cell.</summary>
    public string MachineId { get; init; } = LocalIdentity;

    /// <summary>The Fly app this machine belongs to (<c>FLY_APP_NAME</c>), whose internal DNS lists the cell; <see langword="null"/> off Fly.</summary>
    public string? App { get; init; }

    private const string LocalIdentity = "local";

    internal static bool IsRegion([NotNullWhen(true)] string? region) => region is { Length: >= 1 and <= 63 } && region[0] is >= 'a' and <= 'z'
        && region[^1] != '-' && region.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>A Fly machine ID (or a Fly app name): lowercase letters, digits and inner hyphens, at most 63 characters.</summary>
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
    /// The time half of the engine's query work budget: SQLite execution time one query may spend, excluding queueing and
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

    /// <summary>Dedicated threads that run SQLite calls; defaults to the processor count.</summary>
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
    /// off Fly without <c>auth-issuer</c> trusts the platform's tokens, as <c>ts-terrabase</c> does.
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

        var (region, machineId, app) = FlyIdentity();
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
        if ((app == ProductionApp || (app is null && production)) && authIssuer is null)
        {
            authIssuer = PlatformAuthIssuer;
            authAudiences = [PlatformAuthAudience];
        }

        // A regional issuer or audience would turn every platform token away with 401 in production; previews and tests run their own ts-auth.
        if (app == ProductionApp && authIssuer is not null && (authIssuer != PlatformAuthIssuer || !authAudiences.Contains(PlatformAuthAudience)))
        {
            throw new TerraBaseConfigurationException(
                $"on {ProductionApp}, auth-issuer must be {PlatformAuthIssuer} and auth-audience must include {PlatformAuthAudience}: ts-auth issues every region's platform tokens with that pair");
        }

        return new TerraBaseOptions
        {
            Port = ParsePort(values, "port") ?? DefaultPort,
            InternalPort = ParsePort(values, "internal-port") ?? DefaultInternalPort,
            MetricsPort = ParsePort(values, "metrics-port") ?? DefaultMetricsPort,
            DataDirectory = dataDirectory,
            Region = region,
            MachineId = machineId,
            App = app,
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

    // The production app; previews are ts-pr-<n>.
    private const string ProductionApp = "ts-terrabase";

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

    // Fly sets these on every machine. A process elsewhere (development, tests) is a cell of its own, named local.
    private static (string Region, string MachineId, string? App) FlyIdentity()
    {
        var region = Environment.GetEnvironmentVariable("FLY_REGION");
        var machineId = Environment.GetEnvironmentVariable("FLY_MACHINE_ID");
        var app = Environment.GetEnvironmentVariable("FLY_APP_NAME");
        if (string.IsNullOrEmpty(region) && string.IsNullOrEmpty(machineId) && string.IsNullOrEmpty(app))
        {
            return (LocalIdentity, LocalIdentity, null);
        }

        if (!IsRegion(region) || !IsMachineId(machineId) || !IsMachineId(app))
        {
            throw new TerraBaseConfigurationException("FLY_REGION, FLY_MACHINE_ID and FLY_APP_NAME must name a region, a machine and an app together");
        }

        return (region, machineId, app);
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
