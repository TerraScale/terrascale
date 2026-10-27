using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthClient
{
    internal Task<T> SendPreparedJsonAsync<T>(HttpMethod method, string route, byte[] payload, JsonTypeInfo<T> responseType, CancellationToken caller) =>
        WithDeadlineAsync(token => SendPreparedJsonCoreAsync(method, route, payload, responseType, token), caller);

    internal Task<T> GetMetadataAsync<T>(string route, JsonTypeInfo<T> responseType, CancellationToken caller) =>
        WithDeadlineAsync(token => GetMetadataCoreAsync(route, responseType, token), caller);

    internal Task<T> SendOAuthFormAsync<T>(string route, IReadOnlyList<KeyValuePair<string, string>> values,
        AuthenticationHeaderValue? authorization, JsonTypeInfo<T> responseType, CancellationToken caller) =>
        WithDeadlineAsync(token => SendOAuthFormCoreAsync(route, values, authorization, responseType, token), caller);

    internal Task SendOAuthFormForSuccessAsync(string route, IReadOnlyList<KeyValuePair<string, string>> values,
        AuthenticationHeaderValue? authorization, CancellationToken caller) =>
        WithDeadlineAsync(token => SendOAuthFormForSuccessCoreAsync(route, values, authorization, token), caller);

    internal Task<T> GetWithBearerAsync<T>(string route, string accessToken, JsonTypeInfo<T> responseType, CancellationToken caller) =>
        WithDeadlineAsync(token => GetWithBearerCoreAsync(route, accessToken, responseType, token), caller);

    internal Task<TResponse> SendAuthenticatedJsonAsync<TRequest, TResponse>(HttpMethod method, string route, TRequest? body,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, AuthenticationHeaderValue authorization,
        HttpStatusCode expectedStatus, int maxRequestBytes, CancellationToken caller, string? stepUpProof = null) where TRequest : class =>
        WithDeadlineAsync(token => SendAuthenticatedJsonCoreAsync(method, route, body, requestType, responseType, authorization, expectedStatus, maxRequestBytes, token, stepUpProof), caller);

    internal Task SendAuthenticatedForStatusAsync<TRequest>(HttpMethod method, string route, TRequest? body,
        JsonTypeInfo<TRequest> requestType, AuthenticationHeaderValue authorization, HttpStatusCode expectedStatus,
        int maxRequestBytes, CancellationToken caller) where TRequest : class =>
        WithDeadlineAsync(token => SendAuthenticatedForStatusCoreAsync(method, route, body, requestType, authorization, expectedStatus, maxRequestBytes, token), caller);

    internal Task<bool> SendAuthenticatedForDeletionAsync(string route, AuthenticationHeaderValue authorization, CancellationToken caller) =>
        WithDeadlineAsync(token => SendAuthenticatedForDeletionCoreAsync(route, authorization, token), caller);

    internal Task<bool> SendForAccountDeletionAsync(HttpMethod method, string route, AuthenticationHeaderValue? authorization, string receipt, CancellationToken caller, string? deletionAuthorization = null) =>
        WithDeadlineAsync(token => SendForAccountDeletionCoreAsync(method, route, authorization, receipt, token, deletionAuthorization), caller);

    private Task<T> SendAsync<T>(HttpMethod method, string route, Func<HttpContent?>? bodyFactory, JsonTypeInfo<T> responseType,
        RequestAuthentication authentication, CancellationToken caller) =>
        WithDeadlineAsync(token => SendCoreAsync(method, route, bodyFactory, responseType, authentication, token), caller);

    private Task SendNoContentAsync(HttpMethod method, string route, Func<HttpContent?>? bodyFactory, RequestAuthentication authentication, CancellationToken caller) =>
        WithDeadlineAsync(token => SendNoContentCoreAsync(method, route, bodyFactory, authentication, token), caller);

    private static Task WithDeadlineAsync(Func<CancellationToken, Task> operation, CancellationToken caller) =>
        WithDeadlineAsync(async token => { await operation(token).ConfigureAwait(false); return true; }, caller);

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
