using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;
using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthClient
{

    internal Task<AuthResult<T>> GetMetadataAsync<T>(string route, JsonTypeInfo<T> responseType, CancellationToken caller) =>
        WithDeadlineAsync(token => GetMetadataCoreAsync(route, responseType, token), caller);

    internal Task<AuthResult<T>> SendOAuthFormAsync<T>(string route, IReadOnlyList<KeyValuePair<string, string>> values,
        AuthenticationHeaderValue? authorization, JsonTypeInfo<T> responseType, CancellationToken caller) =>
        WithDeadlineAsync(token => SendOAuthFormCoreAsync(route, values, authorization, responseType, token), caller);

    internal Task<AuthOutcome> SendOAuthFormForSuccessAsync(string route, IReadOnlyList<KeyValuePair<string, string>> values,
        AuthenticationHeaderValue? authorization, CancellationToken caller) =>
        WithDeadlineAsync(token => SendOAuthFormForSuccessCoreAsync(route, values, authorization, token), caller);

    internal Task<AuthResult<T>> GetWithBearerAsync<T>(string route, string accessToken, JsonTypeInfo<T> responseType, CancellationToken caller) =>
        WithDeadlineAsync(token => GetWithBearerCoreAsync(route, accessToken, responseType, token), caller);

    internal Task<AuthResult<TResponse>> SendAuthenticatedJsonAsync<TRequest, TResponse>(HttpMethod method, string route, TRequest? body,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, AuthenticationHeaderValue authorization,
        HttpStatusCode expectedStatus, int maxRequestBytes, CancellationToken caller, string? stepUpProof = null) where TRequest : class =>
        WithDeadlineAsync(token => SendAuthenticatedJsonCoreAsync(method, route, body, requestType, responseType, authorization, expectedStatus, maxRequestBytes, token, stepUpProof), caller);

    internal Task<AuthOutcome> SendAuthenticatedForStatusAsync<TRequest>(HttpMethod method, string route, TRequest? body,
        JsonTypeInfo<TRequest> requestType, AuthenticationHeaderValue authorization, HttpStatusCode expectedStatus,
        int maxRequestBytes, CancellationToken caller, string? stepUpProof = null) where TRequest : class =>
        WithDeadlineAsync(token => SendAuthenticatedForStatusCoreAsync(method, route, body, requestType, authorization, expectedStatus, maxRequestBytes, token, stepUpProof), caller);

    internal Task<AuthCompletion> SendAuthenticatedForDeletionAsync(string route, AuthenticationHeaderValue authorization, CancellationToken caller) =>
        WithDeadlineAsync(token => SendAuthenticatedForDeletionCoreAsync(route, authorization, token), caller);

    internal Task<AuthCompletion> SendForAccountDeletionAsync(HttpMethod method, string route, AuthenticationHeaderValue? authorization, string receipt, CancellationToken caller, string? deletionAuthorization = null) =>
        WithDeadlineAsync(token => SendForAccountDeletionCoreAsync(method, route, authorization, receipt, token, deletionAuthorization), caller);

    internal Task<AuthResult<TerraScaleAuthAccountDeletion>> SendAccountDeletionWithProofAsync(AuthenticationHeaderValue authorization,
        string stepUpProof, string? deletionReceipt, CancellationToken caller) =>
        WithDeadlineAsync(token => SendAccountDeletionWithProofCoreAsync(authorization, stepUpProof, deletionReceipt, token), caller);



    private static Task<AuthOutcome> WithDeadlineAsync(Func<CancellationToken, Task<AuthOutcome>> operation, CancellationToken caller) =>
        WithDeadlineAsync<AuthOutcome>(async token => await operation(token).ConfigureAwait(false), caller);

    private static Task<AuthCompletion> WithDeadlineAsync(Func<CancellationToken, Task<AuthCompletion>> operation, CancellationToken caller) =>
        WithDeadlineAsync<AuthCompletion>(async token => await operation(token).ConfigureAwait(false), caller);

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
