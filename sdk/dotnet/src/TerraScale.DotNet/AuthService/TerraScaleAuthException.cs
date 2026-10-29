using System.Net;

namespace TerraScale.DotNet;

/// <summary>Reports a ts-auth HTTP failure using fixed safe diagnostics and typed status.</summary>
public sealed class TerraScaleAuthException : HttpRequestException
{
    internal TerraScaleAuthException(HttpStatusCode statusCode, string message,
        string? errorCode, TimeSpan? retryAfter, HttpResponseMessage? response = null)
        : base(message, inner: null, statusCode)
    {
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
        RequestId = ResponseRequestId.Read(response);
    }

    /// <summary>A known fixed management or OAuth error code; unknown producer codes are omitted.</summary>
    public string? ErrorCode { get; }

    /// <summary>The response's canonical ULID from X-Request-ID; null when absent, invalid or ambiguous.</summary>
    public string? RequestId { get; }

    /// <summary>The service's retry delay, when supplied.</summary>
    public TimeSpan? RetryAfter { get; }
}
