using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public interface IAuthGateway
{
    Task<SessionRecord> PasswordAsync(string email, string password, string ip, CancellationToken cancellationToken);
    Task<SessionRecord> EmailCodeAsync(string email, string code, string ip, CancellationToken cancellationToken);
    Task StartEmailCodeAsync(string email, string ip, CancellationToken cancellationToken);
    Task StartPasswordResetAsync(string email, string ip, CancellationToken cancellationToken);
    Task CompletePasswordResetAsync(string email, string code, string password, string ip, CancellationToken cancellationToken);
    Task<bool> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken);
    Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken);
}

// Headless grants are the owning ts-auth contract; ts-dotnet currently covers
// OIDC/userinfo/introspection/revocation but has no headless-grant methods yet.
public sealed class TsAuthGateway(IDashboardIdentity identity, IHttpClientFactory http, TimeProvider time) : IAuthGateway
{
    public Task<SessionRecord> PasswordAsync(string email, string password, string ip, CancellationToken cancellationToken) =>
        GrantAsync("urn:terrascale:params:oauth:grant-type:password", email, "password", password, ip, cancellationToken);

    public Task<SessionRecord> EmailCodeAsync(string email, string code, string ip, CancellationToken cancellationToken) =>
        GrantAsync("urn:terrascale:params:oauth:grant-type:email-code", email, "code", code, ip, cancellationToken);

    public Task StartEmailCodeAsync(string email, string ip, CancellationToken cancellationToken) =>
        HeadlessAsync("email-code/start", new { email }, ip, cancellationToken);

    public Task StartPasswordResetAsync(string email, string ip, CancellationToken cancellationToken) =>
        HeadlessAsync("password-reset/start", new { email }, ip, cancellationToken);

    public Task CompletePasswordResetAsync(string email, string code, string password, string ip, CancellationToken cancellationToken) =>
        HeadlessAsync("password-reset/complete", new { email, code, new_password = password }, ip, cancellationToken);

    public async Task<bool> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var client = Client(credentials, await OriginAsync(cancellationToken));
        var token = await client.Oidc.IntrospectTokenAsync(credentials.AuthClientId, credentials.AuthClientSecret, session.AccessToken, cancellationToken);
        return token.Active && token.Subject == session.UserId && token.ClientId == credentials.AuthClientId &&
            token.ExpiresAt > time.GetUtcNow().ToUnixTimeSeconds();
    }

    public async Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        await Client(credentials, await OriginAsync(cancellationToken)).Oidc.RevokeTokenAsync(
            credentials.AuthClientId, credentials.AuthClientSecret, session.RefreshToken ?? session.AccessToken, cancellationToken);
    }

    private async Task<SessionRecord> GrantAsync(string grant, string email, string secretName, string secret, string ip, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("Client address is required.", nameof(ip));
        var credentials = await identity.ReadAsync(cancellationToken);
        var origin = await OriginAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, $"/{Uri.EscapeDataString(credentials.AuthInstanceSlug)}/oauth/token"));
        Authenticate(request, credentials);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = grant,
            ["email"] = email,
            [secretName] = secret,
            ["ip_address"] = ip,
            ["scope"] = "openid email profile offline_access"
        });
        using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is 400 or 401) throw new SignInRejectedException();
        response.EnsureSuccessStatusCode();
        using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
        var tokens = document.RootElement.Deserialize<TerraScaleAuthTokenResponse>()
            ?? throw new HttpRequestException("ts-auth returned no tokens.");
        var client = Client(credentials, origin);
        var user = await client.Oidc.GetUserInfoAsync(tokens.AccessToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(user.Subject) || user.EmailVerified != true || string.IsNullOrWhiteSpace(user.Email) ||
            tokens.ExpiresIn is <= 0 or > 86400 || !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase)) throw new SignInRejectedException();
        var session = new SessionRecord(user.Subject, user.Name ?? user.Email, user.Email,
            tokens.AccessToken, tokens.RefreshToken, time.GetUtcNow().AddSeconds(tokens.ExpiresIn));
        if (!await IsActiveAsync(session, cancellationToken)) throw new SignInRejectedException();
        return session;
    }

    private async Task HeadlessAsync(string action, object body, string ip, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var payload = JsonSerializer.SerializeToElement(body);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(payload)!;
        values["ip_address"] = ip;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(await OriginAsync(cancellationToken), $"/{Uri.EscapeDataString(credentials.AuthInstanceSlug)}/headless/v1/{action}"));
        Authenticate(request, credentials);
        request.Content = JsonContent.Create(values);
        using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is 400 or 401) throw new SignInRejectedException();
        response.EnsureSuccessStatusCode();
    }

    private TerraScaleAuthClient Client(DashboardCredentials credentials, Uri origin) => new(http.CreateClient("platform"),
        new TerraScaleAuthClientOptions { BaseAddress = origin, InstanceSlug = credentials.AuthInstanceSlug });

    private static void Authenticate(HttpRequestMessage request, DashboardCredentials credentials) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.AuthClientId}:{credentials.AuthClientSecret}")));

    private async Task<Uri> OriginAsync(CancellationToken cancellationToken)
    {
        using var response = await http.CreateClient("platform").GetAsync("https://atlas.terrascale.tech/api/v1/auth/discovery", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
        if (document.RootElement.GetProperty("v").GetInt32() != 1)
            throw new HttpRequestException("Invalid auth discovery version.");
        foreach (var entry in document.RootElement.GetProperty("endpoints").EnumerateArray())
        {
            var region = entry.GetProperty("region").GetString();
            if (region is not null && Uri.TryCreate(entry.GetProperty("url").GetString(), UriKind.Absolute, out var origin) &&
                origin.Scheme == "https" && origin.IsDefaultPort && origin.UserInfo.Length == 0 &&
                origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 &&
                origin.Host.Equals($"{region.ToLowerInvariant()}.auth.terrascale.tech", StringComparison.OrdinalIgnoreCase))
                return origin;
        }
        throw new HttpRequestException("Atlas published no serving ts-auth origin.");
    }
}

public sealed class SignInRejectedException : Exception;
