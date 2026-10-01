using System.Net;

namespace TerraScale.DotNet;

/// <summary>
/// Configures a <see cref="TerraScaleAuthClient"/> against a ts-auth deployment.
/// </summary>
public sealed record TerraScaleAuthClientOptions
{
    private const int MaxRetryAttemptsLimit = 8;
    private const int MaxSlugLength = 255;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ts-auth public origin, for example <c>https://auth.example.com</c>. There is
    /// no platform default: each deployment is addressed by its own origin.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Use the public auth reverse proxy when <see cref="BaseAddress"/> is
    /// <c>https://auth.terrascale.tech</c>. The default selects a regional
    /// auth endpoint using Atlas discovery and calls that endpoint directly.
    /// Custom auth origins continue to be used directly.
    /// </summary>
    public bool UseReverseProxy { get; set; }

    /// <summary>Atlas origin used to discover healthy regional auth endpoints.</summary>
    public Uri DiscoveryBaseAddress { get; set; } = new("https://atlas.terrascale.tech/");

    /// <summary>
    /// The instance's management API key (<c>sk_...</c>, returned once when the key
    /// is created). Required for every <c>/v1/{slug}/...</c> management operation;
    /// the public OIDC endpoints authenticate with client credentials instead.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The ts-auth instance slug every route is scoped to. It must match the
    /// instance the <see cref="ApiKey"/> belongs to; the service rejects mismatches.
    /// </summary>
    public string? InstanceSlug { get; set; }

    /// <summary>
    /// Number of transient retry attempts after the initial request. Only safe
    /// reads (GET) are retried; ts-auth mutations have no idempotency contract
    /// and are always sent exactly once. The default is zero and the maximum is eight.
    /// </summary>
    public int MaxRetryAttempts { get; set; }

    /// <summary>
    /// Base delay between retry attempts when the API does not return a Retry-After
    /// header. The maximum is 30 seconds.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public override string ToString() => "TerraScale auth client options";

    internal void Validate()
    {
        if (BaseAddress is null)
        {
            throw new ArgumentException("The ts-auth base address must be configured.", nameof(BaseAddress));
        }

        ValidateEndpoint(BaseAddress, nameof(BaseAddress));
        ValidateEndpoint(DiscoveryBaseAddress, nameof(DiscoveryBaseAddress));
        InstanceSlug = ValidateSlug(InstanceSlug);

        if (ApiKey is not null && string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("The ts-auth API key cannot be blank; leave it null for OIDC-only clients.", nameof(ApiKey));
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            ValidateAuthToken(ApiKey!, nameof(ApiKey));
        }

        if (MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), "Retry attempts must be greater than or equal to zero.");
        }

        if (MaxRetryAttempts > MaxRetryAttemptsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), $"Retry attempts must be less than or equal to {MaxRetryAttemptsLimit}.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "Retry delay must be greater than or equal to zero.");
        }

        if (RetryDelay > MaximumRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), $"Retry delay must be less than or equal to {MaximumRetryDelay.TotalSeconds:0} seconds.");
        }
    }

    private static string ValidateSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("The ts-auth instance slug must be configured.", nameof(InstanceSlug));
        }

        var trimmed = slug.Trim();
        if (trimmed.Any(char.IsControl) || trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The ts-auth instance slug must not contain control or whitespace characters.", nameof(InstanceSlug));
        }

        // A slug is a single route segment: separators and dot segments would
        // let a caller reshape the /v1/{slug}/... management route.
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed is "." or "..")
        {
            throw new ArgumentException("The ts-auth instance slug must not contain path separators or dot segments.", nameof(InstanceSlug));
        }

        if (trimmed.Length > MaxSlugLength)
        {
            throw new ArgumentException($"The ts-auth instance slug must be at most {MaxSlugLength} characters.", nameof(InstanceSlug));
        }

        return trimmed;
    }

    private static void ValidateAuthToken(string value, string name)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} must contain printable characters and no control characters.", name);
        }
    }

    internal static void ValidateEndpoint(Uri endpoint, string name)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The ts-auth base address must be absolute.", name);
        }

        if (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath is not ("" or "/"))
        {
            throw new ArgumentException("The ts-auth base address must be an origin without credentials, paths, queries, or fragments.", name);
        }

        if (endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The ts-auth base address must use HTTP or HTTPS.", name);
        }

        if (endpoint.Scheme == "http" && !IsLoopbackOrTestHost(endpoint.Host))
        {
            throw new ArgumentException("HTTP is allowed only for loopback or explicit test endpoints; production endpoints must use HTTPS.", name);
        }
    }

    private static bool IsLoopbackOrTestHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
