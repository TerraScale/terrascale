using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Auth;

public interface ITsAuthProviders
{
    // Null means unavailable, not an account with no linked providers.
    Task<IReadOnlyList<string>?> ListProvidersAsync(SessionRecord session, CancellationToken cancellationToken);
    Task UnlinkProviderAsync(SessionRecord session, string provider, string code, CancellationToken cancellationToken);
}

// Like the MFA routes, these account APIs are not yet exposed by the pinned ts-dotnet package.
// Keep the adapter on the platform client and the user's original sign-in bearer until the SDK ships them.
public sealed partial class TsAuthGateway : ITsAuthProviders
{
    public static bool IsSignInProvider(string provider) => provider is "github" or "google";
    public static string ProviderName(string provider) => provider == "github" ? "GitHub" : "Google";
    public static readonly string RequestSignInCodeUrl = new Uri(AuthOrigin, "ui/" + Tenant + "/sign-in").ToString();

    public async Task<IReadOnlyList<string>?> ListProvidersAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await ProviderCallAsync(HttpMethod.Get, session, "", null, cancellationToken);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var providers = body.RootElement.GetProperty("providers").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (providers.Any(provider => provider is null || !IsSignInProvider(provider)) || providers.Distinct().Count() != providers.Length)
                throw new IOException("ts-auth returned an invalid provider list.");
            return providers.Select(provider => provider!).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (Unreported(error, cancellationToken)) { return null; }
    }

    public async Task UnlinkProviderAsync(SessionRecord session, string provider, string code, CancellationToken cancellationToken)
    {
        if (!IsSignInProvider(provider)) throw new AccountAccessException("Choose a linked sign-in method.", 422);
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
            throw new AccountAccessException("Enter the six-digit code from your latest sign-in email.", 422);
        // Only validated ASCII digits enter the JSON; never retain or log the code.
        using var response = await ProviderCallAsync(HttpMethod.Delete, session, "/" + provider, "{\"code\":\"" + code + "\"}", cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new IOException("ts-auth did not confirm unlinking.");
    }

    private async Task<HttpResponseMessage> ProviderCallAsync(HttpMethod method, SessionRecord session, string suffix, string? json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(AuthOrigin, "v1/" + Tenant + "/account/providers" + suffix));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer(session));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var client = http.CreateClient("platform");
        var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 400 and < 500)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - time.GetUtcNow());
                var seconds = Math.Clamp((int)Math.Ceiling(wait?.TotalSeconds ?? 60), 1, 3600);
                throw new AccountAccessException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "The code is wrong or expired, or your sign-in has expired. Use the latest email code or sign in again.",
                    HttpStatusCode.TooManyRequests => $"Too many attempts. Try again in {seconds} seconds.",
                    HttpStatusCode.NotFound => "This sign-in method is unavailable. Refresh the page before trying again.",
                    _ => "This sign-in method could not be unlinked. Refresh the page and try again."
                }, status, response.StatusCode == HttpStatusCode.TooManyRequests ? seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            }
            response.EnsureSuccessStatusCode();
            throw new IOException("ts-auth did not confirm the request.");
        }
    }
}
