using System.Net;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

// Every ts-auth call goes through the official ts-dotnet client: headless grants
// and enrollment, OIDC, the RFC 8693 exchange and the platform API.
public sealed partial class TsAuthGateway(IDashboardIdentity identity, IHttpClientFactory http, TimeProvider time) : IAuthGateway, IAuthEnrollmentGateway, IAuthOidcGateway, IAuthRefreshGateway, IAuthLogoutGateway
{
    public Task<SessionRecord> PasswordAsync(string email, string password, string ip, CancellationToken cancellationToken) =>
        GrantAsync((client, credentials, address) => client.Headless.SignInWithPasswordAsync(credentials.AuthClientId, credentials.AuthClientSecret,
            email, password, address, SignInScope, cancellationToken), ip, cancellationToken);

    public Task<SessionRecord> EmailCodeAsync(string email, string code, string ip, CancellationToken cancellationToken) =>
        GrantAsync((client, credentials, address) => client.Headless.SignInWithEmailCodeAsync(credentials.AuthClientId, credentials.AuthClientSecret,
            email, code, address, null, SignInScope, cancellationToken), ip, cancellationToken);

    public Task StartEmailCodeAsync(string email, string ip, CancellationToken cancellationToken) =>
        HeadlessAsync((client, credentials, address) => client.Headless.StartEmailCodeAsync(credentials.AuthClientId, credentials.AuthClientSecret,
            new(email, address), cancellationToken), ip, cancellationToken);

    public Task StartPasswordResetAsync(string email, string ip, CancellationToken cancellationToken) =>
        HeadlessAsync((client, credentials, address) => client.Headless.StartPasswordResetAsync(credentials.AuthClientId, credentials.AuthClientSecret,
            new(email, address), cancellationToken), ip, cancellationToken);

    public Task CompletePasswordResetAsync(string email, string code, string password, string ip, CancellationToken cancellationToken) =>
        HeadlessAsync((client, credentials, address) => client.Headless.CompletePasswordResetAsync(credentials.AuthClientId, credentials.AuthClientSecret,
            new(email, code, password, address), cancellationToken), ip, cancellationToken);

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

    private async Task<SessionRecord> GrantAsync(Func<TerraScaleAuthClient, DashboardCredentials, IPAddress, Task<TerraScaleAuthTokenResponse>> grant, string ip, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        TerraScaleAuthTokenResponse tokens;
        try { tokens = await grant(Client(), credentials, Address(ip)); }
        catch (TerraScaleAuthException error) when (error.StatusCode is HttpStatusCode.TooManyRequests) { throw RateLimited(error); }
        catch (TerraScaleAuthException error) when (error.StatusCode is HttpStatusCode.BadRequest) { throw new SignInRejectedException(); }
        return await SessionAsync(tokens, credentials, null, cancellationToken);
    }

    // ts-auth answers headless requests without revealing whether the account exists; only
    // its fixed codes are shown, never producer text that could echo submitted credentials.
    private async Task HeadlessAsync(Func<TerraScaleAuthClient, DashboardCredentials, IPAddress, Task> action, string ip, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        try { await action(Client(), credentials, Address(ip)); }
        catch (TerraScaleAuthException error) when (error.StatusCode is HttpStatusCode.TooManyRequests) { throw RateLimited(error); }
        catch (TerraScaleAuthException error) when (error.StatusCode is HttpStatusCode.BadRequest)
        {
            if (error.ErrorCode == "invalid_password") throw new AuthRequestRejectedException("Choose a different password.", "password");
            throw new SignInRejectedException();
        }
    }

    // The end user's address as the dashboard's trusted proxy reported it.
    private static IPAddress Address(string ip) =>
        IPAddress.TryParse(ip, out var address) ? address : throw new ArgumentException("Client address is required.", nameof(ip));

    private TerraScaleAuthClient Client() => new(http.CreateClient("platform"), new TerraScaleAuthClientOptions { BaseAddress = AuthOrigin, InstanceSlug = Tenant });

    // ts-auth serves one global issuer, https://auth.terrascale.tech/terrascale, from every region.
    private const string Tenant = "terrascale";
    private const string SignInScope = "openid email profile offline_access";
    private static readonly Uri AuthOrigin = new("https://auth.terrascale.tech/");
    private static readonly Uri TokenEndpoint = new(AuthOrigin, $"/{Tenant}/oauth/token");
}
