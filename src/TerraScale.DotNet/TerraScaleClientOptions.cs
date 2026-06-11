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

    /// <summary>
    /// Default TerraScale database id used by repository operations when one is not supplied per call.
    /// </summary>
    public string? DatabaseId { get; set; }

    /// <summary>
    /// Default TerraScale database environment id used by repository operations when one is not supplied per call.
    /// </summary>
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// Number of transient retry attempts after the initial request. The default is zero.
    /// </summary>
    public int MaxRetryAttempts { get; set; }

    /// <summary>
    /// Base delay between retry attempts when the API does not return a Retry-After header.
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

        if (string.IsNullOrWhiteSpace(ApiKey) && string.IsNullOrWhiteSpace(BearerToken))
        {
            throw new ArgumentException("Configure either an API key or a bearer token.");
        }

        if (!string.IsNullOrWhiteSpace(ApiKey) && string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new ArgumentException("ProjectId is required when ApiKey is configured.", nameof(ProjectId));
        }

        if (!string.IsNullOrWhiteSpace(EnvironmentId) && string.IsNullOrWhiteSpace(DatabaseId))
        {
            throw new ArgumentException("DatabaseId is required when EnvironmentId is configured.", nameof(DatabaseId));
        }

        if (MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), "Retry attempts must be greater than or equal to zero.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "Retry delay must be greater than or equal to zero.");
        }
    }
}
