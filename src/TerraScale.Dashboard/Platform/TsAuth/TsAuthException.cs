using System.Net;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed class TsAuthException : HttpRequestException
    {
        internal TsAuthException(HttpStatusCode statusCode, string message,
            string? errorCode, TimeSpan? retryAfter, HttpResponseMessage? response = null)
            : base(message, inner: null, statusCode)
        {
            ErrorCode = errorCode;
            RetryAfter = retryAfter;
            RequestId = ResponseRequestId.Read(response);
        }

        public string? ErrorCode { get; }

        public string? RequestId { get; }

        public TimeSpan? RetryAfter { get; }
    }
}
