using System.Net;

namespace TerraScale.DotNet;

/// <summary>
/// Configures origin and instance for a <see cref="TerraScaleAuthClient"/>.
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
    /// The ts-auth instance slug the OIDC routes and the deletion handoff are scoped to.
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

        BaseAddress = TerraScaleOrigin.Parse(BaseAddress, "The ts-auth base address", nameof(BaseAddress), TerraScaleService.Auth);
        InstanceSlug = ValidateSlug(InstanceSlug);
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
}
