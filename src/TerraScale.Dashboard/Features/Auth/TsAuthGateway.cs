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
// OIDC/userinfo/revocation but has no headless-grant methods yet.
public sealed partial class TsAuthGateway(IDashboardIdentity identity, IHttpClientFactory http, TimeProvider time) : IAuthGateway, IAuthEnrollmentGateway, IAuthOidcGateway, IAuthRefreshGateway, IAuthLogoutGateway
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
        if (session.ExpiresAt <= time.GetUtcNow()) return false;
        var client = Client();
        try
        {
            // The owning issuer's userinfo validates signature, access-token
            // type, revocation, the current user epoch and MFA state. ts-auth
            // does not serve an introspection endpoint.
            var user = await client.Oidc.GetUserInfoAsync(session.AccessToken, cancellationToken);
            return user.Subject == session.UserId && user.EmailVerified == true &&
                user.Email?.Trim().ToLowerInvariant() == session.Email.Trim().ToLowerInvariant();
        }
        catch (TerraScaleAuthException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return false;
        }
    }

    public async Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        await Client().Oidc.RevokeTokenAsync(
            credentials.AuthClientId, credentials.AuthClientSecret, session.RefreshToken ?? session.AccessToken, cancellationToken);
    }

    private async Task<SessionRecord> GrantAsync(string grant, string email, string secretName, string secret, string ip, CancellationToken cancellationToken, string? pendingToken = null)
    {
        if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("Client address is required.", nameof(ip));
        var credentials = await identity.ReadAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        Authenticate(request, credentials);
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = grant,
            ["email"] = email,
            [secretName] = secret,
            ["ip_address"] = ip,
            ["scope"] = "openid email profile offline_access"
        };
        if (pendingToken is not null) fields["pending_token"] = pendingToken;
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) throw RateLimited(response);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) throw new SignInRejectedException();
        response.EnsureSuccessStatusCode();
        using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
        var tokens = document.RootElement.Deserialize<TerraScaleAuthTokenResponse>()
            ?? throw new HttpRequestException("ts-auth returned no tokens.");
        return await SessionAsync(tokens, credentials, null, cancellationToken);
    }

    private async Task HeadlessAsync(string action, object body, string ip, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var payload = JsonSerializer.SerializeToElement(body);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(payload)!;
        values["ip_address"] = ip;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(AuthOrigin, $"/{Tenant}/headless/v1/{action}"));
        Authenticate(request, credentials);
        request.Content = JsonContent.Create(values);
        using var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) throw RateLimited(response);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            using var error = await PlatformJson.ReadAsync(response.Content, cancellationToken);
            if (action == "password-reset/complete") ThrowPasswordPolicy(error.RootElement);
            throw new SignInRejectedException();
        }
        response.EnsureSuccessStatusCode();
    }

    private TerraScaleAuthClient Client() => new(http.CreateClient("platform"),
        new TerraScaleAuthClientOptions { BaseAddress = AuthOrigin, InstanceSlug = Tenant, UseReverseProxy = true });

    private static void Authenticate(HttpRequestMessage request, DashboardCredentials credentials) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.AuthClientId}:{credentials.AuthClientSecret}")));

    // ts-auth serves one global issuer, https://auth.terrascale.tech/terrascale, from every region. The pinned SDK
    // needs UseReverseProxy to call it directly instead of resolving a regional host through Atlas.
    private const string Tenant = "terrascale";
    private static readonly Uri AuthOrigin = new("https://auth.terrascale.tech/");
    private static readonly Uri TokenEndpoint = new(AuthOrigin, $"/{Tenant}/oauth/token");
}

public sealed class SignInRejectedException : Exception;
