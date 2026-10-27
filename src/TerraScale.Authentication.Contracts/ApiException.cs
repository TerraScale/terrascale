using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

/// <summary>
/// A public API failure. Serialized as <c>{"error":{"code":"…","message":"…"}}</c>,
/// the envelope every ts-auth JSON endpoint shares.
/// </summary>
public sealed class ApiException(int status, string code, string message, Exception? cause = null) : Exception(message, cause)
{
    /// <summary>HTTP status returned to the caller.</summary>
    public int Status { get; } = status;

    /// <summary>Stable, machine-readable error code.</summary>
    public string Code { get; } = code;

    /// <summary>How long a rate-limited caller must wait; the JSON envelope sends it as <c>Retry-After</c>.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>A 429 <c>rate_limited</c> answer that waits <paramref name="retryAfter"/>, the persisted budget's remaining window.</summary>
    public static ApiException RateLimited(string message, TimeSpan retryAfter) =>
        new(429, "rate_limited", message) { RetryAfter = retryAfter };

    /// <summary>An unexpected failure whose details stay in server logs.</summary>
    public static ApiException Internal() =>
        new(500, "internal_error", "An unexpected error occurred.");

    /// <summary>
    /// The storage authority could not complete the request; the caller may retry after two seconds. The
    /// <paramref name="cause"/> reaches server logs (as types and frames only), never the client.
    /// </summary>
    public static ApiException Unavailable(Exception? cause = null) =>
        new(503, "temporarily_unavailable", "The storage engine is unavailable; retry shortly.", cause) { RetryAfter = TimeSpan.FromSeconds(2) };

    /// <summary>The serializable response body for this error.</summary>
    public ErrorBody ToBody() => new(new ErrorDetail(this.Code, this.Message));
}

/// <summary>The JSON error envelope.</summary>
public sealed record ErrorBody([property: JsonPropertyName("error")] ErrorDetail Error);

/// <summary>The code and human-readable message of an error.</summary>
public sealed record ErrorDetail(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
