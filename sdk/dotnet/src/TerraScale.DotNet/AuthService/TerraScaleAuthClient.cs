using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthClient
{
    private const int MaxOperationBytes = 1_048_576;
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private readonly HttpClient transport;
    private readonly Uri origin;

    public TerraScaleAuthClient(HttpClient httpClient, TerraScaleAuthClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        (origin, InstanceSlug) = options.Validate();

        if (httpClient.BaseAddress is { } baseAddress)
        {
            if (TerraScaleOrigin.Parse(baseAddress, "HttpClient.BaseAddress", nameof(httpClient), TerraScaleService.Auth) != origin)
            {
                throw new ArgumentException(
                    "HttpClient.BaseAddress conflicts with the configured ts-auth origin.",
                    nameof(options));
            }
        }

        transport = httpClient;
        transport.BaseAddress ??= origin;
        Oidc = new TerraScaleAuthOidcClient(this);
        Platform = new TerraScaleAuthPlatformClient(this);
        Projects = new TerraScaleAuthProjectsClient(this);
    }

    public TerraScaleAuthOidcClient Oidc { get; }

    public TerraScaleAuthPlatformClient Platform { get; }

    public TerraScaleAuthProjectsClient Projects { get; }

    internal string InstanceSlug { get; }

    internal string ManagementRoute(string relative) => "/v1/" + TerraScaleAuthPaths.Escape(InstanceSlug) + relative;



    private async Task<AuthResult<TResponse>> GetMetadataCoreAsync<TResponse>(
        string requestUri,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Get,
            requestUri,
            bodyFactory: null,
            RequestAuthentication.None,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthResult<TResponse>> SendOAuthFormCoreAsync<TResponse>(
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
            authorization is null ? RequestAuthentication.None : new RequestAuthentication(authorization),
            cancellationToken).ConfigureAwait(false);
        var status = await RequireStatusAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        return status is null ? await ReadJsonAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false) : status;
    }

    private async Task<AuthOutcome> SendOAuthFormForSuccessCoreAsync(
        string requestUri,
        IReadOnlyList<KeyValuePair<string, string>> formValues,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(
            HttpMethod.Post,
            requestUri,
            CreateFormBodyFactory(formValues),
            authorization is null ? RequestAuthentication.None : new RequestAuthentication(authorization),
            cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
        {
            return await TerraScaleAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }
        return new Done();
    }

    private async Task<AuthResult<TResponse>> GetWithBearerCoreAsync<TResponse>(
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
            new RequestAuthentication(TerraScaleAuthCredentials.Bearer(accessToken)),
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthResult<TResponse>> SendAuthenticatedJsonCoreAsync<TRequest, TResponse>(
        HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType, AuthenticationHeaderValue authorization,
        HttpStatusCode expectedStatus, int maxRequestBytes, CancellationToken cancellationToken, string? stepUpProof = null)
        where TRequest : class
    {
        using var response = await SendRequestAsync(method, route,
            body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
            new RequestAuthentication(authorization), cancellationToken, stepUpProof: stepUpProof).ConfigureAwait(false);
        var status = await RequireStatusAsync(response, expectedStatus, cancellationToken).ConfigureAwait(false);
        return status is null ? await ReadJsonAsync(response, responseType, cancellationToken).ConfigureAwait(false) : status;
    }

    private async Task<AuthOutcome> SendAuthenticatedForStatusCoreAsync<TRequest>(
        HttpMethod method, string route, TRequest? body, JsonTypeInfo<TRequest> requestType,
        AuthenticationHeaderValue authorization, HttpStatusCode expectedStatus,
        int maxRequestBytes, CancellationToken cancellationToken, string? stepUpProof = null)
        where TRequest : class
    {
        using var response = await SendRequestAsync(method, route,
            body is null ? null : CreateJsonBodyFactory(body, requestType, maxRequestBytes),
            new RequestAuthentication(authorization), cancellationToken, stepUpProof: stepUpProof).ConfigureAwait(false);
        var status = await RequireStatusAsync(response, expectedStatus, cancellationToken).ConfigureAwait(false);
        return status is not null ? status : new Done();
    }

    private async Task<AuthCompletion> SendAuthenticatedForDeletionCoreAsync(string route, AuthenticationHeaderValue authorization, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(HttpMethod.Delete, route, null, new RequestAuthentication(authorization), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return new InProgress();
        }

        var status = await RequireStatusAsync(response, HttpStatusCode.NoContent, cancellationToken).ConfigureAwait(false);
        return status is not null ? status : new Done();
    }

    private async Task<AuthCompletion> SendForAccountDeletionCoreAsync(HttpMethod method, string route,
        AuthenticationHeaderValue? authorization, string deletionReceipt, CancellationToken cancellationToken, string? deletionAuthorization)
    {
        ValidateDeletionReceipt(deletionReceipt);

        using var response = await SendRequestAsync(method, route, null,
            authorization is null ? RequestAuthentication.None : new RequestAuthentication(authorization),
            cancellationToken, deletionReceipt, deletionAuthorization: deletionAuthorization).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return new InProgress();
        }

        var status = await RequireStatusAsync(response, HttpStatusCode.NoContent, cancellationToken).ConfigureAwait(false);
        return status is not null ? status : new Done();
    }

    private async Task<AuthResult<TerraScaleAuthAccountDeletion>> SendAccountDeletionWithProofCoreAsync(AuthenticationHeaderValue authorization,
        string stepUpProof, string? deletionReceipt, CancellationToken cancellationToken)
    {
        if (deletionReceipt is not null) ValidateDeletionReceipt(deletionReceipt);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await SendRequestAsync(HttpMethod.Delete, "/v1/terrascale/account", null,
            new RequestAuthentication(authorization), deadline.Token, deletionReceipt, stepUpProof).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            var status = await RequireStatusAsync(response, HttpStatusCode.NoContent, deadline.Token).ConfigureAwait(false);
            if (status is not null) return status;
        }
        string? grant = null;
        if (deletionReceipt is not null && response.Headers.TryGetValues("X-TerraScale-Deletion-Authorization", out var values))
        {
            var grants = values.ToArray();
            if (grants.Length != 1 || !TerraScaleAuthPlatformClient.IsCapability(grants[0]))
                return TerraScaleAuthErrors.InvalidResponse(response, "ts-auth returned an invalid deletion grant.");
            grant = grants[0];
        }
        return new TerraScaleAuthAccountDeletion(response.StatusCode == HttpStatusCode.NoContent, grant);
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

    private static async Task<AuthRefused?> RequireStatusAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
        {
            return await TerraScaleAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode != expected)
        {
            return TerraScaleAuthErrors.InvalidResponse(response, "ts-auth returned an unexpected success status.");
        }

        return null;
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
        RequestAuthentication authentication,
        CancellationToken cancellationToken,
        string? deletionReceipt = null,
        string? stepUpProof = null,
        string? deletionAuthorization = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = new Uri(origin, requestUri);
        using var request = new HttpRequestMessage(method, target);
        if (target.Scheme == Uri.UriSchemeHttps)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
        if (authentication.Kind == RequestAuthenticationKind.None && transport.DefaultRequestHeaders.Contains("Authorization"))
        {
            throw new InvalidOperationException("Unauthenticated ts-auth requests require an HttpClient without a default Authorization header.");
        }
        ApplyAuthentication(request, authentication);
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
        return TerraScaleOrigin.Sent(await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false), origin);
    }

    private void ApplyAuthentication(HttpRequestMessage request, RequestAuthentication authentication)
    {
        switch (authentication.Kind)
        {
            case RequestAuthenticationKind.None:
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

    private static async Task<AuthResult<TResponse>> ReadJsonAsync<TResponse>(
        HttpResponseMessage response,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
        {
            return await TerraScaleAuthErrors.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var media = response.Content.Headers.ContentType;
        if (!string.Equals(media?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || media?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return TerraScaleAuthErrors.InvalidResponse(response, "ts-auth returned an unexpected response media type.");
        }

        using var destination = new BoundedHttpBodyStream(MaxResponseBytes,
            () => new InvalidDataException(), cancellationToken);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return TerraScaleAuthErrors.InvalidResponse(response, "ts-auth response exceeds the byte limit.");
        }
        try
        {
            await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TerraScaleAuthErrors.InvalidResponse(response, "ts-auth response body could not be read.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        TResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize(destination.Written.Span, responseTypeInfo);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return TerraScaleAuthErrors.InvalidResponse(response, "ts-auth returned an invalid response body.");
        }

        return payload is not null ? payload : TerraScaleAuthErrors.InvalidResponse(response, "ts-auth returned an empty response body.");
    }

    private readonly record struct RequestAuthentication(RequestAuthenticationKind Kind, AuthenticationHeaderValue? Header = null)
    {
        public static readonly RequestAuthentication None = new(RequestAuthenticationKind.None);

        public RequestAuthentication(AuthenticationHeaderValue header) : this(RequestAuthenticationKind.Explicit, header)
        {
        }
    }

    private enum RequestAuthenticationKind
    {
        None,
        Explicit
    }
}
