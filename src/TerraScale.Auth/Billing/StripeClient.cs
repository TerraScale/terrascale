namespace TerraScale.Auth.Billing;

using System.Net.Http.Headers;
using TerraScale.Auth.Configuration;

/// Minimal Stripe REST client: form-encoded bodies, bearer secret key, JSON responses.
/// No SDK on purpose — the three calls ts-auth needs are simpler than the dependency.
internal static class StripeClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);

    public static async Task<JsonElement> PostAsync(AuthOptions options, string path, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.StripeApiUrl, path))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.StripeSecretKey);
        // Awaited here so the using keeps the request (and its content) alive for the whole send.
        return await SendAsync(request, ct);
    }

    public static async Task<JsonElement> GetAsync(AuthOptions options, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.StripeApiUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.StripeSecretKey);
        return await SendAsync(request, ct);
    }

    private static async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new StripeApiException((int)response.StatusCode, body);
        try
        {
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new StripeResponseException(body, exception);
        }
    }
}

/// A non-2xx Stripe API response.
internal sealed class StripeApiException(int statusCode, string body)
    : Exception($"Stripe API returned HTTP {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
}

/// A 2xx response whose body is not JSON (an intercepting proxy, captive portal, or a
/// misrouted origin). Distinct from <see cref="StripeApiException"/> so callers can treat
/// it differently: the webhook rethrows it (500 → Stripe retries) while the checkout and
/// portal endpoints map it to a clean 502.
internal sealed class StripeResponseException(string body, Exception inner)
    : Exception($"Stripe returned an unparseable 2xx response: {(body.Length <= 200 ? body : body[..200])}", inner)
{
}
