using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthClient
    {
        private const int MaxOperationBytes = 1_048_576;
        private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

        // Immutable header values shared by every request: one allocation, not one per call.
        private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");

        private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

        private readonly HttpClient httpClient;
        private static readonly Uri Origin = new("https://auth.terrascale.tech/");

        public TsAuthClient(HttpClient httpClient)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            this.httpClient = httpClient;
            Oidc = new TsAuthOidcClient(this);
            Platform = new TsAuthPlatformClient(this);
            Projects = new TsAuthProjectsClient(this);
        }

        public TsAuthOidcClient Oidc { get; }

        public TsAuthPlatformClient Platform { get; }

        public TsAuthProjectsClient Projects { get; }

        internal string ManagementRoute(string relative)
        {
            return "/v1/terrascale" + relative;
        }

        private async Task<TResponse> GetMetadataCoreAsync<TResponse>(
            string requestUri,
            JsonTypeInfo<TResponse> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(
                HttpMethod.Get,
                requestUri,
                bodyFactory: null,
                null,
                cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
        }

        private async Task<TResponse> SendOAuthFormCoreAsync<TResponse>(
            string requestUri,
            IReadOnlyList<KeyValuePair<string, string>> formValues,
            AuthenticationHeaderValue? authorization,
            JsonTypeInfo<TResponse> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(
                HttpMethod.Post,
                requestUri,
                CreateFormBodyFactory(formValues),
                authorization,
                cancellationToken).ConfigureAwait(false);
            await RequireStatusAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendOAuthFormForSuccessCoreAsync(
            string requestUri,
            IReadOnlyList<KeyValuePair<string, string>> formValues,
            AuthenticationHeaderValue? authorization,
            CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(
                HttpMethod.Post,
                requestUri,
                CreateFormBodyFactory(formValues),
                authorization,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
            {
                throw await TsAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<TResponse> GetWithBearerCoreAsync<TResponse>(
            string requestUri,
            string accessToken,
            JsonTypeInfo<TResponse> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
            using var response = await SendRequestAsync(
                HttpMethod.Get,
                requestUri,
                bodyFactory: null,
                TsAuthCredentials.Bearer(accessToken),
                cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
        }

        private async Task<TResponse> SendAuthenticatedJsonCoreAsync<TRequest, TResponse>(
            HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
            JsonTypeInfo<TResponse> responseType, AuthenticationHeaderValue authorization,
            HttpStatusCode expectedStatus, int maxRequestBytes, CancellationToken cancellationToken, string? stepUpProof = null)
            where TRequest : class
        {
            using var response = await SendRequestAsync(method, route,
                body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
                authorization, cancellationToken, stepUpProof: stepUpProof).ConfigureAwait(false);
            await RequireStatusAsync(response, expectedStatus, cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, responseType, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendAuthenticatedForStatusCoreAsync<TRequest>(
            HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
            AuthenticationHeaderValue authorization, HttpStatusCode expectedStatus,
            int maxRequestBytes, CancellationToken cancellationToken)
            where TRequest : class
        {
            using var response = await SendRequestAsync(method, route,
                body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
                authorization, cancellationToken).ConfigureAwait(false);
            await RequireStatusAsync(response, expectedStatus, cancellationToken).ConfigureAwait(false);
        }

        // A deletion that ts-auth may finish in the background: true for 204 (done), false for 202 (pending; repeating the
        // request resumes it). Any other success status is not evidence either way.
        private async Task<bool> SendAuthenticatedForDeletionCoreAsync(string route, AuthenticationHeaderValue authorization, CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(HttpMethod.Delete, route, null, authorization, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return false;
            }

            await RequireStatusAsync(response, HttpStatusCode.NoContent, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> SendForAccountDeletionCoreAsync(HttpMethod method, string route,
            AuthenticationHeaderValue? authorization, string deletionReceipt, CancellationToken cancellationToken, string? deletionAuthorization)
        {
            ValidateDeletionReceipt(deletionReceipt);

            using var response = await SendRequestAsync(method, route, null,
                authorization,
                cancellationToken, deletionReceipt, deletionAuthorization: deletionAuthorization).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return false;
            }

            await RequireStatusAsync(response, HttpStatusCode.NoContent, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private static void ValidateDeletionReceipt(string deletionReceipt)
        {
            ArgumentNullException.ThrowIfNull(deletionReceipt);
            if (deletionReceipt.Length != 43 || deletionReceipt.Any(character =>
                    character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) ||
                !"AEIMQUYcgkosw048".Contains(deletionReceipt[^1]))
            {
                throw new ArgumentException("A deletion receipt must be the canonical unpadded base64url encoding of exactly 32 bytes.", nameof(deletionReceipt));
            }
        }

        private static async Task RequireStatusAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken cancellationToken)
        {
            if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
            {
                throw await TsAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (response.StatusCode != expected)
            {
                throw new TsAuthException(response.StatusCode, "ts-auth returned an unexpected success status.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response);
            }
        }

        private static Func<HttpContent?> CreateJsonBodyFactory<TRequest>(TRequest body, JsonTypeInfo<TRequest> typeInfo, int maximumBytes = MaxOperationBytes)
        {
            ArgumentNullException.ThrowIfNull(typeInfo);
            var payload = JsonSerializer.SerializeToUtf8Bytes(body, typeInfo);
            if (payload.Length > maximumBytes)
            {
                throw new ArgumentException($"The serialized ts-auth operation exceeds the {maximumBytes} byte limit.", nameof(body));
            }

            return () =>
            {
                var content = new ByteArrayContent(payload);
                content.Headers.ContentType = JsonContentType;
                return content;
            };
        }

        private static Func<HttpContent?> CreateFormBodyFactory(IReadOnlyList<KeyValuePair<string, string>> values)
        {
            using var content = new FormUrlEncodedContent(values);
            // FormUrlEncodedContent buffers synchronously; its computed length is exact.
            if (content.Headers.ContentLength > MaxOperationBytes)
            {
                throw new ArgumentException("The ts-auth form exceeds the request byte limit.", nameof(values));
            }

            return () => new FormUrlEncodedContent(values);
        }

        private async Task<HttpResponseMessage> SendRequestAsync(
            HttpMethod method,
            string requestUri,
            Func<HttpContent?>? bodyFactory,
            AuthenticationHeaderValue? authorization,
            CancellationToken cancellationToken,
            string? deletionReceipt = null,
            string? stepUpProof = null,
            string? deletionAuthorization = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method, new Uri(Origin, requestUri));
            if (request.RequestUri!.Scheme == Uri.UriSchemeHttps)
            {
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }
            if (authorization is null && httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                throw new InvalidOperationException("Unauthenticated ts-auth requests require an HttpClient without a default Authorization header.");
            }
            request.Headers.Authorization = authorization;
            if (deletionReceipt is not null)
            {
                request.Headers.Add("X-TerraScale-Deletion-Receipt", deletionReceipt);
            }
            if (stepUpProof is not null)
                request.Headers.Add("X-TerraScale-Step-Up-Proof", stepUpProof);
            if (deletionAuthorization is not null)
                request.Headers.Add("X-TerraScale-Deletion-Authorization", deletionAuthorization);
            request.Headers.Accept.Add(AcceptJson);
            if (bodyFactory?.Invoke() is { } content)
            {
                request.Content = content;
            }
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<TResponse> ReadJsonAsync<TResponse>(
            HttpResponseMessage response,
            JsonTypeInfo<TResponse> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
            {
                throw await TsAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
            }

            var media = response.Content.Headers.ContentType;
            if (!string.Equals(media?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || media?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                throw new TsAuthException(response.StatusCode, "ts-auth returned an unexpected response media type.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response);
            }

            // Bound successful bodies before deserialization, including unknown-length responses.
            using var destination = new BoundedHttpBodyStream(MaxOperationBytes,
                () => new TsAuthException(response.StatusCode, "ts-auth response exceeds the byte limit.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response), cancellationToken);
            if (response.Content.Headers.ContentLength > MaxOperationBytes)
            {
                throw new TsAuthException(response.StatusCode, "ts-auth response exceeds the byte limit.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response);
            }
            try
            {
                await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TsAuthException(response.StatusCode, "ts-auth response body could not be read.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response);
            }
            cancellationToken.ThrowIfCancellationRequested();
            TResponse? payload;
            try
            {
                payload = JsonSerializer.Deserialize(destination.Written.Span, responseTypeInfo);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Do not retain response fragments or paths in diagnostics.
                throw new TsAuthException(
                    response.StatusCode,
                    "ts-auth returned an invalid response body.",
                    errorCode: "invalid_response",
                    retryAfter: TsAuthErrors.ResolveRetryAfter(response), response: response);
            }

            return payload ?? throw new TsAuthException(
                response.StatusCode,
                "ts-auth returned an empty response body.",
                errorCode: null,
                retryAfter: TsAuthErrors.ResolveRetryAfter(response), response: response);
        }

    }
}
