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
    private readonly TerraScaleAuthEndpointDiscovery? endpointDiscovery;
    private volatile AuthenticationHeaderValue? cachedApiKeyHeader;

    /// <summary>
    /// Creates a ts-auth client over a caller-owned <see cref="HttpClient"/>.
    /// The client assigns <c>BaseAddress</c> to the HttpClient when it is null
    /// and is never disposed; later mutations of the HttpClient (including
    /// <c>BaseAddress</c>) are honored by subsequent requests.
    /// </summary>
    public TerraScaleAuthClient(HttpClient httpClient, TerraScaleAuthClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        var optionSnapshot = options with { };
        optionSnapshot.Validate();

        if (httpClient.BaseAddress is not null && httpClient.BaseAddress != optionSnapshot.BaseAddress)
        {
            throw new ArgumentException(
                $"HttpClient.BaseAddress '{httpClient.BaseAddress}' conflicts with TerraScaleAuthClientOptions.BaseAddress '{optionSnapshot.BaseAddress}'.",
                nameof(options));
        }

        this.httpClient = httpClient;
        this.options = optionSnapshot;
        this.httpClient.BaseAddress ??= optionSnapshot.BaseAddress;
        if (!optionSnapshot.UseReverseProxy &&
            optionSnapshot.BaseAddress!.Host.Equals("auth.terrascale.tech", StringComparison.OrdinalIgnoreCase))
        {
            endpointDiscovery = new TerraScaleAuthEndpointDiscovery(httpClient, optionSnapshot.DiscoveryBaseAddress);
        }

        Users = new TerraScaleAuthUsersClient(this);
        Sessions = new TerraScaleAuthSessionsClient(this);
        ApiKeys = new TerraScaleAuthApiKeysClient(this);
        Organizations = new TerraScaleAuthOrganizationsClient(this);
        Connections = new TerraScaleAuthConnectionsClient(this);
        Directories = new TerraScaleAuthDirectoriesClient(this);
        JwtTemplates = new TerraScaleAuthJwtTemplatesClient(this);
        Webhooks = new TerraScaleAuthWebhooksClient(this);
        AuditEvents = new TerraScaleAuthAuditClient(this);
        Billing = new TerraScaleAuthBillingClient(this);
        Instance = new TerraScaleAuthInstanceClient(this);
        OAuthClients = new TerraScaleAuthOAuthClientsClient(this);
        Oidc = new TerraScaleAuthOidcClient(this);
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

    /// <summary>Plan, quota, and Stripe checkout/portal operations.</summary>
    public TerraScaleAuthBillingClient Billing { get; }

    /// <summary>Instance record and hosted-UI branding settings.</summary>
    public TerraScaleAuthInstanceClient Instance { get; }

    /// <summary>OAuth client registration for this instance.</summary>
    public TerraScaleAuthOAuthClientsClient OAuthClients { get; }

    /// <summary>OIDC discovery, JWKS, token, introspection, revocation, and userinfo.</summary>
    public TerraScaleAuthOidcClient Oidc { get; }

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

    /// <summary>One OAuth form POST (token, introspect, revoke) with client
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
            () => new FormUrlEncodedContent(formValues),
            authorization is null ? RequestAuthentication.None : new RequestAuthentication(authorization),
            deadline.Token).ConfigureAwait(false);
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
            () => new FormUrlEncodedContent(formValues),
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
            new RequestAuthentication(new AuthenticationHeaderValue("Bearer", accessToken.Trim())),
            deadline.Token).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, deadline.Token).ConfigureAwait(false);
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
            throw new TerraScaleApiException(
                response.StatusCode,
                $"ts-auth returned HTTP {(int)response.StatusCode}, but HTTP {(int)HttpStatusCode.NoContent} was required.",
                errorCode: null,
                requestId: null,
                responseBody: null,
                retryAfter: TerraScaleAuthErrors.ResolveRetryAfter(response));
        }
    }

    private static Func<HttpContent?> CreateJsonBodyFactory<TRequest>(TRequest body, JsonTypeInfo<TRequest> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, typeInfo);
        if (payload.Length > MaxOperationBytes)
        {
            throw new ArgumentException($"The serialized ts-auth operation exceeds the {MaxOperationBytes} byte limit.", nameof(body));
        }

        return () =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = JsonContentType;
            return content;
        };
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpMethod method,
        string requestUri,
        Func<HttpContent?>? bodyFactory,
        RequestAuthentication authentication,
        CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        var attempt = 0;
        var regionIndex = 0;
        var endpoints = endpointDiscovery is null
            ? null
            : await endpointDiscovery.GetEndpointsAsync(token).ConfigureAwait(false);

        while (true)
        {
            // Every attempt rebuilds the request: content cannot be reused across sends.
            var target = endpoints is null ? requestUri : new Uri(endpoints[regionIndex], requestUri).AbsoluteUri;
            using var request = new HttpRequestMessage(method, target);
            ApplyAuthentication(request, authentication);
            request.Headers.Accept.Add(AcceptJson);
            if (bodyFactory?.Invoke() is { } content)
            {
                request.Content = content;
            }

            try
            {
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (IsSafeRead(method) && endpoints is not null && regionIndex < endpoints.Count - 1 &&
                    response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    regionIndex++;
                    response.Dispose();
                    continue;
                }

                if (!ShouldRetry(response, method, attempt, options.MaxRetryAttempts))
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
            catch (HttpRequestException) when (IsSafeRead(method) && endpoints is not null && regionIndex < endpoints.Count - 1)
            {
                regionIndex++;
                continue;
            }
            catch (HttpRequestException) when (IsSafeRead(method) && attempt < options.MaxRetryAttempts)
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

        // Deserializing straight off the content stream avoids the
        // synchronizable-stream wrapper ReadFromJsonAsync allocates.
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        TResponse? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync(stream, responseTypeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Raw JsonException internals (paths, fragments of the body) never
            // escape to SDK consumers.
            throw new TerraScaleApiException(
                response.StatusCode,
                // The constant prefix keeps the redacted message non-null.
                TerraScaleApiException.RedactDiagnostic($"ts-auth returned an invalid response body: {exception.Message}")!,
                errorCode: "invalid_response",
                requestId: null,
                responseBody: null,
                retryAfter: TerraScaleAuthErrors.ResolveRetryAfter(response));
        }

        return payload ?? throw new TerraScaleApiException(
            response.StatusCode,
            "ts-auth returned an empty response body.",
            errorCode: null,
            requestId: null,
            responseBody: null,
            retryAfter: TerraScaleAuthErrors.ResolveRetryAfter(response));
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
