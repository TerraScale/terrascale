using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Common.Live;
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
            ForgetUserInfo(session.AccessToken);
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
        private readonly SharedRead<TsAuthOidcDiscovery> discovery = new();
        private readonly SharedRead<TsAuthJwkSet> signingKeys = new();

        private Task<TsAuthOidcDiscovery> MetadataAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            return discovery.Get(time, DiscoveryReuse, false, () => client.Oidc.GetDiscoveryDocumentAsync(CancellationToken.None)).WaitAsync(cancellationToken);
        }

        private Task<TsAuthJwkSet> SigningKeysAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            return signingKeys.Get(time, KeysReuse, false, () => UsableKeysAsync(client)).WaitAsync(cancellationToken);
        }

        private Task<(TsAuthJwkSet Keys, bool Fetched)> TrackedKeysAsync(TsAuthClient client, bool refresh, CancellationToken cancellationToken)
        {
            return signingKeys.ReadAsync(time, KeysReuse, refresh, () => UsableKeysAsync(client)).WaitAsync(cancellationToken);
        }

        private static async Task<TsAuthJwkSet> UsableKeysAsync(TsAuthClient client)
        {
            var published = await client.Oidc.GetJwksAsync(CancellationToken.None);
            return AuthJwtClaims.KeySet(published) ?? throw new HttpRequestException("ts-auth published an unusable JSON Web Key Set.");
        }

        private async Task<TsAuthJwkSet> SigningKeysAsync(TsAuthClient client, string? token, CancellationToken cancellationToken)
        {
            var keys = await SigningKeysAsync(client, cancellationToken);
            if (AuthJwtClaims.KeyId(token) is not { } kid || keys.Keys.Any(key => key.KeyId == kid))
            {
                return keys;
            }

            return await signingKeys.Get(time, KeysReuse, true, () => UsableKeysAsync(client)).WaitAsync(cancellationToken);
        }



        private static readonly TimeSpan UserInfoReuse = TimeSpan.FromSeconds(30);
        private const int UserInfoEntries = 10_000;
        private readonly ConcurrentDictionary<string, (TsAuthUserInfo User, long At)> userInfo = new(StringComparer.Ordinal);

        private Task<TsAuthUserInfo> SessionUserInfoAsync(TsAuthClient client, SessionRecord session, CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "oidc-userinfo", session.AccessToken, session.UserId, session.Email),
                () => CachedUserInfoAsync(client, session.AccessToken, cancellationToken));
        }

        private async Task<TsAuthUserInfo> CachedUserInfoAsync(TsAuthClient client, string accessToken, CancellationToken cancellationToken)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
            if (LiveView.Streaming && userInfo.TryGetValue(key, out var cached) && time.GetElapsedTime(cached.At) < UserInfoReuse)
            {
                return cached.User;
            }

            var user = await UserInfoAsync(client, accessToken, cancellationToken);
            if (userInfo.Count >= UserInfoEntries)
            {
                foreach (var (stale, entry) in userInfo)
                {
                    if (time.GetElapsedTime(entry.At) >= UserInfoReuse)
                    {
                        userInfo.TryRemove(stale, out _);
                    }
                }
            }

            if (userInfo.Count < UserInfoEntries)
            {
                userInfo[key] = (user, time.GetTimestamp());
            }

            return user;
        }

        internal void ForgetUserInfo(string accessToken)
        {
            userInfo.TryRemove(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken))), out _);
        }

        private const string Tenant = "terrascale";
    }
}
