namespace TerraScale.Auth.Billing;

using System.Net.Http.Headers;
using TerraScale.Auth.Configuration;

/// Minimal Stripe REST client: form-encoded bodies, bearer secret key, JSON responses.
/// No SDK on purpose — the three calls ts-auth needs are simpler than the dependency.
internal static class StripeClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true);

    public static Task<JsonElement> PostAsync(AuthOptions options, string path, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.StripeApiUrl, path))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.StripeSecretKey);
        return SendAsync(request, ct);
    }

    public static Task<JsonElement> GetAsync(AuthOptions options, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.StripeApiUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.StripeSecretKey);
        return SendAsync(request, ct);
    }

    private static async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new StripeApiException((int)response.StatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

/// A non-2xx Stripe API response.
internal sealed class StripeApiException(int statusCode, string body)
    : Exception($"Stripe API returned HTTP {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
}
