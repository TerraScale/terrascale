using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    // The dashboard owns its ts-auth HTTP client for OIDC, the
    // RFC 8693 exchange and the platform API.
    public sealed partial class TsAuthGateway(IDashboardIdentity identity, IHttpClientFactory http, TimeProvider time, TsAuthClient authClient) : IAuthGateway, IAuthOidcGateway, IAuthRefreshGateway, IAuthLogoutGateway
    {
        public async Task<bool> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            if (session.ExpiresAt <= time.GetUtcNow())
            {
                return false;
            }

            var client = Client();
            try
            {
                // The owning issuer's userinfo validates signature, access-token
                // type, revocation, the current user epoch and MFA state. ts-auth
                // does not serve an introspection endpoint.
                var user = await SessionUserInfoAsync(client, session, cancellationToken);
                return user.Subject == session.UserId && user.EmailVerified == true &&
                    string.Equals(user.Email?.Trim(), session.Email.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (SignInRejectedException)
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

        private TsAuthClient Client()
        {
            return authClient;
        }

        private Task<TsAuthOidcDiscovery> MetadataAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            return ServerTiming.PublicReadAsync((this, "oidc-metadata"), () => client.Oidc.GetDiscoveryDocumentAsync(cancellationToken));
        }

        private Task<TsAuthJwkSet> SigningKeysAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            return ServerTiming.PublicReadAsync((this, "oidc-keys"), () => client.Oidc.GetJwksAsync(cancellationToken));
        }

        private Task<TsAuthUserInfo> SessionUserInfoAsync(TsAuthClient client, SessionRecord session, CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "oidc-userinfo", session.AccessToken, session.UserId, session.Email),
                () => UserInfoAsync(client, session.AccessToken, cancellationToken));
        }

        // ts-auth serves one global issuer, https://auth.terrascale.tech/terrascale, from every region.
        private const string Tenant = "terrascale";
        private static readonly Uri AuthOrigin = new("https://auth.terrascale.tech/");
    }
}
