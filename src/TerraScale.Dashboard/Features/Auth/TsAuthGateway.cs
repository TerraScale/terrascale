using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
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
                var user = await SessionUserInfoAsync(client, session, cancellationToken);
                return user.Subject == session.UserId && user.EmailVerified == true && Acceptable(user) && user.Actor?.Subject == session.Actor &&
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

        private static readonly TimeSpan DiscoveryReuse = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan KeysReuse = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan KeysRefreshFloor = TimeSpan.FromSeconds(30);
        private SharedRead<TsAuthOidcDiscovery>? discovery;
        private SharedRead<TsAuthJwkSet>? signingKeys;

        private Task<TsAuthOidcDiscovery> MetadataAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            return Shared(ref discovery, DiscoveryReuse, false, () => client.Oidc.GetDiscoveryDocumentAsync(CancellationToken.None)).WaitAsync(cancellationToken);
        }

        private Task<TsAuthJwkSet> SigningKeysAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            return Shared(ref signingKeys, KeysReuse, false, () => client.Oidc.GetJwksAsync(CancellationToken.None)).WaitAsync(cancellationToken);
        }

        private async Task<TsAuthJwkSet> SigningKeysAsync(TsAuthClient client, string? token, CancellationToken cancellationToken)
        {
            var keys = await SigningKeysAsync(client, cancellationToken);
            if (AuthJwtClaims.KeyId(token) is not { } kid || keys.Keys.Any(key => key.KeyId == kid) ||
                Volatile.Read(ref signingKeys) is { } current && time.GetUtcNow() - current.At < KeysRefreshFloor)
            {
                return keys;
            }

            return await Shared(ref signingKeys, KeysReuse, true, () => client.Oidc.GetJwksAsync(CancellationToken.None)).WaitAsync(cancellationToken);
        }

        private Task<T> Shared<T>(ref SharedRead<T>? slot, TimeSpan reuse, bool refresh, Func<Task<T>> read)
        {
            var now = time.GetUtcNow();
            var current = Volatile.Read(ref slot);
            if (!refresh && current is { Read.IsFaulted: false, Read.IsCanceled: false } && now - current.At < reuse)
            {
                return current.Read;
            }

            var next = new SharedRead<T>(now, read());
            return Interlocked.CompareExchange(ref slot, next, current) == current ? next.Read : Volatile.Read(ref slot)?.Read ?? next.Read;
        }

        private sealed record SharedRead<T>(DateTimeOffset At, Task<T> Read);

        private Task<TsAuthUserInfo> SessionUserInfoAsync(TsAuthClient client, SessionRecord session, CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "oidc-userinfo", session.AccessToken, session.UserId, session.Email),
                () => UserInfoAsync(client, session.AccessToken, cancellationToken));
        }

        private const string Tenant = "terrascale";
    }
}
