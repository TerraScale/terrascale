using System.Net;

namespace TerraScale.DotNet;

/// <summary>
/// Configures origin, instance and credentials for a <see cref="TerraScaleAuthClient"/>.
/// Transport deadlines, body bounds and the no-retry policy are fixed by the SDK.
/// </summary>
public sealed record TerraScaleAuthClientOptions
{
    private const int MaxSlugLength = 255;

    /// <summary>
    /// ts-auth public origin, for example <c>https://auth.example.com</c>. There is
    /// no platform default: each deployment is addressed by its own origin.
    /// </summary>
    public Uri? BaseAddress { get; set; }

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

    /// <inheritdoc />
    public override string ToString() => "TerraScale auth client options";

    internal void Validate()
    {
        if (BaseAddress is null)
        {
            throw new ArgumentException("The ts-auth base address must be configured.", nameof(BaseAddress));
        }

        ValidateEndpoint(BaseAddress, nameof(BaseAddress));
        InstanceSlug = ValidateSlug(InstanceSlug);

        if (ApiKey is not null && string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("The ts-auth API key cannot be blank; leave it null for OIDC-only clients.", nameof(ApiKey));
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            ValidateAuthToken(ApiKey!, nameof(ApiKey));
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
