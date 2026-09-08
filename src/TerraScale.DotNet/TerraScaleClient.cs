using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cysharp.Text;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet;

/// <summary>
/// Entry point for TerraScale database API operations.
/// </summary>
public sealed class TerraScaleClient
{
    private const int MaxOperationBytes = 1_048_576;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient httpClient;
    private readonly TerraScaleClientOptions options;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> sessionConsistencyTokens =
        new(StringComparer.OrdinalIgnoreCase);
    private Uri? pinnedRestBaseAddress;

    /// <summary>
    /// Creates a TerraScale client over a caller-owned <see cref="HttpClient"/>.
    /// Its underlying handler must disable automatic redirects; only explicit TerraScale 421 routing repair is supported.
    /// </summary>
    public TerraScaleClient(HttpClient httpClient, TerraScaleClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        var optionSnapshot = options with { };
        optionSnapshot.Validate();

        if (httpClient.BaseAddress is not null && httpClient.BaseAddress != optionSnapshot.BaseAddress)
        {
            throw new ArgumentException(
                $"HttpClient.BaseAddress '{httpClient.BaseAddress}' conflicts with TerraScaleClientOptions.BaseAddress '{optionSnapshot.BaseAddress}'.",
                nameof(options));
        }

        this.httpClient = httpClient;
        this.options = optionSnapshot;
        jsonOptions = DefaultJsonOptions;

        this.httpClient.BaseAddress ??= optionSnapshot.BaseAddress;
        Databases = new TerraScaleDatabasesClient(this);
        Operations = new TerraScaleOperationsClient(this);
        AuthPersistence = new TerraScaleAuthPersistenceClient(this);
        Collections = new TerraScaleCollectionsClient(this);
        Documents = new TerraScaleDocumentsClient(this);
        Regions = new TerraScaleRegionsClient(this);
        Backups = new TerraScaleBackupsClient(this);
        Management = new TerraScaleManagementClient(this);
        Tenants = new TerraScaleTenantsClient(this);
        Sql = new TerraScaleSqlClient(this);
        Reactive = new TerraScaleReactiveHttpClient(
            httpClient,
            new TerraScaleReactiveHttpClientOptions
            {
                BaseAddress = ResolveReactiveBaseAddress(optionSnapshot.BaseAddress),
                ApiBaseAddress = optionSnapshot.BaseAddress,
                // Public API keys use the same bearer scheme on REST and sync.
                BearerToken = !string.IsNullOrWhiteSpace(optionSnapshot.ApiKey)
                    ? optionSnapshot.ApiKey.Trim()
                    : optionSnapshot.BearerToken,
                BearerTokenProvider = string.IsNullOrWhiteSpace(optionSnapshot.ApiKey)
                    ? optionSnapshot.BearerTokenProvider
                    : null,
                MaxRetryAttempts = optionSnapshot.MaxRetryAttempts,
                RetryDelay = optionSnapshot.RetryDelay,
                RegionHint = optionSnapshot.PreferredRegion,
            });
    }

    /// <summary>
    /// Database management operations.
    /// </summary>
    public TerraScaleDatabasesClient Databases { get; }

    /// <summary>Authoritative provisioning operation lookup.</summary>
    public TerraScaleOperationsClient Operations { get; }

    /// <summary>Named public authentication persistence operations.</summary>
    public TerraScaleAuthPersistenceClient AuthPersistence { get; }

    /// <summary>
    /// Database collection operations.
    /// </summary>
    public TerraScaleCollectionsClient Collections { get; }

    /// <summary>
    /// Database document operations.
    /// </summary>
    public TerraScaleDocumentsClient Documents { get; }

    /// <summary>
    /// TerraScale region catalog operations.
    /// </summary>
    public TerraScaleRegionsClient Regions { get; }

    /// <summary>
    /// Project database backup, restore point, and restore operations.
    /// </summary>
    public TerraScaleBackupsClient Backups { get; }

    /// <summary>The composed management surface over this client.</summary>
    public TerraScaleManagementClient Management { get; }

    /// <summary>Database-per-tenant lifecycle operations over this client.</summary>
    public TerraScaleTenantsClient Tenants { get; }

    /// <summary>Typed-builder and raw parameterized read-only SQL operations.</summary>
    public TerraScaleSqlClient Sql { get; }

    /// <summary>CDN-first HTTP reactive view operations over this client.</summary>
    public TerraScaleReactiveHttpClient Reactive { get; }





    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, route.RequestUri, body, cancellationToken);

    internal Task<TResponse> SendDashboardAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse, TResponse>(
            method,
            route.RequestUri,
            body,
            preferredRegion: null,
            configureRequest: null,
            responseFactory: static (response, _) => response,
            cancellationToken: cancellationToken,
            idempotencyKeyOverride: null,
            allowUnkeyedPutRetry: true,
            errorContract: TerraScaleErrorContract.DashboardProduct);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, requestUri, body, configureRequest: null, cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        Action<HttpRequestMessage>? configureRequest,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, route.RequestUri, body, configureRequest, cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        Action<HttpRequestMessage>? configureRequest,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, requestUri, body, preferredRegion: null, configureRequest, cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        string? preferredRegion,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, route.RequestUri, body, preferredRegion, cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        string? preferredRegion,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, requestUri, body, preferredRegion, configureRequest: null, cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        string? preferredRegion,
        Action<HttpRequestMessage>? configureRequest,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(
            method,
            route.RequestUri,
            body,
            preferredRegion,
            configureRequest,
            cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        string? preferredRegion,
        Action<HttpRequestMessage>? configureRequest,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse, TResponse>(
            method,
            requestUri,
            body,
            preferredRegion,
            configureRequest,
            static (response, _) => response,
            cancellationToken);

    internal async Task<AuthResult> SendAuthPersistenceAsync<T>(HttpMethod method, string route, T? body, JsonTypeInfo<T>? metadata, string? idempotencyKey, string? transactionId, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetriesAsync(method, route,
            body is null ? static () => null : CreateJsonContentFactory(body, metadata!), RequestAuthenticationMode.Authenticated,
            request =>
            {
                if (transactionId is not null) request.Headers.Add("X-TerraScale-Auth-Transaction", transactionId);
            }, activity: null, cancellationToken, idempotencyKey).ConfigureAwait(false);
        return await ReadJsonResponseAsync(response, TerraScaleContractJsonContext.Default.AuthResult, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SchemaDocument> PutCollectionSchemaAsync(string route, CollectionSchema schema, string idempotencyKey, ulong? expectedGeneration, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetriesAsync(HttpMethod.Put, route,
            CreateJsonContentFactory(schema, TerraScaleContractJsonContext.Default.CollectionSchema), RequestAuthenticationMode.Authenticated,
            request =>
            {
                if (expectedGeneration is { } generation)
                    request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\""));
            }, activity: null, cancellationToken, idempotencyKey, allowUnkeyedPutRetry: false).ConfigureAwait(false);
        return await ReadJsonResponseAsync(response, TerraScaleContractJsonContext.Default.SchemaDocument, activity: null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ExportDownload> DownloadExportArtifactAsync(string route, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetriesAsync(HttpMethod.Get, route, static () => null, RequestAuthenticationMode.Authenticated,
            request => request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream")), activity: null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/octet-stream")
            throw new TerraScaleApiException(response.StatusCode, "The export artifact response must be HTTP 200 application/octet-stream.", null, ReadRequestId(response), null, null);
        var lengthHeader = ExactlyOneHeader(response, "X-TerraScale-Artifact-Length");
        if (lengthHeader is null || !ulong.TryParse(lengthHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            length.ToString(CultureInfo.InvariantCulture) != lengthHeader)
            throw new TerraScaleApiException(response.StatusCode, "The export artifact length header is invalid.", null, ReadRequestId(response), null, null);
        var checksum = ExactlyOneHeader(response, "X-TerraScale-Artifact-Checksum");
        if (checksum is null || checksum.Length != 64 || checksum.Any(character => !Uri.IsHexDigit(character)))
            throw new TerraScaleApiException(response.StatusCode, "The export artifact checksum header is invalid.", null, ReadRequestId(response), null, null);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if ((ulong)bytes.LongLength != length)
            throw new TerraScaleApiException(response.StatusCode, "The export artifact length header does not match the response body.", null, ReadRequestId(response), null, null);
        return new ExportDownload(bytes, length, checksum.ToLowerInvariant());
    }

    private static string? ExactlyOneHeader(HttpResponseMessage response, string name)
    {
        var values = response.Headers.TryGetValues(name, out var responseValues)
            ? responseValues.ToArray()
            : response.Content.Headers.TryGetValues(name, out var contentValues) ? contentValues.ToArray() : [];
        return values.Length == 1 && !values[0].Contains(',', StringComparison.Ordinal) ? values[0] : null;
    }

    internal async Task<TerraScaleDatabaseCreationResult> CreateDatabaseAsync(CreateDatabaseRequest request, string operationId, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetriesAsync(HttpMethod.Post, "/api/v1/databases",
            CreateJsonContentFactory(request, TerraScaleContractJsonContext.Default.CreateDatabaseRequest), RequestAuthenticationMode.Authenticated,
            configureRequest: null, activity: null, cancellationToken, operationId).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.Created => new TerraScaleDatabaseCreated(await ReadJsonResponseAsync(response, TerraScaleContractJsonContext.Default.DatabaseRecord, null, cancellationToken).ConfigureAwait(false)),
            HttpStatusCode.Accepted => new TerraScaleDatabaseAccepted(await ReadJsonResponseAsync(response, TerraScaleContractJsonContext.Default.ProvisioningOperation, null, cancellationToken).ConfigureAwait(false)),
            _ => throw new TerraScaleApiException(response.StatusCode, "Database creation requires HTTP 201 or 202.", null, ReadRequestId(response), null, null),
        };
    }

    internal Task<TResponse> SendJsonAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken,
        string? idempotencyKey = null) =>
        SendJsonAsync(
            method,
            route.RequestUri,
            responseTypeInfo,
            cancellationToken,
            idempotencyKey);

    internal Task<TResponse> SendJsonAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken,
        string? idempotencyKey = null) =>
        SendTypedJsonAsync<object, TResponse>(method, requestUri, body: null, requestTypeInfo: null, responseTypeInfo, cancellationToken, idempotencyKey);

    internal Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken,
        string? idempotencyKey = null) =>
        SendJsonAsync(
            method,
            route.RequestUri,
            body,
            requestTypeInfo,
            responseTypeInfo,
            cancellationToken,
            idempotencyKey);

    internal Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string requestUri,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken,
        string? idempotencyKey = null) =>
        SendTypedJsonAsync(method, requestUri, body, requestTypeInfo, responseTypeInfo, cancellationToken, idempotencyKey);

    internal Task<TResponse> SendNonRetryableJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendNonRetryableJsonAsync(
            method,
            route.RequestUri,
            body,
            requestTypeInfo,
            responseTypeInfo,
            cancellationToken);

    internal Task<TResponse> SendNonRetryableJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string requestUri,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        SendTypedJsonAsync(
            method,
            requestUri,
            body,
            requestTypeInfo,
            responseTypeInfo,
            cancellationToken,
            idempotencyKeyOverride: string.Empty,
            allowUnkeyedPutRetry: false);

    private async Task<TResponse> SendTypedJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string requestUri,
        TRequest? body,
        JsonTypeInfo<TRequest>? requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken,
        string? idempotencyKeyOverride = null,
        bool allowUnkeyedPutRetry = true)
    {
        using var activity = StartActivity(method, requestUri);
        Func<HttpContent?> contentFactory;
        if (body is null)
        {
            contentFactory = static () => null;
        }
        else
        {
            var resolvedRequestTypeInfo = requestTypeInfo
                ?? throw new InvalidOperationException("A request JSON type descriptor is required when a request body is present.");
            contentFactory = CreateJsonContentFactory(body, resolvedRequestTypeInfo);
        }

        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            contentFactory,
            RequestAuthenticationMode.Authenticated,
            configureRequest: null,
            activity,
            cancellationToken,
            idempotencyKeyOverride,
            allowUnkeyedPutRetry).ConfigureAwait(false);

        SetResponseTags(activity, response);
        return await ReadJsonResponseAsync(response, responseTypeInfo, activity, cancellationToken).ConfigureAwait(false);
    }

    internal Task<TResponse> SendWithIdempotencyKeyAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendWithIdempotencyKeyAsync<TResponse>(
            method,
            route.RequestUri,
            body,
            idempotencyKey,
            cancellationToken);

    internal Task<TResponse> SendWithIdempotencyKeyAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse, TResponse>(
            method,
            requestUri,
            body,
            preferredRegion: null,
            configureRequest: null,
            responseFactory: static (response, _) => response,
            cancellationToken,
            idempotencyKey);

    internal Task<TResponse> SendNonRetryableMutationAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        CancellationToken cancellationToken) =>
        SendNonRetryableMutationAsync<TResponse>(
            method,
            route.RequestUri,
            body,
            cancellationToken);

    internal Task<TResponse> SendNonRetryableMutationAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse, TResponse>(
            method,
            requestUri,
            body,
            preferredRegion: null,
            configureRequest: null,
            responseFactory: static (response, _) => response,
            cancellationToken,
            idempotencyKeyOverride: string.Empty,
            allowUnkeyedPutRetry: false);

    internal Task SendNonRetryableMutationAsync(
        HttpMethod method,
        TerraScaleBoundRoute route,
        CancellationToken cancellationToken) =>
        SendNonRetryableMutationAsync(method, route.RequestUri, cancellationToken);

    internal Task SendNonRetryableMutationAsync(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken) =>
        SendAsync(
            method,
            requestUri,
            cancellationToken,
            idempotencyKeyOverride: string.Empty,
            allowUnkeyedPutRetry: false);

    internal Task SendNonRetryableNoContentAsync(
        HttpMethod method,
        TerraScaleBoundRoute route,
        CancellationToken cancellationToken) =>
        SendNonRetryableNoContentAsync(method, route.RequestUri, cancellationToken);

    internal async Task SendNonRetryableNoContentAsync(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var requestUri = route.RequestUri;
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateJsonContentFactory(body),
            RequestAuthenticationMode.Authenticated,
            configureRequest: null,
            activity,
            cancellationToken,
            idempotencyKeyOverride: string.Empty,
            allowUnkeyedPutRetry: false).ConfigureAwait(false);

        SetResponseTags(activity, response);
        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw new TerraScaleApiException(
                response.StatusCode,
                $"TerraScale returned HTTP {(int)response.StatusCode}, but HTTP {(int)HttpStatusCode.NoContent} was required.",
                errorCode: null,
                requestId: ReadRequestId(response),
                responseBody: null,
                retryAfter: response.Headers.RetryAfter?.Delta);
        }
    }

    internal Task SendNonRetryableNoContentAsync(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken) =>
        SendAsync(
            method,
            requestUri,
            cancellationToken,
            idempotencyKeyOverride: string.Empty,
            allowUnkeyedPutRetry: false,
            expectedStatusCode: HttpStatusCode.NoContent);

    internal Task SendNoContentAsync(
        HttpMethod method,
        TerraScaleBoundRoute route,
        Action<HttpRequestMessage>? configureRequest,
        string? idempotencyKey,
        CancellationToken cancellationToken) =>
        SendNoContentAsync(method, route.RequestUri, configureRequest, idempotencyKey, cancellationToken);

    internal async Task SendNoContentAsync(
        HttpMethod method,
        string requestUri,
        Action<HttpRequestMessage>? configureRequest,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateJsonContentFactory(body: null),
            RequestAuthenticationMode.Authenticated,
            configureRequest,
            activity: null,
            cancellationToken,
            idempotencyKey,
            allowUnkeyedPutRetry: false).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new TerraScaleApiException(response.StatusCode, "TerraScale document deletion requires HTTP 204.", null, ReadRequestId(response), null, response.Headers.RetryAfter?.Delta);
    }

    internal Task<TResult> SendAsync<TResponse, TResult>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        string? preferredRegion,
        Action<HttpRequestMessage>? configureRequest,
        Func<TResponse, HttpResponseMessage, TResult> responseFactory,
        CancellationToken cancellationToken) =>
        SendAsync(
            method,
            route.RequestUri,
            body,
            preferredRegion,
            configureRequest,
            responseFactory,
            cancellationToken);

    internal Task<TResult> SendAsync<TResponse, TResult>(
        HttpMethod method,
        string requestUri,
        object? body,
        string? preferredRegion,
        Action<HttpRequestMessage>? configureRequest,
        Func<TResponse, HttpResponseMessage, TResult> responseFactory,
        CancellationToken cancellationToken) =>
        SendAsync(
            method,
            requestUri,
            body,
            preferredRegion,
            configureRequest,
            responseFactory,
            cancellationToken,
            idempotencyKeyOverride: null,
            allowUnkeyedPutRetry: true);


    internal async Task<TResult> SendAsync<TResponse, TResult>(
        HttpMethod method,
        string requestUri,
        object? body,
        string? preferredRegion,
        Action<HttpRequestMessage>? configureRequest,
        Func<TResponse, HttpResponseMessage, TResult> responseFactory,
        CancellationToken cancellationToken,
        string? idempotencyKeyOverride,
        bool allowUnkeyedPutRetry = true,
        TerraScaleErrorContract errorContract = TerraScaleErrorContract.TerraBase)
    {
        ArgumentNullException.ThrowIfNull(responseFactory);
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateJsonContentFactory(body),
            RequestAuthenticationMode.Authenticated,
            configureRequest: request =>
            {
                if (preferredRegion is not null)
                {
                    ApplyPreferredRegion(request, preferredRegion);
                }

                configureRequest?.Invoke(request);
            },
            activity,
            cancellationToken,
            idempotencyKeyOverride,
            allowUnkeyedPutRetry).ConfigureAwait(false);

        SetResponseTags(activity, response);
        var payload = await ReadJsonResponseAsync<TResponse>(response, activity, cancellationToken, errorContract).ConfigureAwait(false);
        return responseFactory(payload, response);
    }

    private static void ApplyPreferredRegion(HttpRequestMessage request, string preferredRegion)
    {
        var regionCode = TerraScaleRegionCodes.Validate(preferredRegion, nameof(preferredRegion));
        request.Headers.Remove("X-TerraScale-Preferred-Region");
        request.Headers.TryAddWithoutValidation("X-TerraScale-Preferred-Region", regionCode);
    }

    internal async Task<TResponse> SendAnonymousJsonAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        object? body,
        CancellationToken cancellationToken) =>
        await SendAnonymousJsonAsync<TResponse>(
            method,
            route.RequestUri,
            body,
            cancellationToken).ConfigureAwait(false);

    internal async Task<TResponse> SendAnonymousJsonAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateJsonContentFactory(body),
            RequestAuthenticationMode.Anonymous,
            configureRequest: null,
            activity,
            cancellationToken).ConfigureAwait(false);

        SetResponseTags(activity, response);
        return await ReadJsonResponseAsync<TResponse>(response, activity, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TResponse> SendAnonymousJsonAsync<TResponse>(
        HttpMethod method,
        TerraScaleBoundRoute route,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken) =>
        await SendAnonymousJsonAsync(
            method,
            route.RequestUri,
            responseTypeInfo,
            cancellationToken).ConfigureAwait(false);

    internal async Task<TResponse> SendAnonymousJsonAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateJsonContentFactory(body: null),
            RequestAuthenticationMode.Anonymous,
            configureRequest: null,
            activity,
            cancellationToken).ConfigureAwait(false);

        SetResponseTags(activity, response);
        return await ReadJsonResponseAsync(response, responseTypeInfo, activity, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TResponse> SendAnonymousFormAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        IReadOnlyList<KeyValuePair<string, string>> formValues,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            () => new FormUrlEncodedContent(formValues),
            RequestAuthenticationMode.Anonymous,
            request =>
            {
                if (authorization is not null)
                {
                    request.Headers.Authorization = authorization;
                }
            },
            activity,
            cancellationToken).ConfigureAwait(false);

        SetResponseTags(activity, response);
        return await ReadJsonResponseAsync<TResponse>(response, activity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> ReadJsonResponseAsync<TResponse>(
        HttpResponseMessage response,
        Activity? activity,
        CancellationToken cancellationToken,
        TerraScaleErrorContract errorContract = TerraScaleErrorContract.TerraBase)
    {
        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken, errorContract).ConfigureAwait(false);
        }

        var payload = await response.Content.ReadFromJsonAsync<TResponse>(
            jsonOptions,
            cancellationToken).ConfigureAwait(false);

        return payload ?? throw new TerraScaleApiException(
            response.StatusCode,
            "TerraScale returned an empty response body.",
            errorCode: null,
            requestId: ReadRequestId(response),
            responseBody: null,
            retryAfter: response.Headers.RetryAfter?.Delta);
    }

    private async Task<TResponse> ReadJsonResponseAsync<TResponse>(
        HttpResponseMessage response,
        JsonTypeInfo<TResponse> responseTypeInfo,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var payload = await response.Content.ReadFromJsonAsync(responseTypeInfo, cancellationToken).ConfigureAwait(false);
        return payload ?? throw new TerraScaleApiException(
            response.StatusCode,
            "TerraScale returned an empty response body.",
            errorCode: null,
            requestId: ReadRequestId(response),
            responseBody: null,
            retryAfter: response.Headers.RetryAfter?.Delta);
    }

    internal async Task SendAsync(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken,
        string? idempotencyKeyOverride = null,
        bool allowUnkeyedPutRetry = true,
        HttpStatusCode? expectedStatusCode = null)
    {
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateJsonContentFactory(body: null),
            RequestAuthenticationMode.Authenticated,
            configureRequest: null,
            activity,
            cancellationToken,
            idempotencyKeyOverride,
            allowUnkeyedPutRetry).ConfigureAwait(false);

        SetResponseTags(activity, response);

        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        if (expectedStatusCode is { } expected && response.StatusCode != expected)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw new TerraScaleApiException(
                response.StatusCode,
                $"TerraScale returned HTTP {(int)response.StatusCode}, but HTTP {(int)expected} was required.",
                errorCode: null,
                requestId: ReadRequestId(response),
                responseBody: null,
                retryAfter: response.Headers.RetryAfter?.Delta);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpMethod method,
        string requestUri,
        Func<HttpContent?> contentFactory,
        RequestAuthenticationMode authenticationMode,
        Action<HttpRequestMessage>? configureRequest,
        Activity? activity,
        CancellationToken cancellationToken,
        string? idempotencyKeyOverride = null,
        bool allowUnkeyedPutRetry = true)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        var requestCancellationToken = deadline.Token;
        var requestId = CreateRequestId();
        var idempotencyKey = idempotencyKeyOverride ?? CreateIdempotencyKey(method);
        var attempt = 0;
        var repairedRestPin = false;

        while (true)
        {
            using var request = await CreateRequestAsync(
                method,
                requestUri,
                contentFactory,
                authenticationMode,
                configureRequest,
                requestId,
                idempotencyKey,
                requestCancellationToken).ConfigureAwait(false);

            try
            {
                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.MisdirectedRequest &&
                    !repairedRestPin &&
                    TryRepairRestPin(response, request.RequestUri))
                {
                    repairedRestPin = true;
                    response.Dispose();
                    continue;
                }

                ObserveRestPin(response);
                if (!ShouldRetryResponse(response, method, idempotencyKey, attempt, allowUnkeyedPutRetry))
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                if (delay > MaxRetryAfter)
                {
                    return response;
                }

                AddRetryEvent(activity, attempt + 1, delay, $"HTTP {(int)response.StatusCode}");
                response.Dispose();
                await DelayRetryAsync(delay, requestCancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (CanRetry(method, idempotencyKey, attempt, allowUnkeyedPutRetry))
            {
                var delay = GetRetryDelay(response: null, attempt);
                AddRetryEvent(activity, attempt + 1, delay, exception.GetType().Name);
                await DelayRetryAsync(delay, requestCancellationToken).ConfigureAwait(false);
            }

            attempt++;
        }
    }

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string requestUri,
        Func<HttpContent?> contentFactory,
        RequestAuthenticationMode authenticationMode,
        Action<HttpRequestMessage>? configureRequest,
        string? requestId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, requestUri);
        try
        {
            ApplyRestPin(request);
            ApplyRequestVersion(request);
            if (authenticationMode == RequestAuthenticationMode.Authenticated)
            {
                await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
                if (options.TenantId is { } tenant && options.ProjectId is { } project)
                {
                    request.Headers.Add("X-TerraScale-Tenant-Id", tenant);
                    request.Headers.Add("X-TerraScale-Project-Id", project);
                }
                if (options.RegionAffinity == TerraScaleRegionAffinity.Failover &&
                    options.PreferredRegion is { } preferredRegion)
                {
                    ApplyPreferredRegion(request, preferredRegion);
                }
            }

            ApplyRequestHeaders(request, requestId, idempotencyKey);

            var content = contentFactory();
            if (content is not null)
            {
                request.Content = content;
            }

            configureRequest?.Invoke(request);
            if (request.Headers.Accept.Count == 0)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private void ApplyRestPin(HttpRequestMessage request)
    {
        var pinnedBaseAddress = Volatile.Read(ref pinnedRestBaseAddress);
        if (pinnedBaseAddress is not null && request.RequestUri is { IsAbsoluteUri: false } relativeUri)
        {
            request.RequestUri = new Uri(pinnedBaseAddress, relativeUri);
        }
    }

    private void ObserveRestPin(HttpResponseMessage response)
    {
        if (TryReadRegionHeader(response, "X-TerraScale-Region", out var region))
        {
            PinRestRegion(region);
        }
    }

    private bool TryRepairRestPin(HttpResponseMessage response, Uri? requestUri)
    {
        if (options.RegionAffinity == TerraScaleRegionAffinity.Strict ||
            !IsTerraScaleRestHost(options.BaseAddress.Host) ||
            !response.Headers.TryGetValues("X-TerraScale-Placement", out var values))
        {
            return false;
        }

        var requestRegion = RegionFromHost(requestUri?.Host);
        foreach (var value in values)
        {
            foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsRegion(candidate) && !string.Equals(candidate, requestRegion, StringComparison.Ordinal))
                {
                    PinRestRegion(candidate);
                    return true;
                }
            }
        }

        return false;
    }

    private void PinRestRegion(string region)
    {
        if (options.RegionAffinity == TerraScaleRegionAffinity.Strict ||
            !IsTerraScaleRestHost(options.BaseAddress.Host) ||
            !IsRegion(region))
        {
            return;
        }

        var builder = new UriBuilder(options.BaseAddress)
        {
            Host = $"{region.ToLowerInvariant()}.api.terrascale.tech"
        };
        Volatile.Write(ref pinnedRestBaseAddress, builder.Uri);
    }

    private static bool TryReadRegionHeader(
        HttpResponseMessage response,
        string headerName,
        out string region)
    {
        if (response.Headers.TryGetValues(headerName, out var values))
        {
            var candidate = values.FirstOrDefault()?.Trim();
            if (candidate is not null && IsRegion(candidate))
            {
                region = candidate;
                return true;
            }
        }

        region = string.Empty;
        return false;
    }

    private static bool IsTerraScaleRestHost(string host) =>
        string.Equals(host, "api.terrascale.tech", StringComparison.OrdinalIgnoreCase) ||
        RegionFromHost(host) is not null;

    private static string? RegionFromHost(string? host)
    {
        const string suffix = ".api.terrascale.tech";
        if (host is null || !host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = host[..^suffix.Length].ToUpperInvariant();
        return IsRegion(candidate) ? candidate : null;
    }

    private static bool IsRegion(string region) =>
        region is TerraScaleRegions.Gru or TerraScaleRegions.Sfo or TerraScaleRegions.Fra;

    private static Uri ResolveReactiveBaseAddress(Uri restBaseAddress)
    {
        if (string.Equals(restBaseAddress.Host, "api.terrascale.tech", StringComparison.OrdinalIgnoreCase) ||
            RegionFromHost(restBaseAddress.Host) is not null)
        {
            return new Uri("https://sync.api.terrascale.tech", UriKind.Absolute);
        }

        return restBaseAddress;
    }

    private Func<HttpContent?> CreateJsonContentFactory(object? body)
    {
        if (body is null)
        {
            return static () => null;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(body, jsonOptions);
        ValidateOperationSize(payload.Length);
        return () => CreateJsonContent(payload);
    }

    private static Func<HttpContent?> CreateJsonContentFactory<TRequest>(TRequest body, JsonTypeInfo<TRequest> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, typeInfo);
        ValidateOperationSize(payload.Length);
        return () => CreateJsonContent(payload);
    }

    private static HttpContent CreateJsonContent(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static void ValidateOperationSize(int bytes)
    {
        if (bytes > MaxOperationBytes)
        {
            throw new ArgumentException(
                $"The serialized TerraScale operation exceeds the {MaxOperationBytes} byte limit.",
                "body");
        }
    }

    private string? CreateIdempotencyKey(HttpMethod method)
    {
        if (!IsMutation(method) || options.IdempotencyKeyFactory is null)
        {
            return null;
        }

        var idempotencyKey = options.IdempotencyKeyFactory();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new InvalidOperationException("IdempotencyKeyFactory must return a nonblank key.");
        }

        var normalized = idempotencyKey.Trim();
        if (System.Text.Encoding.UTF8.GetByteCount(normalized) > 128)
        {
            throw new InvalidOperationException("IdempotencyKeyFactory must return at most 128 UTF-8 bytes.");
        }

        return normalized;
    }

    private string? CreateRequestId()
    {
        if (options.RequestIdFactory is null)
        {
            return null;
        }

        var requestId = options.RequestIdFactory();
        return string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
    }

    private async ValueTask ApplyAuthenticationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
            return;
        }

        var bearerToken = await ResolveBearerTokenAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new InvalidOperationException("Configure an API key, bearer token, or bearer token provider for authenticated TerraScale API calls.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
    }






    private async ValueTask<string?> ResolveBearerTokenAsync(
        CancellationToken cancellationToken)
    {
        var bearerToken = options.BearerToken;
        if (string.IsNullOrWhiteSpace(bearerToken) && options.BearerTokenProvider is { } provider)
        {
            bearerToken = await provider(cancellationToken).ConfigureAwait(false);
        }

        return bearerToken;
    }

    internal void ApplyDocumentReadHeaders(
        HttpRequestMessage request,
        string database,
        string? documentId,
        TerraScaleDocumentReadOptions? readOptions)
    {
        if (readOptions?.AllowStale == true)
        {
            request.Headers.TryAddWithoutValidation("X-TerraScale-Allow-Stale", "true");
        }

        if (documentId is null || options.SessionConsistency != TerraScaleSessionConsistency.Automatic ||
            !string.IsNullOrWhiteSpace(readOptions?.Session))
        {
            return;
        }

        var scope = DocumentConsistencyScope(database, documentId);
        if (sessionConsistencyTokens.TryGetValue(scope, out var token))
        {
            request.Headers.TryAddWithoutValidation("X-TerraScale-Session", token);
        }
    }

    internal TerraScaleDocument ObserveDocumentWrite(
        TerraScaleDocument document,
        HttpResponseMessage response,
        string requestDatabase)
    {
        RememberSessionConsistencyToken(
            document.SessionConsistencyToken,
            document.DatabaseId,
            document.Id);
        RememberSessionConsistencyToken(
            document.SessionConsistencyToken,
            requestDatabase,
            document.Id);
        return document with { Routing = TerraScaleRoutingObservation.From(response) };
    }

    internal TerraScaleDocument ObserveDocumentRead(
        TerraScaleDocument document,
        HttpResponseMessage response)
    {
        RememberSessionConsistencyToken(
            document.SessionConsistencyToken,
            document.DatabaseId,
            document.Id);
        return document with { Routing = TerraScaleRoutingObservation.From(response) };
    }

    internal TerraScaleDocumentPage ObserveDocumentPage(
        TerraScaleDocumentPage page,
        HttpResponseMessage response) =>
        page with { Routing = TerraScaleRoutingObservation.From(response) };

    internal TerraScaleDocumentBatch ObserveDocumentBatch(
        TerraScaleDocumentBatch batch,
        HttpResponseMessage response,
        string requestDatabase)
    {
        foreach (var result in batch.Results)
        {
            if (result.Document is { } document)
            {
                RememberSessionConsistencyToken(
                    document.SessionConsistencyToken,
                    batch.DatabaseId,
                    document.Id);
                RememberSessionConsistencyToken(
                    document.SessionConsistencyToken,
                    requestDatabase,
                    document.Id);
            }

            if (result.Deleted is { } deleted)
            {
                RememberSessionConsistencyToken(
                    deleted.SessionConsistencyToken,
                    batch.DatabaseId,
                    deleted.Id);
                RememberSessionConsistencyToken(
                    deleted.SessionConsistencyToken,
                    requestDatabase,
                    deleted.Id);
            }
        }

        return batch with { Routing = TerraScaleRoutingObservation.From(response) };
    }

    private void RememberSessionConsistencyToken(
        string? token,
        string database,
        string? documentId)
    {
        if (documentId is not null &&
            options.SessionConsistency == TerraScaleSessionConsistency.Automatic &&
            token is { Length: > 0 and <= 4096 })
        {
            sessionConsistencyTokens[DocumentConsistencyScope(database, documentId)] = token;
        }
    }

    private static string DocumentConsistencyScope(string database, string documentId) =>
        $"{database.Trim()}\n{documentId.Trim()}";

    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        string? requestId,
        string? idempotencyKey)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }
    }

    private void ApplyRequestVersion(HttpRequestMessage request)
    {
        request.Version = httpClient.DefaultRequestVersion;
        request.VersionPolicy = httpClient.DefaultVersionPolicy;
    }

    private bool ShouldRetryResponse(
        HttpResponseMessage response,
        HttpMethod method,
        string? idempotencyKey,
        int attempt,
        bool allowUnkeyedPutRetry) =>
        CanRetry(method, idempotencyKey, attempt, allowUnkeyedPutRetry) &&
        IsRetryableStatus(response.StatusCode);

    private bool CanRetry(HttpMethod method, string? idempotencyKey, int attempt, bool allowUnkeyedPutRetry) =>
        attempt < options.MaxRetryAttempts &&
        ((IsSafeRetryMethod(method) && (method != HttpMethod.Put || allowUnkeyedPutRetry)) ||
         !string.IsNullOrWhiteSpace(idempotencyKey));

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response is null ? null : GetRetryAfter(response);
        if (retryAfter is { } explicitDelay)
        {
            return explicitDelay;
        }

        if (options.RetryDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, attempt);
        var milliseconds = Math.Min(
            options.RetryDelay.TotalMilliseconds * multiplier,
            TimeSpan.FromSeconds(30).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return BoundRetryAfter(delta);
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return BoundRetryAfter(date - DateTimeOffset.UtcNow);
        }

        return null;
    }

    private static TimeSpan BoundRetryAfter(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? TimeSpan.Zero : delay;

    private static Task DelayRetryAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static bool IsSafeRetryMethod(HttpMethod method) =>
        method == HttpMethod.Get ||
        method == HttpMethod.Head ||
        method == HttpMethod.Options ||
        method == HttpMethod.Trace ||
        method == HttpMethod.Put;

    private static bool IsMutation(HttpMethod method) =>
        method != HttpMethod.Get &&
        method != HttpMethod.Head &&
        method != HttpMethod.Options &&
        method != HttpMethod.Trace;

    private static Activity? StartActivity(HttpMethod method, string requestUri)
    {
        var activity = TerraScaleTelemetry.ActivitySource.StartActivity(
            $"TerraScale {method.Method}",
            ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("http.request.method", method.Method);
        activity.SetTag("url.path", requestUri);
        activity.SetTag("server.address", "TerraScale.Dashboard.Api");
        return activity;
    }

    private static void SetResponseTags(Activity? activity, HttpResponseMessage response)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("http.response.status_code", (int)response.StatusCode);
        if (ReadRequestId(response) is { Length: > 0 } requestId)
        {
            activity.SetTag("terrascale.request_id", requestId);
        }
    }

    private static void AddRetryEvent(
        Activity? activity,
        int attempt,
        TimeSpan delay,
        string reason)
    {
        activity?.AddEvent(
            new ActivityEvent(
                "terrascale.retry",
                tags: new ActivityTagsCollection
                {
                    ["terrascale.retry.attempt"] = attempt,
                    ["terrascale.retry.delay_ms"] = delay.TotalMilliseconds,
                    ["terrascale.retry.reason"] = reason
                }));
    }

    private static string? ReadRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Request-Id", out var requestIds)
            ? requestIds.FirstOrDefault()
            : null;

    private enum RequestAuthenticationMode
    {
        Authenticated,
        Anonymous
    }
}
