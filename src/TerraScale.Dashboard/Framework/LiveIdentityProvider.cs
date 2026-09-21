using System.Net.Http.Headers;
using System.Text.Json;

namespace TerraScale.Dashboard;

public sealed record LiveIdentity(string Subject, string Email, string DisplayName);

/// <summary>RFC 7662 adapter for the approved dashboard identity provider.</summary>
public sealed class LiveIdentityProvider(HttpClient httpClient, DashboardSecurityOptions security)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LiveIdentity?> IntrospectAsync(string bearerToken, CancellationToken ct)
    {
        if (security.Identity is not { } options || string.IsNullOrWhiteSpace(bearerToken)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, options.IntrospectionEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = bearerToken }),
        };
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync<IntrospectionResponse>(body, Json, ct);
        if (result is not { Active: true } || string.IsNullOrWhiteSpace(result.Subject) || string.IsNullOrWhiteSpace(result.Email)) return null;
        if (!string.IsNullOrWhiteSpace(result.Issuer) && !string.Equals(result.Issuer, options.Issuer.ToString().TrimEnd('/'), StringComparison.Ordinal)) return null;
        return new(result.Subject, result.Email, string.IsNullOrWhiteSpace(result.Name) ? result.Email : result.Name);
    }

    private sealed record IntrospectionResponse(
        bool Active,
        string? Subject,
        string? Email,
        string? Name,
        string? Issuer);
}
