using System.Net;
using System.Text.Json;

namespace TerraScale.TerraBase.Testing;

/// <summary>Asserts expected failures: <c>{"error":{"code","message"}}</c> as JSON, never cached (docs/error-handling.md).</summary>
public static class ErrorAssert
{
    /// <summary>Asserts that <paramref name="sending"/> answers the error; a <see langword="null"/> <paramref name="message"/> means none.</summary>
    public static async Task ErrorAsync(Task<HttpResponseMessage> sending, HttpStatusCode status, string code, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(sending);
        using var response = await sending;
        var request = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}";
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var text = await response.Content.ReadAsStringAsync();
        // Anything but the envelope keeps its raw text as the code, so the assertion shows the actual response.
        var (actualCode, actualMessage) = contentType == "application/json" ? Envelope(text) : (text, null);
        var actual = new Answer(request, response.StatusCode, contentType, response.Headers.CacheControl?.NoStore == true, actualCode, actualMessage);
        await Assert.That(actual).IsEqualTo(new Answer(request, status, "application/json", true, code, message));
    }

    private static (string? Code, string? Message) Envelope(string text)
    {
        try
        {
            using var body = JsonDocument.Parse(text);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                    // The envelope omits an absent message; any other non-string, JSON null included, is its raw text.
                    ? (code.GetString(), error.TryGetProperty("message", out var message)
                        ? message.ValueKind == JsonValueKind.String ? message.GetString() : message.GetRawText()
                        : null)
                    : (text, null);
        }
        catch (JsonException)
        {
            return (text, null);
        }
    }

    private sealed record Answer(string Request, HttpStatusCode Status, string? ContentType, bool NoStore, string? Code, string? Message);
}
