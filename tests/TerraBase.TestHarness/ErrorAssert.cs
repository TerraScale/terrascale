using System.Net;
using System.Text.Json;

namespace TerraBase.Testing;

public static class ErrorAssert
{
    public static async Task ErrorAsync(Task<HttpResponseMessage> sending, HttpStatusCode status, string code, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(sending);
        using var response = await sending;
        await ErrorAsync(response, status, code, message);
    }

    public static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var request = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}";
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var text = await response.Content.ReadAsStringAsync();
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
