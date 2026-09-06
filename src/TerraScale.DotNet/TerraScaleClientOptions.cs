using System.Net;

namespace TerraScale.DotNet;

/// <summary>
/// Configures a <see cref="TerraScaleClient"/>.
/// </summary>
public sealed record TerraScaleClientOptions
{
    private const int MaxRetryAttemptsLimit = 8;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TerraScale API base address, for example <c>https://api.terrascale.tech</c>.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("https://api.terrascale.tech", UriKind.Absolute);

    /// <summary>Tenant selector for project-scoped operations; the server verifies membership.</summary>
    public string? TenantId { get; set; }

    /// <summary>Project selector for project-scoped operations; the server verifies authority.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Optional selected region. Failover affinity sends it as a routing hint.
    /// </summary>
    public string? PreferredRegion { get; set; }

    /// <summary>
    /// Controls whether a preferred region may fail over or must use its regional hostname.
    /// </summary>
    public TerraScaleRegionAffinity RegionAffinity { get; set; } = TerraScaleRegionAffinity.Failover;

    /// <summary>
    /// Controls automatic propagation of opaque write tokens to later document reads.
    /// </summary>
    public TerraScaleSessionConsistency SessionConsistency { get; set; } = TerraScaleSessionConsistency.None;

    /// <summary>
    /// Caller-provided TerraScale project API key used to authenticate SDK requests.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Caller-provided bearer token for public API calls.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Caller-provided factory for the current OAuth or machine-to-machine bearer token.
    /// The SDK invokes this before each HTTP request and retry. Automatic token acquisition and refresh stay in the caller boundary.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? BearerTokenProvider { get; set; }

    /// <summary>
    /// Number of transient retry attempts after the initial request. The default is zero and the maximum is eight.
    /// Responses requesting a Retry-After delay longer than 30 seconds are returned as errors without automatic retry.
    /// </summary>
    public int MaxRetryAttempts { get; set; }

    /// <summary>
    /// Base delay between retry attempts when the API does not return a Retry-After header. The maximum is 30 seconds.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Creates an idempotency key for mutating requests. Required before the SDK retries non-GET mutations.
    /// </summary>
    public Func<string>? IdempotencyKeyFactory { get; set; }

    /// <summary>
    /// Creates a client request id sent as X-Request-Id. Set to null to let the API gateway generate request ids.
    /// </summary>
    public Func<string>? RequestIdFactory { get; set; } = static () => Guid.NewGuid().ToString("N");

    internal void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("TerraScale base address must be absolute.", nameof(BaseAddress));
        }
        ValidateEndpoint(BaseAddress, nameof(BaseAddress));

        if ((TenantId is null) != (ProjectId is null))
            throw new ArgumentException("TenantId and ProjectId must be configured together.");
        TenantId = ValidateScopeSelector(TenantId, nameof(TenantId));
        ProjectId = ValidateScopeSelector(ProjectId, nameof(ProjectId));

        if (!Enum.IsDefined(RegionAffinity))
        {
            throw new ArgumentOutOfRangeException(nameof(RegionAffinity), "Unknown regional affinity mode.");
        }

        if (!Enum.IsDefined(SessionConsistency))
        {
            throw new ArgumentOutOfRangeException(nameof(SessionConsistency), "Unknown session consistency mode.");
        }

        var validatedRegion = ValidateRegion(PreferredRegion);
        PreferredRegion = validatedRegion;
        if (RegionAffinity == TerraScaleRegionAffinity.Strict)
        {
            if (validatedRegion is null)
            {
                throw new ArgumentException("Strict regional affinity requires PreferredRegion.", nameof(PreferredRegion));
            }

            BaseAddress = ResolveStrictRegionalBaseAddress(BaseAddress, validatedRegion);
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(ApiKey);
        var hasStaticBearerToken = !string.IsNullOrWhiteSpace(BearerToken);
        var hasBearerTokenProvider = BearerTokenProvider is not null;

        if (hasApiKey && (hasStaticBearerToken || hasBearerTokenProvider))
        {
            throw new ArgumentException("Configure either project API-key authentication or bearer-token authentication, not both.");
        }

        if (hasStaticBearerToken && hasBearerTokenProvider)
        {
            throw new ArgumentException("Configure either BearerToken or BearerTokenProvider, not both.", nameof(BearerTokenProvider));
        }

        if (MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), "Retry attempts must be greater than or equal to zero.");
        }

        if (MaxRetryAttempts > MaxRetryAttemptsLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetryAttempts),
                $"Retry attempts must be less than or equal to {MaxRetryAttemptsLimit}.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "Retry delay must be greater than or equal to zero.");
        }

        if (RetryDelay > MaximumRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetryDelay),
                $"Retry delay must be less than or equal to {MaximumRetryDelay.TotalSeconds:0} seconds.");
        }
    }

    private static string? ValidateScopeSelector(string? value, string name)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Any(character => character < 33 || character > 126))
            throw new ArgumentException("Scope selectors must contain 1 to 255 visible ASCII characters.", name);
        return value;
    }

    private static void ValidateEndpoint(Uri endpoint, string name)
    {
        if (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath is not ("" or "/"))
            throw new ArgumentException("TerraScale endpoints must be origins without credentials, paths, queries, or fragments.", name);
        if (endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("TerraScale endpoints must use HTTP or HTTPS.", name);
        if (endpoint.Scheme == "http" && !IsLoopbackOrTestHost(endpoint.Host))
            throw new ArgumentException("HTTP is allowed only for loopback or explicit test endpoints; production endpoints must use HTTPS.", name);
    }

    private static bool IsLoopbackOrTestHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static string? ValidateRegion(string? region)
    {
        if (region is null)
        {
            return null;
        }

        return TerraScaleRegionCodes.Validate(region, nameof(PreferredRegion));
    }

    private static Uri ResolveStrictRegionalBaseAddress(Uri baseAddress, string region)
    {
        var builder = new UriBuilder(baseAddress);
        if (string.Equals(builder.Host, "api.terrascale.tech", StringComparison.OrdinalIgnoreCase))
        {
            builder.Host = $"{region}.api.terrascale.tech";
        }
        else if (!string.Equals(builder.Host, $"{region}.api.terrascale.tech", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Strict regional affinity requires the global TerraScale API URL or the selected regional URL.",
                nameof(BaseAddress));
        }

        return builder.Uri;
    }
}

/// <summary>
/// Regional routing behavior for a preferred TerraScale region.
/// </summary>
public enum TerraScaleRegionAffinity
{
    /// <summary>
    /// Prefer the selected region but allow the public global route to fail over.
    /// </summary>
    Failover,

    /// <summary>
    /// Use the selected region's hostname and do not silently fail over.
    /// </summary>
    Strict
}

/// <summary>
/// Document session consistency behavior for a client instance.
/// </summary>
public enum TerraScaleSessionConsistency
{
    /// <summary>
    /// Do not retain or forward session consistency tokens.
    /// </summary>
    None,

    /// <summary>
    /// Retain the latest successful write token and forward it to later reads.
    /// </summary>
    Automatic
}
