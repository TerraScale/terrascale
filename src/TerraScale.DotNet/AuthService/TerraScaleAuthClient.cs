using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>
/// Entry point for ts-auth management API and OIDC operations against one
/// ts-auth instance. Built over a caller-owned <see cref="HttpClient"/>; the
/// underlying handler must disable automatic redirects. Safe reads are retried
/// per <see cref="TerraScaleAuthClientOptions"/>; ts-auth mutations carry no
/// idempotency contract and are always sent exactly once.
/// </summary>
public sealed class TerraScaleAuthClient
{
    private const int MaxOperationBytes = 1_048_576;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    // Immutable header values shared by every request: one allocation, not one per call.
    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private readonly HttpClient httpClient;
    private readonly TerraScaleAuthClientOptions options;
    private volatile AuthenticationHeaderValue? cachedApiKeyHeader;

    /// <summary>
    /// Creates a ts-auth client over a caller-owned <see cref="HttpClient"/>.
    /// The client assigns <c>BaseAddress</c> to the HttpClient when it is null
    /// and is never disposed. Requests remain bound to the validated configured
    /// origin even if the caller later changes <c>BaseAddress</c>.
    /// </summary>
    public TerraScaleAuthClient(HttpClient httpClient, TerraScaleAuthClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        var optionSnapshot = options with { };
        optionSnapshot.Validate();

        if (httpClient.BaseAddress is { } baseAddress)
        {
            // Uri equality omits user information and fragments. Validate the
            // caller's address as a bare auth origin before comparing it.
            TerraScaleAuthClientOptions.ValidateEndpoint(baseAddress, nameof(httpClient));
            if (baseAddress != optionSnapshot.BaseAddress)
            {
                throw new ArgumentException(
                    "HttpClient.BaseAddress conflicts with the configured ts-auth origin.",
                    nameof(options));
            }
        }

        this.httpClient = httpClient;
        this.options = optionSnapshot;
        this.httpClient.BaseAddress ??= optionSnapshot.BaseAddress;
        Users = new TerraScaleAuthUsersClient(this);
        Sessions = new TerraScaleAuthSessionsClient(this);
        ApiKeys = new TerraScaleAuthApiKeysClient(this);
        Organizations = new TerraScaleAuthOrganizationsClient(this);
        Connections = new TerraScaleAuthConnectionsClient(this);
        Directories = new TerraScaleAuthDirectoriesClient(this);
        JwtTemplates = new TerraScaleAuthJwtTemplatesClient(this);
        Webhooks = new TerraScaleAuthWebhooksClient(this);
        AuditEvents = new TerraScaleAuthAuditClient(this);
        Instance = new TerraScaleAuthInstanceClient(this);
        OAuthClients = new TerraScaleAuthOAuthClientsClient(this);
        Oidc = new TerraScaleAuthOidcClient(this);
        Platform = new TerraScaleAuthPlatformClient(this);
        Projects = new TerraScaleAuthProjectsClient(this);
    }

    /// <summary>User, session-adjacent, MFA factor, and passkey management.</summary>
    public TerraScaleAuthUsersClient Users { get; }

    /// <summary>Session revocation.</summary>
    public TerraScaleAuthSessionsClient Sessions { get; }

    /// <summary>Management API key creation, listing, and revocation.</summary>
    public TerraScaleAuthApiKeysClient ApiKeys { get; }

    /// <summary>Organizations, memberships, invitations, and enrollment domains.</summary>
    public TerraScaleAuthOrganizationsClient Organizations { get; }

    /// <summary>OIDC and SAML enterprise SSO connections.</summary>
    public TerraScaleAuthConnectionsClient Connections { get; }

    /// <summary>SCIM directory provisioning endpoints and token rotation.</summary>
    public TerraScaleAuthDirectoriesClient Directories { get; }

    /// <summary>Custom-claim JWT templates and token minting.</summary>
    public TerraScaleAuthJwtTemplatesClient JwtTemplates { get; }

    /// <summary>Outbound webhook endpoints, deliveries, and dead-letter replay.</summary>
    public TerraScaleAuthWebhooksClient Webhooks { get; }

    /// <summary>Instance audit event listing.</summary>
    public TerraScaleAuthAuditClient AuditEvents { get; }

    /// <summary>Instance record and hosted-UI branding settings.</summary>
    public TerraScaleAuthInstanceClient Instance { get; }

    /// <summary>OAuth client registration for this instance.</summary>
    public TerraScaleAuthOAuthClientsClient OAuthClients { get; }

    /// <summary>OIDC discovery, JWKS, token, revocation, and userinfo.</summary>
    public TerraScaleAuthOidcClient Oidc { get; }

    /// <summary>Platform organizations, projects, members and API keys, using a user sign-in token.</summary>
    public TerraScaleAuthPlatformClient Platform { get; }

    /// <summary>Project tenant settings, end users, sessions and OAuth clients under <c>/v1/{project}</c>, using a user sign-in token.</summary>
    public TerraScaleAuthProjectsClient Projects { get; }

    /// <summary>The validated instance slug every management route is scoped to.</summary>
    internal string InstanceSlug => options.InstanceSlug!;

    internal string ManagementRoute(string relative) => "/v1/" + TerraScaleAuthPaths.Escape(InstanceSlug) + relative;

    internal Task<TResponse> GetJsonAsync<TResponse>(
        string requestUri,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(
            HttpMethod.Get,
            requestUri,
            bodyFactory: null,
            responseTypeInfo,
            authentication: RequestAuthentication.Management,
            cancellationToken);

    /// <summary>One management POST that takes no request body and returns JSON.</summary>
    internal Task<TResponse> PostJsonAsync<TResponse>(
        string requestUri,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(
            HttpMethod.Post,
            requestUri,
            bodyFactory: null,
            responseTypeInfo,
            authentication: RequestAuthentication.Management,
            cancellationToken);

    /// <summary>One management request whose JSON body the caller already serialized
    /// (the tri-state user patch builds its own object).</summary>
    internal async Task<TResponse> SendPreparedJsonAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        byte[] payload,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        // The deadline spans the send and the body read, so it is owned here
        // and disposed once the response is fully consumed.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateRawJsonBodyFactory(payload),
            RequestAuthentication.Management,
            deadline.Token).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, deadline.Token).ConfigureAwait(false);
    }

    private static Func<HttpContent?> CreateRawJsonBodyFactory(byte[] payload)
    {
        if (payload.Length > MaxOperationBytes)
        {
            throw new ArgumentException($"The serialized ts-auth operation exceeds the {MaxOperationBytes} byte limit.", nameof(payload));
        }

        return () =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = JsonContentType;
            return content;
        };
    }

    internal Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string requestUri,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync(
            method,
            requestUri,
            CreateJsonBodyFactory(body, requestTypeInfo),
            responseTypeInfo,
            RequestAuthentication.Management,
            cancellationToken);
    }

    internal Task SendForNoContentAsync(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken) =>
        SendNoContentAsync(method, requestUri, bodyFactory: null, RequestAuthentication.Management, cancellationToken);

    internal Task SendJsonForNoContentAsync<TRequest>(
        HttpMethod method,
        string requestUri,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendNoContentAsync(
            method,
            requestUri,
            CreateJsonBodyFactory(body, requestTypeInfo),
            RequestAuthentication.Management,
            cancellationToken);
    }

    /// <summary>One unauthenticated GET for OIDC metadata documents.</summary>
    internal async Task<TResponse> GetMetadataAsync<TResponse>(
        string requestUri,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(
            HttpMethod.Get,
            requestUri,
            bodyFactory: null,
            RequestAuthentication.None,
            deadline.Token).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>One OAuth token form POST with client
    /// authentication and the flat RFC 6749 error shape.</summary>
    internal async Task<TResponse> SendOAuthFormAsync<TResponse>(
        string requestUri,
        IReadOnlyList<KeyValuePair<string, string>> formValues,
        AuthenticationHeaderValue? authorization,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(
            HttpMethod.Post,
            requestUri,
            CreateFormBodyFactory(formValues),
            authorization is null ? RequestAuthentication.None : new RequestAuthentication(authorization),
            deadline.Token).ConfigureAwait(false);
        await RequireStatusAsync(response, HttpStatusCode.OK, deadline.Token).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>One OAuth form POST whose success body is empty (revocation,
    /// RFC 7009: unknown tokens also succeed).</summary>
    internal async Task SendOAuthFormForSuccessAsync(
        string requestUri,
        IReadOnlyList<KeyValuePair<string, string>> formValues,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(
            HttpMethod.Post,
            requestUri,
            CreateFormBodyFactory(formValues),
            authorization is null ? RequestAuthentication.None : new RequestAuthentication(authorization),
            deadline.Token).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
        {
            throw await TerraScaleAuthErrors.CreateAsync(response, deadline.Token).ConfigureAwait(false);
        }
    }

    /// <summary>One bearer-authenticated GET with a caller-provided access token (userinfo).</summary>
    internal async Task<TResponse> GetWithBearerAsync<TResponse>(
        string requestUri,
        string accessToken,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(
            HttpMethod.Get,
            requestUri,
            bodyFactory: null,
            new RequestAuthentication(TerraScaleAuthCredentials.Bearer(accessToken)),
            deadline.Token).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, deadline.Token).ConfigureAwait(false);
    }

    internal async Task<TResponse> SendAuthenticatedJsonAsync<TRequest, TResponse>(
        HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType, AuthenticationHeaderValue authorization,
        HttpStatusCode expectedStatus, int maxRequestBytes, CancellationToken cancellationToken)
        where TRequest : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(method, route,
            body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
            new RequestAuthentication(authorization), deadline.Token).ConfigureAwait(false);
        await RequireStatusAsync(response, expectedStatus, deadline.Token).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseType, deadline.Token).ConfigureAwait(false);
    }

    internal async Task SendAuthenticatedForStatusAsync<TRequest>(
        HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
        AuthenticationHeaderValue authorization, HttpStatusCode expectedStatus,
        int maxRequestBytes, CancellationToken cancellationToken)
        where TRequest : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(method, route,
            body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
            new RequestAuthentication(authorization), deadline.Token).ConfigureAwait(false);
        await RequireStatusAsync(response, expectedStatus, deadline.Token).ConfigureAwait(false);
    }

    // A deletion that ts-auth may finish in the background: true for 204 (done), false for 202 (pending; repeating the
    // request resumes it). Any other success status is not evidence either way.
    internal async Task<bool> SendAuthenticatedForDeletionAsync(string route, AuthenticationHeaderValue authorization, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(HttpMethod.Delete, route, null, new RequestAuthentication(authorization), deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return false;
        }

        await RequireStatusAsync(response, HttpStatusCode.NoContent, deadline.Token).ConfigureAwait(false);
        return true;
    }

    internal async Task<bool> SendForAccountDeletionAsync(HttpMethod method, string route,
        AuthenticationHeaderValue? authorization, string deletionReceipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deletionReceipt);
        if (deletionReceipt.Length != 43 || deletionReceipt.Any(character =>
                character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) ||
            !"AEIMQUYcgkosw048".Contains(deletionReceipt[^1]))
        {
            throw new ArgumentException("A deletion receipt must be the canonical unpadded base64url encoding of exactly 32 bytes.", nameof(deletionReceipt));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(method, route, null,
            authorization is null ? RequestAuthentication.None : new RequestAuthentication(authorization),
            deadline.Token, deletionReceipt).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return false;
        }

        await RequireStatusAsync(response, HttpStatusCode.NoContent, deadline.Token).ConfigureAwait(false);
        return true;
    }

    private static async Task RequireStatusAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
        {
            throw await TerraScaleAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode != expected)
        {
            throw new TerraScaleAuthException(response.StatusCode, "ts-auth returned an unexpected success status.", "invalid_response", TerraScaleAuthErrors.ResolveRetryAfter(response), response);
        }
    }

    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        Func<HttpContent?>? bodyFactory,
        JsonTypeInfo<TResponse> responseTypeInfo,
        RequestAuthentication authentication,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            bodyFactory,
            authentication,
            deadline.Token).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, deadline.Token).ConfigureAwait(false);
    }

    private async Task SendNoContentAsync(
        HttpMethod method,
        string requestUri,
        Func<HttpContent?>? bodyFactory,
        RequestAuthentication authentication,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            bodyFactory,
            authentication,
            deadline.Token).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
        {
            throw await TerraScaleAuthErrors.CreateAsync(response, deadline.Token).ConfigureAwait(false);
        }

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new TerraScaleAuthException(
                response.StatusCode,
                $"ts-auth returned HTTP {(int)response.StatusCode}, but HTTP {(int)HttpStatusCode.NoContent} was required.",
                errorCode: null,
                retryAfter: TerraScaleAuthErrors.ResolveRetryAfter(response), response: response);
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

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpMethod method,
        string requestUri,
        Func<HttpContent?>? bodyFactory,
        RequestAuthentication authentication,
        CancellationToken cancellationToken,
        string? deletionReceipt = null)
    {
        var token = cancellationToken;
        var attempt = 0;
        while (true)
        {
            // Every attempt rebuilds the request: content cannot be reused across sends.
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method, new Uri(options.BaseAddress!, requestUri));
            if (authentication.Kind == RequestAuthenticationKind.None && httpClient.DefaultRequestHeaders.Authorization is not null)
            {
                throw new InvalidOperationException("Unauthenticated ts-auth requests require an HttpClient without a default Authorization header.");
            }
            ApplyAuthentication(request, authentication);
            if (deletionReceipt is not null)
            {
                request.Headers.Add("X-TerraScale-Deletion-Receipt", deletionReceipt);
            }
            request.Headers.Accept.Add(AcceptJson);
            if (bodyFactory?.Invoke() is { } content)
            {
                request.Content = content;
            }

            try
            {
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (deletionReceipt is not null || authentication.Kind == RequestAuthenticationKind.Explicit || !ShouldRetry(response, method, attempt, options.MaxRetryAttempts))
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                // Surfacing at exactly the cap too: a delay equal to the
                // request deadline could never complete before it fires.
                if (delay >= MaxRetryAfter)
                {
                    return response;
                }

                response.Dispose();
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (deletionReceipt is null && authentication.Kind != RequestAuthenticationKind.Explicit && IsSafeRead(method) && attempt < options.MaxRetryAttempts)
            {
                await Task.Delay(GetRetryDelay(response: null, attempt), token).ConfigureAwait(false);
            }

            attempt++;
        }
    }

    private static bool ShouldRetry(HttpResponseMessage response, HttpMethod method, int attempt, int maxAttempts) =>
        IsSafeRead(method) &&
        attempt < maxAttempts &&
        response.StatusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static bool IsSafeRead(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head;

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (TerraScaleAuthErrors.ResolveRetryAfter(response) is { } retryAfter)
        {
            return retryAfter <= TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
        }

        if (options.RetryDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var milliseconds = Math.Min(
            options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt),
            TimeSpan.FromSeconds(30).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private void ApplyAuthentication(HttpRequestMessage request, RequestAuthentication authentication)
    {
        switch (authentication.Kind)
        {
            case RequestAuthenticationKind.None:
                return;
            case RequestAuthenticationKind.Management:
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    throw new InvalidOperationException("Configure TerraScaleAuthClientOptions.ApiKey (the instance sk_ management key) for ts-auth management API calls.");
                }

                request.Headers.Authorization ??= cachedApiKeyHeader ??= new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
                return;
            case RequestAuthenticationKind.Explicit:
                request.Headers.Authorization = authentication.Header;
                return;
            default:
                ThrowUnknownAuthenticationKind();
                return;
        }
    }

    private static void ThrowUnknownAuthenticationKind() => throw new InvalidOperationException("Unknown ts-auth request authentication kind.");

    private static async Task<TResponse> ReadJsonAsync<TResponse>(
        HttpResponseMessage response,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
        {
            throw await TerraScaleAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var media = response.Content.Headers.ContentType;
        if (!string.Equals(media?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || media?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new TerraScaleAuthException(response.StatusCode, "ts-auth returned an unexpected response media type.", "invalid_response", TerraScaleAuthErrors.ResolveRetryAfter(response), response);
        }

        // Bound successful bodies before deserialization, including unknown-length responses.
        using var destination = new BoundedHttpBodyStream(MaxOperationBytes,
            () => new TerraScaleAuthException(response.StatusCode, "ts-auth response exceeds the byte limit.", "invalid_response", TerraScaleAuthErrors.ResolveRetryAfter(response), response), cancellationToken);
        if (response.Content.Headers.ContentLength > MaxOperationBytes)
        {
            throw new TerraScaleAuthException(response.StatusCode, "ts-auth response exceeds the byte limit.", "invalid_response", TerraScaleAuthErrors.ResolveRetryAfter(response), response);
        }
        try
        {
            await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TerraScaleAuthException(response.StatusCode, "ts-auth response body could not be read.", "invalid_response", TerraScaleAuthErrors.ResolveRetryAfter(response), response);
        }
        cancellationToken.ThrowIfCancellationRequested();
        TResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize(destination.Written.Span, responseTypeInfo);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Raw JsonException internals (paths, fragments of the body) never
            // escape to SDK consumers.
            throw new TerraScaleAuthException(
                response.StatusCode,
                "ts-auth returned an invalid response body.",
                errorCode: "invalid_response",
                retryAfter: TerraScaleAuthErrors.ResolveRetryAfter(response), response: response);
        }

        return payload ?? throw new TerraScaleAuthException(
            response.StatusCode,
            "ts-auth returned an empty response body.",
            errorCode: null,
            retryAfter: TerraScaleAuthErrors.ResolveRetryAfter(response), response: response);
    }

    private readonly record struct RequestAuthentication(RequestAuthenticationKind Kind, AuthenticationHeaderValue? Header = null)
    {
        public static readonly RequestAuthentication None = new(RequestAuthenticationKind.None);
        public static readonly RequestAuthentication Management = new(RequestAuthenticationKind.Management);

        public RequestAuthentication(AuthenticationHeaderValue header) : this(RequestAuthenticationKind.Explicit, header)
        {
        }
    }

    private enum RequestAuthenticationKind
    {
        None,
        Management,
        Explicit
    }
}
