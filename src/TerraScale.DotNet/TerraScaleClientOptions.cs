namespace TerraScale.DotNet;

/// <summary>
/// Configures a <see cref="TerraScaleClient"/>.
/// </summary>
public sealed record TerraScaleClientOptions
{
    /// <summary>
    /// TerraScale API base address, for example <c>https://api.terrascale.tech</c>.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("http://localhost:5294", UriKind.Absolute);

    /// <summary>
    /// Caller-provided TerraScale project API key. The SDK never creates or rotates credentials.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Caller-provided bearer token for public API calls.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// TerraScale project id used with project API-key authentication.
    /// </summary>
    public string? ProjectId { get; set; }

    internal void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("TerraScale base address must be absolute.", nameof(BaseAddress));
        }

        if (string.IsNullOrWhiteSpace(ApiKey) && string.IsNullOrWhiteSpace(BearerToken))
        {
            throw new ArgumentException("Configure either an API key or a bearer token.");
        }

        if (!string.IsNullOrWhiteSpace(ApiKey) && string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new ArgumentException("ProjectId is required when ApiKey is configured.", nameof(ProjectId));
        }
    }
}
