using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Exception thrown for non-success TerraScale API responses.
/// </summary>
public sealed class TerraScaleApiException : HttpRequestException
{
    internal TerraScaleApiException(
        HttpStatusCode statusCode,
        string message,
        string? errorCode,
        string? requestId,
        string? responseBody,
        TimeSpan? retryAfter)
        : base(message, inner: null, statusCode)
    {
        ErrorCode = errorCode;
        RequestId = requestId;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// TerraScale error code when the API returned one.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Request id returned by the API or gateway.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>
    /// Safe raw response body captured for diagnostics.
    /// </summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Retry delay communicated by the API.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    internal static async Task<TerraScaleApiException> CreateAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var (code, message) = ReadError(body);
        return new TerraScaleApiException(
            response.StatusCode,
            message ?? $"TerraScale API returned HTTP {(int)response.StatusCode} {response.StatusCode}.",
            code,
            ReadHeader(response, "X-Request-Id"),
            body,
            response.Headers.RetryAfter?.Delta);
    }

    private static (string? Code, string? Message) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                return (
                    TryGetString(error, "code"),
                    TryGetString(error, "message") ?? TryGetString(error, "error"));
            }

            return (TryGetString(root, "code"), TryGetString(root, "message") ?? TryGetString(root, "error"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
