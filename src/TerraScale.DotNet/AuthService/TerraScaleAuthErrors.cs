using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Maps ts-auth error responses onto <see cref="TerraScaleApiException"/>. The
/// management API uses the nested envelope <c>{"error":{code,message}}</c>;
/// the OAuth endpoints use the flat RFC 6749 shape
/// <c>{"error":"code","error_description":"message"}</c>.
/// </summary>
internal static class TerraScaleAuthErrors
{
    internal static async Task<TerraScaleApiException> CreateAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Bounded and redacted exactly like the main SDK: no raw server body
        // or message is stored on the exception.
        var body = await TerraScaleApiException.ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var (code, message) = ReadError(body);

        return new TerraScaleApiException(
            response.StatusCode,
            TerraScaleApiException.RedactDiagnostic(message)
            ?? $"ts-auth returned HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}.",
            code,
            requestId: null,
            responseBody: TerraScaleApiException.RedactResponseBody(body),
            retryAfter: ResolveRetryAfter(response));
    }

    /// <summary>Resolves Retry-After in both forms (delta seconds and HTTP
    /// date), clamped to zero, so date-only headers are not dropped.</summary>
    internal static TimeSpan? ResolveRetryAfter(HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response?.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            // Nested management envelope: {"error":{"code":...,"message":...}}.
            if (root.TryGetProperty("error", out var envelope) && envelope.ValueKind == JsonValueKind.Object)
            {
                var code = envelope.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString()
                    : null;
                var message = envelope.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
                return code is null && message is null ? (null, null) : (code, message);
            }

            // Flat OAuth shape: {"error":"invalid_grant","error_description":"..."}.
            if (root.TryGetProperty("error", out var flat) && flat.ValueKind == JsonValueKind.String)
            {
                var description = root.TryGetProperty("error_description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : null;
                return (flat.GetString(), description);
            }

            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
