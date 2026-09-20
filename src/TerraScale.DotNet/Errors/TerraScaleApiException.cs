using System.Net;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Cysharp.Text;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet;

/// <summary>
/// Exception thrown for non-success TerraScale API responses.
/// </summary>
public sealed class TerraScaleApiException : HttpRequestException
{
    private const int MaxCapturedResponseBodyChars = 64 * 1024;
    private const string RedactedValue = "[REDACTED]";

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
        string? failoverFromRegion = null,
        string? placement = null,
        string? fix = null)
        : base(message, inner: null, statusCode)
    {
        ErrorCode = errorCode;
        Fix = fix;
        RequestId = requestId;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
        Placement = placement;
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
    /// TerraScale remediation supplied by the API for this failure.
    /// </summary>
    public string? Fix { get; }

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
    /// Database placement returned by the API, when the request was
    /// misdirected or the service supplied placement context.
    /// </summary>
    public string? Placement { get; }

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
        CancellationToken cancellationToken,
        TerraScaleErrorContract errorContract = TerraScaleErrorContract.TerraBase)
    {
        var body = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        var (code, message, fix) = ReadError(body, errorContract);
        var safeMessage = RedactDiagnostic(message);
        return new TerraScaleApiException(
            response.StatusCode,
            safeMessage ?? $"TerraScale API returned HTTP {(int)response.StatusCode} {response.StatusCode}.",
            code,
            ReadHeader(response, "X-Request-Id"),
            RedactResponseBody(body),
            ReadRetryAfter(response),
            ReadHeader(response, "X-TerraScale-Edge-Region"),
            ReadHeader(response, "X-TerraScale-Serving-Region") ?? ReadHeader(response, "X-TerraScale-Region"),
            ReadHeader(response, "X-TerraScale-Route-Mode"),
            ReadHeader(response, "X-TerraScale-Failover-From-Region"),
            ReadHeader(response, "X-TerraScale-Placement"),
            RedactDiagnostic(fix));
    }

    private static (string? Code, string? Message, string? Fix) ReadError(string? body, TerraScaleErrorContract errorContract)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null, null);
        }

        try
        {
            if (errorContract == TerraScaleErrorContract.DashboardProduct)
            {
                var dashboard = JsonSerializer.Deserialize(body, DashboardErrorJsonContext.Default.DashboardErrorResponse);
                return dashboard is { Error: { Code: not null, Message: not null, Category: not null } dashboardError }
                    ? (dashboardError.Code, dashboardError.Message, null)
                    : (null, null, null);
            }
            var envelope = JsonSerializer.Deserialize(body, TerraScaleContractJsonContext.Default.ErrorResponse);
            return envelope is { Error: { Code: not null, Message: not null, Fix: not null } error }
                ? (error.Code, error.Message, error.Fix)
                : (null, null, null);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    internal static string? RedactDiagnostic(string? value) =>
        value is null ? null : RedactTextAssignments(value);

    internal static string? RedactResponseBody(string? body)
    {
        if (body is null || body.Length == 0)
        {
            return body;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!ContainsSensitiveProperty(document.RootElement))
            {
                return body;
            }

            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output))
            {
                WriteRedacted(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(output.WrittenSpan);
        }
        catch (JsonException)
        {
            // Preserve the existing diagnostic behavior for malformed bodies,
            // while still removing obvious key=value credential forms.
            return RedactTextAssignments(body);
        }
    }

    private static bool ContainsSensitiveProperty(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSensitiveProperty(property.Name) || ContainsSensitiveProperty(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(ContainsSensitiveProperty);
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                return !string.Equals(RedactTextAssignments(value), value, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveProperty(property.Name))
                    {
                        writer.WriteStringValue(RedactedValue);
                    }
                    else
                    {
                        WriteRedacted(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(item, writer);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactTextAssignments(element.GetString() ?? string.Empty));
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private static bool IsSensitiveProperty(string name)
    {
        Span<char> normalized = stackalloc char[name.Length];
        var length = 0;
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized[length++] = char.ToLowerInvariant(character);
            }
        }

        var key = new string(normalized[..length]);
        return key switch
        {
            "authorization" or "proxyauthorization" or "cookie" or "setcookie" or
            "token" or "accesstoken" or "refreshtoken" or "idtoken" or "session" or
            "sessiontoken" or "bearertoken" or "authorizationtoken" or "securitytoken" or
            "apikey" or "secret" or "clientsecret" or "signingsecret" or "jwt" or "password" or
            "credential" or "credentials" or "privatekey" or "authentication" or "auth" => true,
            _ => false
        };
    }

    private static string RedactTextAssignments(string value)
    {
        // Structured JSON is handled above. This bounded fallback covers
        // plaintext diagnostics such as "token=..." without altering ordinary
        // malformed response text.
        var result = value;
        foreach (var key in SensitiveTextKeys)
        {
            var searchStart = 0;
            while (searchStart < result.Length)
            {
                var keyStart = result.IndexOf(key, searchStart, StringComparison.OrdinalIgnoreCase);
                if (keyStart < 0) break;
                var keyEnd = keyStart + key.Length;
                if ((keyStart == 0 || !char.IsLetterOrDigit(result[keyStart - 1])) &&
                    (keyEnd == result.Length || !char.IsLetterOrDigit(result[keyEnd])))
                {
                    var separator = keyEnd;
                    while (separator < result.Length && char.IsWhiteSpace(result[separator])) separator++;
                    if (separator < result.Length && (result[separator] == ':' || result[separator] == '='))
                    {
                        separator++;
                        while (separator < result.Length && char.IsWhiteSpace(result[separator])) separator++;
                        var quote = separator < result.Length && (result[separator] == '\'' || result[separator] == '"')
                            ? result[separator]
                            : '\0';
                        var valueStart = quote == '\0' ? separator : separator + 1;
                        var valueEnd = valueStart;
                        if (quote != '\0')
                        {
                            while (valueEnd < result.Length)
                            {
                                if (result[valueEnd] == quote && (valueEnd == valueStart || result[valueEnd - 1] != '\\')) break;
                                valueEnd++;
                            }
                        }
                        else if (IsHeaderCredentialKey(key))
                        {
                            // Authorization and cookie values commonly contain
                            // spaces (for example, "Bearer <key>"). Consume
                            // the whole header line so the second token cannot
                            // escape redaction.
                            while (valueEnd < result.Length && result[valueEnd] is not ('\r' or '\n')) valueEnd++;
                        }
                        else
                        {
                            while (valueEnd < result.Length && !char.IsWhiteSpace(result[valueEnd]) &&
                                   result[valueEnd] is not ('&' or ',' or ';')) valueEnd++;
                        }

                        if (valueEnd > valueStart)
                        {
                            result = result.Remove(valueStart, valueEnd - valueStart).Insert(valueStart, RedactedValue);
                            searchStart = valueStart + RedactedValue.Length;
                            continue;
                        }
                    }
                }

                searchStart = keyEnd;
            }
        }

        return result;
    }

    private static readonly string[] SensitiveTextKeys =
    [
        "authorization", "proxy-authorization", "cookie", "set-cookie", "token",
        "access_token", "refresh_token", "id_token", "session", "api_key", "api-key",
        "apikey", "session_token", "session-token", "bearer_token", "bearer-token",
        "authorization_token", "authorization-token", "security_token", "security-token",
        "auth_token", "auth-token", "secret", "client_secret", "signing_secret", "jwt",
        "password", "credential", "credentials", "authentication", "auth", "private_key"
    ];

    private static bool IsHeaderCredentialKey(string key) =>
        key is "authorization" or "proxy-authorization" or "cookie" or "set-cookie";

    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    internal static async Task<string?> ReadResponseBodyAsync(
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

            var probe = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (probe == 0)
            {
                return builder.ToString();
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
