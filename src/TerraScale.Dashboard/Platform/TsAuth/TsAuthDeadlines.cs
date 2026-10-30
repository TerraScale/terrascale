using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthClient
    {
        internal Task<T> GetMetadataAsync<T>(string route, JsonTypeInfo<T> responseType, CancellationToken caller)
        {
            return WithDeadlineAsync(token => GetMetadataCoreAsync(route, responseType, token), caller);
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
            int maxRequestBytes, CancellationToken caller) where TRequest : class
        {
            return WithDeadlineAsync(token => SendAuthenticatedForStatusCoreAsync(method, route, body, requestType, authorization, expectedStatus, maxRequestBytes, token), caller);
        }

        internal Task<bool> SendAuthenticatedForDeletionAsync(string route, AuthenticationHeaderValue authorization, CancellationToken caller)
        {
            return WithDeadlineAsync(token => SendAuthenticatedForDeletionCoreAsync(route, authorization, token), caller);
        }

        internal Task<bool> SendForAccountDeletionAsync(HttpMethod method, string route, AuthenticationHeaderValue? authorization, string receipt, CancellationToken caller, string? deletionAuthorization = null)
        {
            return WithDeadlineAsync(token => SendForAccountDeletionCoreAsync(method, route, authorization, receipt, token, deletionAuthorization), caller);
        }

        private static Task WithDeadlineAsync(Func<CancellationToken, Task> operation, CancellationToken caller)
        {
            return WithDeadlineAsync(async token => { await operation(token).ConfigureAwait(false); return true; }, caller);
        }

        private static async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(RequestDeadline);
            try
            {
                var result = await operation(deadline.Token).ConfigureAwait(false);
                caller.ThrowIfCancellationRequested();
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception error)
            {
                // Recheck even when synchronous parsing failed: cancellation wins over a malformed response.
                caller.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                    throw new TimeoutException("ts-auth did not answer within the request deadline.");
                if (error is OperationCanceledException { InnerException: TimeoutException })
                    throw new TimeoutException("ts-auth did not answer within the caller's HttpClient.Timeout.");
                throw;
            }
        }
    }
}
