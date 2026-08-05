using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cysharp.Text;

namespace TerraScale.DotNet;

/// <summary>
/// Entry point for TerraScale database and repository API operations.
/// </summary>
public sealed class TerraScaleClient
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient httpClient;
    private readonly TerraScaleClientOptions options;
    private readonly JsonSerializerOptions jsonOptions;

    /// <summary>
    /// Creates a TerraScale client over a caller-owned <see cref="HttpClient"/>.
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
        Collections = new TerraScaleCollectionsClient(this);
        Repositories = new TerraScaleRepositoriesClient(this);
        Documents = new TerraScaleDocumentsClient(this);
        Tql = new TerraScaleTqlClient(this);
        Schemas = new TerraScaleSchemasClient(this);
        Changes = new TerraScaleChangesClient(this);
        Billing = new TerraScaleBillingClient(this);
        Projects = new TerraScaleProjectsClient(this);
        Regions = new TerraScaleRegionsClient(this);
        ProjectDatabases = new TerraScaleProjectDatabasesClient(this);
        SavedQueries = new TerraScaleSavedQueriesClient(this);
        Queues = new TerraScaleQueuesClient(this);
        Environments = new TerraScaleEnvironmentsClient(this);
        ProjectUsage = new TerraScaleProjectUsageClient(this);
        ApiKeys = new TerraScaleApiKeysClient(this);
        M2MApplications = new TerraScaleM2MApplicationsClient(this);
        Notifications = new TerraScaleNotificationsClient(this);
        Backups = new TerraScaleBackupsClient(this);
        Sync = new TerraScaleSyncClient(this);
    }

    /// <summary>
    /// Database management operations.
    /// </summary>
    public TerraScaleDatabasesClient Databases { get; }

    /// <summary>
    /// Database collection operations.
    /// </summary>
    public TerraScaleCollectionsClient Collections { get; }

    /// <summary>
    /// Repository management operations.
    /// </summary>
    public TerraScaleRepositoriesClient Repositories { get; }

    /// <summary>
    /// Repository document operations.
    /// </summary>
    public TerraScaleDocumentsClient Documents { get; }

    /// <summary>
    /// TerraScale Query Language operations.
    /// </summary>
    public TerraScaleTqlClient Tql { get; }

    /// <summary>
    /// Repository schema operations.
    /// </summary>
    public TerraScaleSchemasClient Schemas { get; }

    /// <summary>
    /// Real-time repository change subscriptions.
    /// </summary>
    public TerraScaleChangesClient Changes { get; }

    /// <summary>
    /// Organization billing, invoices, checkout, and customer portal operations.
    /// </summary>
    public TerraScaleBillingClient Billing { get; }

    /// <summary>
    /// Project list and lookup operations.
    /// </summary>
    public TerraScaleProjectsClient Projects { get; }

    /// <summary>
    /// TerraScale region catalog operations.
    /// </summary>
    public TerraScaleRegionsClient Regions { get; }

    /// <summary>
    /// Project-scoped logical database operations.
    /// </summary>
    public TerraScaleProjectDatabasesClient ProjectDatabases { get; }

    /// <summary>
    /// Project saved-query operations.
    /// </summary>
    public TerraScaleSavedQueriesClient SavedQueries { get; }

    /// <summary>
    /// Project-scoped queue and topic operations.
    /// </summary>
    public TerraScaleQueuesClient Queues { get; }

    /// <summary>
    /// Project database environment operations.
    /// </summary>
    public TerraScaleEnvironmentsClient Environments { get; }

    /// <summary>
    /// Project usage operations.
    /// </summary>
    public TerraScaleProjectUsageClient ProjectUsage { get; }

    /// <summary>
    /// Project auth API key management operations.
    /// </summary>
    public TerraScaleApiKeysClient ApiKeys { get; }

    /// <summary>
    /// Project machine-to-machine application operations.
    /// </summary>
    public TerraScaleM2MApplicationsClient M2MApplications { get; }

    /// <summary>
    /// Dashboard notification operations for the signed-in user.
    /// </summary>
    public TerraScaleNotificationsClient Notifications { get; }

    /// <summary>
    /// Project database backup, restore point, and restore operations.
    /// </summary>
    public TerraScaleBackupsClient Backups { get; }

    /// <summary>
    /// TerraScale Sync live-query and mutation sessions.
    /// </summary>
    public TerraScaleSyncClient Sync { get; }

    /// <summary>
    /// Creates a typed repository facade for document CRUD, list, and query operations.
    /// </summary>
    public TerraScaleTypedRepository<TEntity> Repository<TEntity>(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        return new TerraScaleTypedRepository<TEntity>(this, repository.Trim());
    }

    /// <summary>
    /// Creates a client context that targets a default database for repository creation.
    /// </summary>
    public TerraScaleClient ForDatabase(string databaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return new TerraScaleClient(
            httpClient,
            options with
            {
                DatabaseId = databaseId.Trim(),
                EnvironmentId = null
            });
    }

    /// <summary>
    /// Creates a client context that targets a default database environment for repository creation.
    /// </summary>
    public TerraScaleClient ForEnvironment(string databaseId, string environmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        return new TerraScaleClient(
            httpClient,
            options with
            {
                DatabaseId = databaseId.Trim(),
                EnvironmentId = environmentId.Trim()
            });
    }

    internal string? DatabaseId => options.DatabaseId;

    internal string? EnvironmentId => options.EnvironmentId;

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, requestUri, body, configureRequest: null, cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        Action<HttpRequestMessage>? configureRequest,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, requestUri, body, preferredRegion: null, configureRequest, cancellationToken);

    internal Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        string? preferredRegion,
        CancellationToken cancellationToken) =>
        SendAsync<TResponse>(method, requestUri, body, preferredRegion, configureRequest: null, cancellationToken);

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
            idempotencyKeyOverride: null,
            allowUnkeyedPutRetry: false);

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

    private async Task<TResult> SendAsync<TResponse, TResult>(
        HttpMethod method,
        string requestUri,
        object? body,
        string? preferredRegion,
        Action<HttpRequestMessage>? configureRequest,
        Func<TResponse, HttpResponseMessage, TResult> responseFactory,
        CancellationToken cancellationToken,
        string? idempotencyKeyOverride,
        bool allowUnkeyedPutRetry = true)
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
        var payload = await ReadJsonResponseAsync<TResponse>(response, activity, cancellationToken).ConfigureAwait(false);
        return responseFactory(payload, response);
    }

    private static void ApplyPreferredRegion(HttpRequestMessage request, string preferredRegion)
    {
        if (!string.IsNullOrWhiteSpace(preferredRegion))
        {
            request.Headers.TryAddWithoutValidation(
                "X-TerraScale-Preferred-Region",
                preferredRegion.Trim().ToLowerInvariant());
        }
    }

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
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
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

    internal async Task SendAsync(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            CreateJsonContentFactory(body: null),
            RequestAuthenticationMode.Authenticated,
            configureRequest: null,
            activity,
            cancellationToken).ConfigureAwait(false);

        SetResponseTags(activity, response);

        if (!response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    internal IAsyncEnumerable<TEvent> StreamServerSentEventsAsync<TEvent>(
        string requestUri,
        string eventName,
        CancellationToken cancellationToken) =>
        new GuardedServerSentEventEnumerable<TEvent>(
            ReadServerSentEventsAsync<TEvent>(requestUri, eventName, cancellationToken));

    private async IAsyncEnumerable<TEvent> ReadServerSentEventsAsync<TEvent>(
        string requestUri,
        string eventName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = StartActivity(HttpMethod.Get, requestUri);
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        Stream? stream = null;
        StreamReader? reader = null;

        try
        {
            request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            ApplyRequestVersion(request);
            await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
            ApplyRequestHeaders(
                request,
                CreateRequestId(),
                idempotencyKey: null);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            SetResponseTags(activity, response);

            if (!response.IsSuccessStatusCode)
            {
                activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
                throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            var currentEvent = string.Empty;
            using var data = ZString.CreateStringBuilder();

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }
                catch (NotSupportedException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                if (line is null)
                {
                    yield break;
                }

                if (line.Length == 0)
                {
                    if (string.Equals(currentEvent, eventName, StringComparison.Ordinal) &&
                        data.Length > 0)
                    {
                        var payload = JsonSerializer.Deserialize<TEvent>(data.ToString(), jsonOptions)
                            ?? throw new InvalidOperationException($"TerraScale SSE event '{eventName}' contained an empty payload.");
                        yield return payload;
                    }

                    currentEvent = string.Empty;
                    data.Clear();
                    continue;
                }

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    currentEvent = line["event: ".Length..].Trim();
                    continue;
                }

                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(line["data: ".Length..]);
                }
            }
        }
        finally
        {
            DisposeIgnoringNotSupported(reader);
            DisposeIgnoringNotSupported(stream);
            DisposeIgnoringNotSupported(response);
            DisposeIgnoringNotSupported(request);
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
        var requestId = CreateRequestId();
        var idempotencyKey = idempotencyKeyOverride ?? CreateIdempotencyKey(method);
        var attempt = 0;

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
                cancellationToken).ConfigureAwait(false);

            try
            {
                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!ShouldRetryResponse(response, method, idempotencyKey, attempt, allowUnkeyedPutRetry))
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                AddRetryEvent(activity, attempt + 1, delay, $"HTTP {(int)response.StatusCode}");
                response.Dispose();
                await DelayRetryAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (CanRetry(method, idempotencyKey, attempt, allowUnkeyedPutRetry))
            {
                var delay = GetRetryDelay(response: null, attempt);
                AddRetryEvent(activity, attempt + 1, delay, exception.GetType().Name);
                await DelayRetryAsync(delay, cancellationToken).ConfigureAwait(false);
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
            ApplyRequestVersion(request);
            if (authenticationMode == RequestAuthenticationMode.Authenticated)
            {
                await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
            }

            ApplyRequestHeaders(request, requestId, idempotencyKey);

            var content = contentFactory();
            if (content is not null)
            {
                request.Content = content;
            }

            configureRequest?.Invoke(request);
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private Func<HttpContent?> CreateJsonContentFactory(object? body) =>
        body is null
            ? static () => null
            : () => JsonContent.Create(body, options: jsonOptions);

    private sealed class GuardedServerSentEventEnumerable<TEvent>(
        IAsyncEnumerable<TEvent> inner) : IAsyncEnumerable<TEvent>
    {
        public IAsyncEnumerator<TEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new GuardedServerSentEventEnumerator<TEvent>(inner.GetAsyncEnumerator(cancellationToken));
    }

    private sealed class GuardedServerSentEventEnumerator<TEvent>(
        IAsyncEnumerator<TEvent> inner) : IAsyncEnumerator<TEvent>
    {
        private bool hasReadEvent;

        public TEvent Current => inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            try
            {
                var moved = await inner.MoveNextAsync().ConfigureAwait(false);
                hasReadEvent |= moved;
                return moved;
            }
            catch (NotSupportedException) when (hasReadEvent)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
            }
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
        if (normalized.Length > 128)
        {
            throw new InvalidOperationException("IdempotencyKeyFactory must return at most 128 characters.");
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
            request.Headers.TryAddWithoutValidation("X-TerraScale-Auth-Key", options.ApiKey.Trim());
            request.Headers.TryAddWithoutValidation("X-TerraScale-Project-Id", options.ProjectId?.Trim());
            return;
        }

        var bearerToken = await ResolveBearerTokenAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new InvalidOperationException("Configure an API key, bearer token, or bearer token provider for authenticated TerraScale API calls.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
    }

    internal async ValueTask<string?> ConfigureSyncWebSocketAsync(
        ClientWebSocketOptions webSocketOptions,
        TerraScaleSyncId databaseId,
        TerraScaleSyncId environmentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webSocketOptions);
        webSocketOptions.SetRequestHeader("X-TerraScale-Database-Id", databaseId.Value);
        webSocketOptions.SetRequestHeader("X-TerraScale-Environment-Id", environmentId.Value);
        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            webSocketOptions.SetRequestHeader("X-TerraScale-Project-Id", options.ProjectId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            webSocketOptions.SetRequestHeader("X-TerraScale-Auth-Key", options.ApiKey.Trim());
            return null;
        }

        var bearerToken = await ResolveBearerTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new InvalidOperationException(
                "Configure an API key, bearer token, or bearer token provider for TerraScale Sync.");
        }

        var normalized = bearerToken.Trim();
        webSocketOptions.SetRequestHeader("Authorization", $"Bearer {normalized}");
        return normalized;
    }

    internal async ValueTask<string> ResolveSyncBearerTokenAsync(
        CancellationToken cancellationToken)
    {
        var bearerToken = await ResolveBearerTokenAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(bearerToken)
            ? bearerToken.Trim()
            : throw new InvalidOperationException(
                "Authentication refresh requires a bearer token or bearer-token provider.");
    }

    internal bool CanRefreshSyncBearerToken => options.BearerTokenProvider is not null;

    internal bool HasSyncAuthentication =>
        !string.IsNullOrWhiteSpace(options.ApiKey) ||
        !string.IsNullOrWhiteSpace(options.BearerToken) ||
        options.BearerTokenProvider is not null;

    internal Uri SyncEndpoint
    {
        get
        {
            var builder = new UriBuilder(options.BaseAddress)
            {
                Scheme = options.BaseAddress.Scheme switch
                {
                    "https" => "wss",
                    "http" => "ws",
                    _ => throw new InvalidOperationException(
                        "TerraScale Sync requires an HTTP or HTTPS base address.")
                },
                Path = TerraScaleApiRoutes.Sync,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri;
        }
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
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

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
        activity.SetTag("server.address", "TerraScale.Platform");
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

    private static void DisposeIgnoringNotSupported(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (NotSupportedException)
        {
        }
    }

    private enum RequestAuthenticationMode
    {
        Authenticated,
        Anonymous
    }
}
