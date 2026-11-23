using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

public sealed class ApiException(int status, string code, string message, Exception? cause = null) : Exception(message, cause)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public TimeSpan? RetryAfter { get; init; }

    public static ApiException RateLimited(string message, TimeSpan retryAfter) =>
        new(429, "rate_limited", message) { RetryAfter = retryAfter };

    public static ApiException Internal() =>
        new(500, "internal_error", "An unexpected error occurred.");

    public static ApiException Unavailable(Exception? cause = null) =>
        new(503, "temporarily_unavailable", "The storage engine is unavailable; retry shortly.", cause) { RetryAfter = TimeSpan.FromSeconds(2) };

    public ErrorBody ToBody() => new(new ErrorDetail(this.Code, this.Message));
}

public sealed record ErrorBody([property: JsonPropertyName("error")] ErrorDetail Error);

public sealed record ErrorDetail(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
