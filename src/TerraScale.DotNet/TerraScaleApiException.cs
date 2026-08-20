using System.Net;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Cysharp.Text;

namespace TerraScale.DotNet;

/// <summary>
/// Exception thrown for non-success TerraScale API responses.
/// </summary>
public sealed class TerraScaleApiException : HttpRequestException
{
    private const int MaxCapturedResponseBodyChars = 64 * 1024;

    internal TerraScaleApiException(
        HttpStatusCode statusCode,
        string message,
        string? errorCode,
        string? requestId,
        string? responseBody,
        TimeSpan? retryAfter,
        string? edgeRegion = null,
        string? servingRegion = null,
        string? routeMode = null,
        string? failoverFromRegion = null)
        : base(message, inner: null, statusCode)
    {
        ErrorCode = errorCode;
        RequestId = requestId;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
        Routing = TerraScaleRoutingObservation.Create(
            edgeRegion,
            servingRegion,
            routeMode,
            failoverFromRegion);
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

    /// <summary>
    /// Public regional routing metadata returned with the failure.
    /// </summary>
    public TerraScaleRoutingObservation? Routing { get; }

    /// <summary>
    /// Whether the API reported that no selected region could serve the request.
    /// </summary>
    public bool IsRegionalUnavailable =>
        StatusCode == HttpStatusCode.ServiceUnavailable &&
        string.Equals(ErrorCode, "project_region_unavailable", StringComparison.Ordinal);

    internal static async Task<TerraScaleApiException> CreateAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        var (code, message) = ReadError(body);
        return new TerraScaleApiException(
            response.StatusCode,
            message ?? $"TerraScale API returned HTTP {(int)response.StatusCode} {response.StatusCode}.",
            code,
            ReadHeader(response, "X-Request-Id"),
            body,
            response.Headers.RetryAfter?.Delta,
            ReadHeader(response, "X-TerraScale-Edge-Region"),
            ReadHeader(response, "X-TerraScale-Serving-Region"),
            ReadHeader(response, "X-TerraScale-Route-Mode"),
            ReadHeader(response, "X-TerraScale-Failover-From-Region"));
    }

    private static (string? Code, string? Message) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            var root = JsonElement.Parse(body);
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

    private static async Task<string?> ReadResponseBodyAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: false);
        using var builder = ZString.CreateStringBuilder();
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (builder.Length < MaxCapturedResponseBodyChars)
            {
                var remaining = Math.Min(buffer.Length, MaxCapturedResponseBodyChars - builder.Length);
                var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return builder.Length == 0 ? null : builder.ToString();
                }

                builder.Append(buffer, 0, read);
            }

            builder.AppendLine();
            builder.Append("... response body truncated after ");
            builder.Append(MaxCapturedResponseBodyChars);
            builder.Append(" characters.");
            return builder.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }
}
