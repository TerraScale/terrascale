using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthClient
    {
        internal Task<T> GetMetadataAsync<T>(string route, JsonTypeInfo<T> responseType, CancellationToken caller)
        {
            return WithDeadlineAsync(token => GetMetadataCoreAsync(route, responseType, MaxResponseBytes, token), caller);
        }

        internal Task<T> GetKeySetAsync<T>(string route, JsonTypeInfo<T> responseType, CancellationToken caller)
        {
            return WithDeadlineAsync(token => GetMetadataCoreAsync(route, responseType, MaxKeySetBytes, token), caller);
        }

        internal Task<T> SendOAuthFormAsync<T>(string route, IReadOnlyList<KeyValuePair<string, string>> values,
            AuthenticationHeaderValue? authorization, JsonTypeInfo<T> responseType, CancellationToken caller)
        {
            return WithDeadlineAsync(token => SendOAuthFormCoreAsync(route, values, authorization, responseType, token), caller);
        }

        internal Task SendOAuthFormForSuccessAsync(string route, IReadOnlyList<KeyValuePair<string, string>> values,
            AuthenticationHeaderValue? authorization, CancellationToken caller)
        {
            return WithDeadlineAsync(token => SendOAuthFormForSuccessCoreAsync(route, values, authorization, token), caller);
        }

        internal async Task<OAuthTokenOutcome> TrySendOAuthTokenAsync(
            string route,
            IReadOnlyList<KeyValuePair<string, string>> values,
            AuthenticationHeaderValue? authorization,
            JsonTypeInfo<TsAuthTokenResponse> responseType,
            TimeSpan? timeout,
            CancellationToken caller)
        {
            var effective = EffectiveDeadline(timeout);
            if (effective <= TimeSpan.Zero)
            {
                return new OAuthTokenNotStarted(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(effective);
            try
            {
                var result = await TrySendOAuthTokenCoreAsync(route, values, authorization, responseType, deadline.Token);
                caller.ThrowIfCancellationRequested();
                return result;
            }
            catch (HttpRequestException error)
            {
                caller.ThrowIfCancellationRequested();
                return error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                    ? new OAuthTokenNotStarted(nameof(HttpRequestException), error.StatusCode, "none", error.Message)
                    : new OAuthTokenUnavailable(nameof(HttpRequestException), error.StatusCode, "none", error.Message);
            }
            catch (IOException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OAuthTokenUnavailable(nameof(IOException), null, "none", error.Message);
            }
            catch (TimeoutException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OAuthTokenUnavailable(nameof(TimeoutException), null, "none", error.Message);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                return new OAuthTokenUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }
        }

        internal async Task<OidcDiscoveryOutcome> TryGetDiscoveryAsync(
            string route,
            JsonTypeInfo<TsAuthOidcDiscovery> responseType,
            TimeSpan? timeout,
            CancellationToken caller)
        {
            var effective = EffectiveDeadline(timeout);
            if (effective <= TimeSpan.Zero)
            {
                return new OidcDiscoveryUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(effective);
            try
            {
                var result = await TryGetDiscoveryCoreAsync(route, responseType, deadline.Token);
                caller.ThrowIfCancellationRequested();
                return result;
            }
            catch (HttpRequestException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OidcDiscoveryUnavailable(nameof(HttpRequestException), error.StatusCode, "none", error.Message);
            }
            catch (IOException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OidcDiscoveryUnavailable(nameof(IOException), null, "none", error.Message);
            }
            catch (TimeoutException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OidcDiscoveryUnavailable(nameof(TimeoutException), null, "none", error.Message);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                return new OidcDiscoveryUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }
        }

        internal async Task<OidcJwksOutcome> TryGetKeySetAsync(
            string route,
            JsonTypeInfo<System.Text.Json.JsonElement> responseType,
            TimeSpan? timeout,
            CancellationToken caller)
        {
            var effective = EffectiveDeadline(timeout);
            if (effective <= TimeSpan.Zero)
            {
                return new OidcJwksUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(effective);
            try
            {
                var result = await TryGetKeySetCoreAsync(route, responseType, deadline.Token);
                caller.ThrowIfCancellationRequested();
                return result;
            }
            catch (HttpRequestException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OidcJwksUnavailable(nameof(HttpRequestException), error.StatusCode, "none", error.Message);
            }
            catch (IOException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OidcJwksUnavailable(nameof(IOException), null, "none", error.Message);
            }
            catch (TimeoutException error)
            {
                caller.ThrowIfCancellationRequested();
                return new OidcJwksUnavailable(nameof(TimeoutException), null, "none", error.Message);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                return new OidcJwksUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }
        }

        internal async Task<UserInfoOutcome> TryGetUserInfoAsync(
            string route,
            string accessToken,
            JsonTypeInfo<TsAuthUserInfo> responseType,
            TimeSpan? timeout,
            CancellationToken caller)
        {
            var effective = EffectiveDeadline(timeout);
            if (effective <= TimeSpan.Zero)
            {
                return new UserInfoUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(effective);
            try
            {
                var result = await TryGetUserInfoCoreAsync(route, accessToken, responseType, deadline.Token);
                caller.ThrowIfCancellationRequested();
                return result;
            }
            catch (HttpRequestException error)
            {
                caller.ThrowIfCancellationRequested();
                return new UserInfoUnavailable(nameof(HttpRequestException), error.StatusCode, "none", error.Message);
            }
            catch (IOException error)
            {
                caller.ThrowIfCancellationRequested();
                return new UserInfoUnavailable(nameof(IOException), null, "none", error.Message);
            }
            catch (TimeoutException error)
            {
                caller.ThrowIfCancellationRequested();
                return new UserInfoUnavailable(nameof(TimeoutException), null, "none", error.Message);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                return new UserInfoUnavailable(nameof(TimeoutException), null, "none", "ts-auth did not answer within the request deadline.");
            }
        }

        private static TimeSpan EffectiveDeadline(TimeSpan? timeout) =>
            timeout is { } limit && limit < RequestDeadline ? limit : RequestDeadline;

        internal async Task<TokenRevocationOutcome> TrySendOAuthRevokeAsync(
            string route,
            IReadOnlyList<KeyValuePair<string, string>> values,
            AuthenticationHeaderValue? authorization,
            CancellationToken caller)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(RequestDeadline);
            try
            {
                var result = await TrySendOAuthRevokeCoreAsync(route, values, authorization, deadline.Token);
                caller.ThrowIfCancellationRequested();
                return result;
            }
            catch (HttpRequestException error)
            {
                caller.ThrowIfCancellationRequested();
                return error.StatusCode == HttpStatusCode.TooManyRequests
                    ? new TokenRevocationRateLimited(null, "none")
                    : new TokenRevocationUnavailable(nameof(HttpRequestException), error.StatusCode, "none");
            }
            catch (IOException)
            {
                caller.ThrowIfCancellationRequested();
                return new TokenRevocationUnavailable(nameof(IOException), null, "none");
            }
            catch (TimeoutException)
            {
                caller.ThrowIfCancellationRequested();
                return new TokenRevocationUnavailable(nameof(TimeoutException), null, "none");
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                return new TokenRevocationUnavailable(nameof(TimeoutException), null, "none");
            }
        }

        internal Task<T> GetWithBearerAsync<T>(string route, string accessToken, JsonTypeInfo<T> responseType, CancellationToken caller)
        {
            return WithDeadlineAsync(token => GetWithBearerCoreAsync(route, accessToken, responseType, token), caller);
        }

        internal Task<TResponse> SendAuthenticatedJsonAsync<TRequest, TResponse>(HttpMethod method, string route, TRequest? body,
            JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, AuthenticationHeaderValue authorization,
            HttpStatusCode expectedStatus, int maxRequestBytes, CancellationToken caller, string? stepUpProof = null) where TRequest : class
        {
            return WithDeadlineAsync(token => SendAuthenticatedJsonCoreAsync(method, route, body, requestType, responseType, authorization, expectedStatus, maxRequestBytes, token, stepUpProof), caller);
        }

        internal Task SendAuthenticatedForStatusAsync<TRequest>(HttpMethod method, string route, TRequest? body,
            JsonTypeInfo<TRequest> requestType, AuthenticationHeaderValue authorization, HttpStatusCode expectedStatus,
            int maxRequestBytes, CancellationToken caller, string? stepUpProof = null) where TRequest : class
        {
            return WithDeadlineAsync(token => SendAuthenticatedForStatusCoreAsync(method, route, body, requestType, authorization, expectedStatus, maxRequestBytes, token, stepUpProof), caller);
        }

        internal Task<bool> SendAuthenticatedForDeletionAsync(string route, AuthenticationHeaderValue authorization, CancellationToken caller)
        {
            return WithDeadlineAsync(token => SendAuthenticatedForDeletionCoreAsync(route, authorization, token), caller);
        }

        internal Task<bool> SendForAccountDeletionAsync(HttpMethod method, string route, AuthenticationHeaderValue? authorization, string receipt, CancellationToken caller, string? deletionAuthorization = null)
        {
            return WithDeadlineAsync(token => SendForAccountDeletionCoreAsync(method, route, authorization, receipt, deletionAuthorization, token), caller);
        }

        private static async Task WithDeadlineAsync(Func<CancellationToken, Task> operation, CancellationToken caller)
        {
            await WithDeadlineAsync(async token => { await operation(token); return true; }, caller);
        }

        private static async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(RequestDeadline);
            try
            {
                var result = await operation(deadline.Token);
                caller.ThrowIfCancellationRequested();
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (TaskCanceledException error)
            {
                caller.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("ts-auth did not answer within the request deadline.");
                }

                if (error.InnerException is TimeoutException)
                {
                    throw new TimeoutException("ts-auth did not answer within the caller's HttpClient.Timeout.");
                }

                throw;
            }
            catch (OperationCanceledException error)
            {
                caller.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("ts-auth did not answer within the request deadline.");
                }

                if (error.InnerException is TimeoutException)
                {
                    throw new TimeoutException("ts-auth did not answer within the caller's HttpClient.Timeout.");
                }

                throw;
            }
            catch (HttpRequestException)
            {
                caller.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("ts-auth did not answer within the request deadline.");
                }

                throw;
            }
            catch (IOException)
            {
                caller.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("ts-auth did not answer within the request deadline.");
                }

                throw;
            }
            catch (JsonException)
            {
                caller.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("ts-auth did not answer within the request deadline.");
                }

                throw;
            }
        }
    }
}
