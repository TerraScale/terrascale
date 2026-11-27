using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthClient
    {
        private const int MaxOperationBytes = 1_048_576;
        private const int MaxResponseBytes = 16 * 1024 * 1024;
        internal const int MaxKeySetBytes = 64 * 1024;
        private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

        private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");

        private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

        private readonly HttpClient httpClient;

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

        internal static string ManagementRoute(string relative)
        {
            return "/v1/terrascale" + relative;
        }

        private async Task<TResponse> GetMetadataCoreAsync<TResponse>(
            string requestUri,
            JsonTypeInfo<TResponse> responseTypeInfo,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(
                HttpMethod.Get,
                requestUri,
                bodyFactory: null,
                null,
                cancellationToken);
            return await ReadJsonAsync(response, responseTypeInfo, maxResponseBytes, cancellationToken);
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
                cancellationToken);
            await RequireStatusAsync(response, HttpStatusCode.OK, cancellationToken);
            return await ReadJsonAsync(response, responseTypeInfo, MaxResponseBytes, cancellationToken);
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
                cancellationToken);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
            {
                throw await TsAuthErrors.CreateAsync(response, cancellationToken);
            }
        }

        private async Task<OAuthTokenOutcome> TrySendOAuthTokenCoreAsync(
            string requestUri,
            IReadOnlyList<KeyValuePair<string, string>> formValues,
            AuthenticationHeaderValue? authorization,
            JsonTypeInfo<TsAuthTokenResponse> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(
                HttpMethod.Post,
                requestUri,
                CreateFormBodyFactory(formValues),
                authorization,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var read = await TryReadJsonAsync(response, responseTypeInfo, MaxResponseBytes, cancellationToken);
                return read.HasValue && read.Value is { } tokens
                    ? tokens
                    : new OAuthTokenUnavailable(read.ErrorType, read.StatusCode, read.ErrorCode, read.Message);
            }

            if ((int)response.StatusCode is >= 200 and <= 299)
            {
                return new OAuthTokenUnavailable("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth returned an unexpected success status.");
            }

            var retryAfter = TsAuthErrors.ResolveRetryAfter(response);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new OAuthTokenNotStarted("TsAuthHttpError", response.StatusCode, "rate_limited", "ts-auth returned HTTP 429.", retryAfter);
            }

            var errorCode = await TsAuthErrors.ReadErrorCodeAsync(response, cancellationToken);
            if (errorCode == "invalid_grant")
            {
                return new OAuthTokenInvalidGrant();
            }

            if (errorCode == "invalid_request")
            {
                return new OAuthTokenInvalidRequest(response.StatusCode, $"ts-auth returned HTTP {(int)response.StatusCode}.");
            }

            if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
            {
                return new OAuthTokenNotStarted("TsAuthHttpError", response.StatusCode, errorCode ?? "none", $"ts-auth returned HTTP {(int)response.StatusCode}.", retryAfter);
            }

            return new OAuthTokenUnavailable("TsAuthHttpError", response.StatusCode, errorCode ?? "none", $"ts-auth returned HTTP {(int)response.StatusCode}.");
        }

        private async Task<OidcDiscoveryOutcome> TryGetDiscoveryCoreAsync(
            string requestUri,
            JsonTypeInfo<TsAuthOidcDiscovery> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(
                HttpMethod.Get,
                requestUri,
                bodyFactory: null,
                null,
                cancellationToken);
            if ((int)response.StatusCode is < 200 or > 299)
            {
                var errorCode = await TsAuthErrors.ReadErrorCodeAsync(response, cancellationToken);
                return new OidcDiscoveryUnavailable("TsAuthHttpError", response.StatusCode, errorCode ?? "none", $"ts-auth returned HTTP {(int)response.StatusCode}.");
            }

            var read = await TryReadJsonAsync(response, responseTypeInfo, MaxResponseBytes, cancellationToken);
            return read.HasValue && read.Value is { } discovery
                ? discovery
                : new OidcDiscoveryUnavailable(read.ErrorType, read.StatusCode, read.ErrorCode, read.Message);
        }

        private async Task<OidcJwksOutcome> TryGetKeySetCoreAsync(
            string requestUri,
            JsonTypeInfo<JsonElement> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(
                HttpMethod.Get,
                requestUri,
                bodyFactory: null,
                null,
                cancellationToken);
            if ((int)response.StatusCode is < 200 or > 299)
            {
                var errorCode = await TsAuthErrors.ReadErrorCodeAsync(response, cancellationToken);
                return new OidcJwksUnavailable("TsAuthHttpError", response.StatusCode, errorCode ?? "none", $"ts-auth returned HTTP {(int)response.StatusCode}.");
            }

            var read = await TryReadJsonAsync(response, responseTypeInfo, MaxKeySetBytes, cancellationToken);
            return read.HasValue
                ? read.Value
                : new OidcJwksUnavailable(read.ErrorType, read.StatusCode, read.ErrorCode, read.Message);
        }

        private async Task<UserInfoOutcome> TryGetUserInfoCoreAsync(
            string requestUri,
            string accessToken,
            JsonTypeInfo<TsAuthUserInfo> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
            using var response = await SendRequestAsync(
                HttpMethod.Get,
                requestUri,
                bodyFactory: null,
                TsAuthCredentials.Bearer(accessToken),
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new UserInfoUnauthorized();
            }

            if ((int)response.StatusCode is < 200 or > 299)
            {
                var errorCode = await TsAuthErrors.ReadErrorCodeAsync(response, cancellationToken);
                return new UserInfoUnavailable("TsAuthHttpError", response.StatusCode, errorCode ?? "none", $"ts-auth returned HTTP {(int)response.StatusCode}.");
            }

            var read = await TryReadJsonAsync(response, responseTypeInfo, MaxResponseBytes, cancellationToken);
            return read.HasValue && read.Value is { } user
                ? user
                : new UserInfoUnavailable(read.ErrorType, read.StatusCode, read.ErrorCode, read.Message);
        }

        private async Task<TokenRevocationOutcome> TrySendOAuthRevokeCoreAsync(
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
                cancellationToken);
            if ((int)response.StatusCode is >= 200 and <= 299)
            {
                return new TokenRevoked();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new TokenRevocationRateLimited(TsAuthErrors.ResolveRetryAfter(response), "none");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new TokenAlreadyRevoked();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new TokenRevocationClientRejected(response.StatusCode, "unauthorized");
            }

            var errorCode = await TsAuthErrors.ReadErrorCodeAsync(response, cancellationToken);
            if (errorCode is "invalid_grant" or "invalid_token")
            {
                return new TokenAlreadyRevoked();
            }

            if (errorCode == "invalid_client")
            {
                return new TokenRevocationClientRejected(response.StatusCode, errorCode);
            }

            return new TokenRevocationUnavailable("TsAuthHttpError", response.StatusCode, errorCode ?? "none");
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
                cancellationToken);
            return await ReadJsonAsync(response, responseTypeInfo, MaxResponseBytes, cancellationToken);
        }

        private async Task<TResponse> SendAuthenticatedJsonCoreAsync<TRequest, TResponse>(
            HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
            JsonTypeInfo<TResponse> responseType, AuthenticationHeaderValue authorization,
            HttpStatusCode expectedStatus, int maxRequestBytes, CancellationToken cancellationToken, string? stepUpProof = null)
            where TRequest : class
        {
            using var response = await SendRequestAsync(method, route,
                body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
                authorization, cancellationToken, stepUpProof: stepUpProof);
            await RequireStatusAsync(response, expectedStatus, cancellationToken);
            return await ReadJsonAsync(response, responseType, MaxResponseBytes, cancellationToken);
        }

        private async Task SendAuthenticatedForStatusCoreAsync<TRequest>(
            HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
            AuthenticationHeaderValue authorization, HttpStatusCode expectedStatus,
            int maxRequestBytes, CancellationToken cancellationToken, string? stepUpProof = null)
            where TRequest : class
        {
            using var response = await SendRequestAsync(method, route,
                body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
                authorization, cancellationToken, stepUpProof: stepUpProof);
            await RequireStatusAsync(response, expectedStatus, cancellationToken);
        }

        private async Task<bool> SendAuthenticatedForDeletionCoreAsync(string route, AuthenticationHeaderValue authorization, CancellationToken cancellationToken)
        {
            using var response = await SendRequestAsync(HttpMethod.Delete, route, null, authorization, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return false;
            }

            await RequireStatusAsync(response, HttpStatusCode.NoContent, cancellationToken);
            return true;
        }

        private async Task<bool> SendForAccountDeletionCoreAsync(HttpMethod method, string route,
            AuthenticationHeaderValue? authorization, string deletionReceipt, string? deletionAuthorization, CancellationToken cancellationToken)
        {
            ValidateDeletionReceipt(deletionReceipt);

            using var response = await SendRequestAsync(method, route, null,
                authorization,
                cancellationToken, deletionReceipt, deletionAuthorization: deletionAuthorization);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return false;
            }

            await RequireStatusAsync(response, HttpStatusCode.NoContent, cancellationToken);
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
                throw await TsAuthErrors.CreateAsync(response, cancellationToken);
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
            var uri = new Uri(TsAuthOrigin.Value, requestUri);
            using var request = new HttpRequestMessage(method, uri);
            if (uri.Scheme == Uri.UriSchemeHttps)
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
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private static async Task<TResponse> ReadJsonAsync<TResponse>(
            HttpResponseMessage response,
            JsonTypeInfo<TResponse> responseTypeInfo,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
            {
                throw await TsAuthErrors.CreateAsync(response, cancellationToken);
            }

            var media = response.Content.Headers.ContentType;
            if (!string.Equals(media?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || media?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                throw new TsAuthException(response.StatusCode, "ts-auth returned an unexpected response media type.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response);
            }

            using var destination = new BoundedHttpBodyStream(maxResponseBytes,
                () => new TsAuthException(response.StatusCode, "ts-auth response exceeds the byte limit.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response), cancellationToken);
            if (response.Content.Headers.ContentLength > maxResponseBytes)
            {
                throw new TsAuthException(response.StatusCode, "ts-auth response exceeds the byte limit.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response);
            }
            try
            {
                await response.Content.CopyToAsync(destination, cancellationToken);
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TsAuthException(response.StatusCode, "ts-auth response body could not be read.", "invalid_response", TsAuthErrors.ResolveRetryAfter(response), response);
            }
            catch (HttpRequestException)
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
            catch (JsonException)
            {
                throw new TsAuthException(
                    response.StatusCode,
                    "ts-auth returned an invalid response body.",
                    errorCode: "invalid_response",
                    retryAfter: TsAuthErrors.ResolveRetryAfter(response), response: response);
            }
            catch (InvalidOperationException)
            {
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

        private static async Task<JsonReadResult<TResponse>> TryReadJsonAsync<TResponse>(
            HttpResponseMessage response,
            JsonTypeInfo<TResponse> responseTypeInfo,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            var media = response.Content.Headers.ContentType;
            if (!string.Equals(media?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || media?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                return JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth returned an unexpected response media type.");
            }

            if (response.Content.Headers.ContentLength > maxResponseBytes)
            {
                return JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth response exceeds the byte limit.");
            }

            using var destination = new BoundedHttpBodyStream(
                maxResponseBytes,
                static () => new InvalidDataException("ts-auth response exceeds the byte limit."),
                cancellationToken);
            try
            {
                await response.Content.CopyToAsync(destination, cancellationToken);
            }
            catch (InvalidDataException)
            {
                return JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth response exceeds the byte limit.");
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth response body could not be read.");
            }
            catch (HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth response body could not be read.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            TResponse? payload;
            try
            {
                payload = JsonSerializer.Deserialize(destination.Written.Span, responseTypeInfo);
            }
            catch (JsonException)
            {
                return JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth returned an invalid response body.");
            }
            catch (InvalidOperationException)
            {
                return JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "invalid_response", "ts-auth returned an invalid response body.");
            }

            return payload is null
                ? JsonReadResult<TResponse>.Failed("TsAuthInvalidResponse", response.StatusCode, "none", "ts-auth returned an empty response body.")
                : JsonReadResult<TResponse>.Succeeded(payload, response.StatusCode);
        }

        private readonly record struct JsonReadResult<TResponse>(
            TResponse? Value,
            bool HasValue,
            string ErrorType,
            HttpStatusCode? StatusCode,
            string ErrorCode,
            string Message)
        {
            public static JsonReadResult<TResponse> Succeeded(TResponse value, HttpStatusCode statusCode) =>
                new(value, true, "", statusCode, "none", "");

            public static JsonReadResult<TResponse> Failed(string errorType, HttpStatusCode statusCode, string errorCode, string message) =>
                new(default, false, errorType, statusCode, errorCode, message);
        }

    }
}
