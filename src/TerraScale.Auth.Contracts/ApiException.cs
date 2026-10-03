using System.Text.Json.Serialization;

namespace TerraScale.Auth.Contracts;

/// <summary>
/// A public API failure. Serialized as <c>{"error":{"code":"…","message":"…"}}</c>,
/// the envelope every ts-auth JSON endpoint shares.
/// </summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    /// <summary>HTTP status returned to the caller.</summary>
    public int Status { get; } = status;

    /// <summary>Stable, machine-readable error code.</summary>
    public string Code { get; } = code;

    /// <summary>An unexpected failure whose details stay in server logs.</summary>
    public static ApiException Internal() =>
        new(500, "internal_error", "An unexpected error occurred.");

    /// <summary>The storage authority could not complete the request.</summary>
    public static ApiException Unavailable() =>
        new(503, "temporarily_unavailable", "The storage engine is unavailable; retry shortly.");

    /// <summary>The serializable response body for this error.</summary>
    public ErrorBody ToBody() => new(new ErrorDetail(this.Code, this.Message));
}

/// <summary>The JSON error envelope.</summary>
public sealed record ErrorBody([property: JsonPropertyName("error")] ErrorDetail Error);

/// <summary>The code and human-readable message of an error.</summary>
public sealed record ErrorDetail(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
