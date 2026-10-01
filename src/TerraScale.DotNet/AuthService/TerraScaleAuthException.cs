using System.Net;

namespace TerraScale.DotNet;

/// <summary>Reports a ts-auth HTTP failure using fixed safe diagnostics and typed status.</summary>
public sealed class TerraScaleAuthException : HttpRequestException
{
    internal TerraScaleAuthException(HttpStatusCode statusCode, string message,
        string? errorCode, TimeSpan? retryAfter)
        : base(message, inner: null, statusCode)
    {
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }

    /// <summary>A known fixed management or OAuth error code; unknown producer codes are omitted.</summary>
    public string? ErrorCode { get; }

    /// <summary>The service's retry delay, when supplied.</summary>
    public TimeSpan? RetryAfter { get; }
}
