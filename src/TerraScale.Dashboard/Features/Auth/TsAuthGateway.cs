using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed partial class TsAuthGateway(IDashboardIdentity identity, IHttpClientFactory http, TimeProvider time, TsAuthClient authClient) : IAuthGateway, IAuthOidcGateway, IAuthRefreshGateway, IAuthLogoutGateway
    {
        public async Task<SessionActivityOutcome> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            if (session.ExpiresAt <= time.GetUtcNow())
            {
                return new SessionEnded();
            }

            var outcome = await TrySessionUserInfoAsync(Client(), session, timeout: null, cancellationToken);
            return outcome switch
            {
                TsAuthUserInfo user => user.Subject == session.UserId && user.EmailVerified == true && Acceptable(user) && user.Actor?.Subject == session.Actor &&
                    string.Equals(user.Email?.Trim(), session.Email.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? new SessionStillActive()
                        : new SessionEnded(),
                UserInfoUnauthorized => new SessionEnded(),
                UserInfoUnavailable unavailable => new SessionActivityUnavailable(
                    unavailable.ErrorType,
                    unavailable.StatusCode,
                    unavailable.ErrorCode,
                    unavailable.Message),
            };
        }

        public async Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            ForgetUserInfo(session.AccessToken);
            var credentials = await identity.ReadAsync(cancellationToken);
            await Client().Oidc.RevokeTokenAsync(
                credentials.AuthClientId, credentials.AuthClientSecret, session.RefreshToken ?? session.AccessToken, cancellationToken);
        }

        public async Task<TokenRevocationOutcome> TryRevokeAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            ForgetUserInfo(session.AccessToken);
            var credentials = await identity.ReadAsync(cancellationToken);
            return await Client().Oidc.TryRevokeTokenAsync(
                credentials.AuthClientId, credentials.AuthClientSecret, session.RefreshToken ?? session.AccessToken, cancellationToken);
        }

        private TsAuthClient Client()
        {
            return authClient;
        }

        private static readonly TimeSpan DiscoveryReuse = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan KeysReuse = TimeSpan.FromMinutes(5);
        private readonly SharedRead<OidcDiscoveryOutcome> discovery = new(IsDiscovery);
        private readonly SharedRead<OidcKeySetOutcome> signingKeys = new(IsKeys);

        private static bool IsDiscovery(OidcDiscoveryOutcome outcome) => outcome switch
        {
            TsAuthOidcDiscovery => true,
            OidcDiscoveryUnavailable => false,
        };

        private static bool IsKeys(OidcKeySetOutcome outcome) => outcome switch
        {
            TsAuthJwkSet => true,
            OidcJwksUnavailable => false,
        };

        private async Task<TsAuthOidcDiscovery> MetadataAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            var outcome = await TryMetadataAsync(client, timeout: null, cancellationToken);
            return outcome switch
            {
                TsAuthOidcDiscovery value => value,
                OidcDiscoveryUnavailable failure => throw ToDependencyException(failure.ErrorType, failure.StatusCode, failure.ErrorCode, failure.Message),
            };
        }

        private Task<OidcDiscoveryOutcome> TryMetadataAsync(TsAuthClient client, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var read = discovery.Get(time, DiscoveryReuse, false, () => client.Oidc.TryGetDiscoveryMetadataAsync(cancellationToken: CancellationToken.None));
            return timeout is { } limit
                ? AwaitWithTimeoutAsync(read, limit, new OidcDiscoveryUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline."), cancellationToken)
                : read.WaitAsync(cancellationToken);
        }

        private async Task<TsAuthJwkSet> SigningKeysAsync(TsAuthClient client, CancellationToken cancellationToken)
        {
            var outcome = await TrySigningKeysAsync(client, timeout: null, cancellationToken);
            return outcome switch
            {
                TsAuthJwkSet value => value,
                OidcJwksUnavailable failure => throw ToDependencyException(failure.ErrorType, failure.StatusCode, failure.ErrorCode, failure.Message),
            };
        }

        private Task<OidcKeySetOutcome> TrySigningKeysAsync(TsAuthClient client, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var read = signingKeys.Get(time, KeysReuse, false, () => TryUsableKeysAsync(client));
            return timeout is { } limit
                ? AwaitWithTimeoutAsync(read, limit, new OidcJwksUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline."), cancellationToken)
                : read.WaitAsync(cancellationToken);
        }

        private async Task<(TsAuthJwkSet Keys, bool Fetched)> TrackedKeysAsync(TsAuthClient client, bool refresh, CancellationToken cancellationToken)
        {
            var (outcome, fetched) = await signingKeys.ReadAsync(time, KeysReuse, refresh, () => TryUsableKeysAsync(client)).WaitAsync(cancellationToken);
            return outcome switch
            {
                TsAuthJwkSet value => (value, fetched),
                OidcJwksUnavailable failure => throw ToDependencyException(failure.ErrorType, failure.StatusCode, failure.ErrorCode, failure.Message),
            };
        }

        private static async Task<OidcKeySetOutcome> TryUsableKeysAsync(TsAuthClient client)
        {
            var outcome = await client.Oidc.TryGetJwksAsync(cancellationToken: CancellationToken.None);
            return outcome switch
            {
                JsonElement published => AuthJwtClaims.KeySet(published) is { } parsed
                    ? parsed
                    : new OidcJwksUnavailable(nameof(HttpRequestException), HttpStatusCode.OK, "invalid_response", "ts-auth published an unusable JSON Web Key Set."),
                OidcJwksUnavailable unavailable => unavailable,
            };
        }

        private async Task<TsAuthJwkSet> SigningKeysAsync(TsAuthClient client, string? token, CancellationToken cancellationToken)
        {
            var outcome = await TrySigningKeysAsync(client, token, initialRead: null, deadlineAt: null, cancellationToken);
            return outcome switch
            {
                TsAuthJwkSet value => value,
                OidcJwksUnavailable failure => throw ToDependencyException(failure.ErrorType, failure.StatusCode, failure.ErrorCode, failure.Message),
            };
        }

        private async Task<OidcKeySetOutcome> TrySigningKeysAsync(
            TsAuthClient client,
            string? token,
            Task<OidcKeySetOutcome>? initialRead,
            DateTimeOffset? deadlineAt,
            CancellationToken cancellationToken)
        {
            var outcome = initialRead is not null
                ? await initialRead
                : await TrySigningKeysAsync(client, deadlineAt is { } initialDeadline ? Remaining(initialDeadline) : null, cancellationToken);
            return outcome switch
            {
                TsAuthJwkSet keys => await KeysForTokenAsync(client, keys, token, deadlineAt is { } refreshDeadline ? Remaining(refreshDeadline) : null, cancellationToken),
                OidcJwksUnavailable failure => failure,
            };
        }

        private async Task<OidcKeySetOutcome> KeysForTokenAsync(TsAuthClient client, TsAuthJwkSet keys, string? token, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            if (AuthJwtClaims.KeyId(token) is not { } kid || keys.Keys.Any(key => key.KeyId == kid))
            {
                return keys;
            }

            var refreshed = signingKeys.Get(time, KeysReuse, true, () => TryUsableKeysAsync(client));
            return timeout is { } limit
                ? await AwaitWithTimeoutAsync(refreshed, limit, new OidcJwksUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline."), cancellationToken)
                : await refreshed.WaitAsync(cancellationToken);
        }

        private async Task<T> AwaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, T onTimeout, CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
            {
                return await task;
            }

            if (timeout <= TimeSpan.Zero)
            {
                return onTimeout;
            }

            using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, time, timerCts.Token);
            var completed = await Task.WhenAny(task, delay);
            await timerCts.CancelAsync();
            if (completed == task)
            {
                return await task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return onTimeout;
        }

        private static readonly TimeSpan UserInfoReuse = TimeSpan.FromSeconds(30);
        private const int UserInfoEntries = 10_000;
        private readonly ConcurrentDictionary<string, (TsAuthUserInfo User, long At)> userInfo = new(StringComparer.Ordinal);

        private Task<UserInfoOutcome> TrySessionUserInfoAsync(TsAuthClient client, SessionRecord session, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "oidc-userinfo", session.AccessToken, session.UserId, session.Email),
                () => TryCachedUserInfoAsync(client, session.AccessToken, timeout, cancellationToken));
        }

        private async Task<UserInfoOutcome> TryCachedUserInfoAsync(TsAuthClient client, string accessToken, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
            if (LiveView.Streaming && userInfo.TryGetValue(key, out var cached) && time.GetElapsedTime(cached.At) < UserInfoReuse)
            {
                return cached.User;
            }

            var outcome = await client.Oidc.TryGetUserInfoAsync(accessToken, timeout, cancellationToken);
            (TsAuthUserInfo? User, UserInfoOutcome Outcome) resolved = outcome switch
            {
                TsAuthUserInfo value => (value, value),
                UserInfoUnauthorized unauthorized => (null, unauthorized),
                UserInfoUnavailable unavailable => (null, unavailable),
            };
            if (resolved.User is { } user)
            {
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
            }

            return resolved.Outcome;
        }

        private static Exception ToDependencyException(string errorType, HttpStatusCode? statusCode, string errorCode, string message)
        {
            if (errorType == nameof(TimeoutException))
            {
                return new TimeoutException(message);
            }

            if (statusCode is { } status)
            {
                return new TsAuthException(status, message, errorCode == "none" ? null : errorCode, null);
            }

            return new HttpRequestException(message);
        }

        internal void ForgetUserInfo(string accessToken)
        {
            userInfo.TryRemove(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken))), out _);
        }

        private static async Task ObservePublicReadsAsync(params Task[] reads)
        {
            await Task.WhenAll(reads).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        private const string Tenant = "terrascale";
    }
}
