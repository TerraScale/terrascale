using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Entry point for TerraScale database and repository API operations.
/// </summary>
public sealed class TerraScaleClient
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

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
        options.Validate();

        this.httpClient = httpClient;
        this.options = options;
        jsonOptions = DefaultJsonOptions;

        this.httpClient.BaseAddress ??= options.BaseAddress;
        Databases = new TerraScaleDatabasesClient(this);
        Collections = new TerraScaleCollectionsClient(this);
        Repositories = new TerraScaleRepositoriesClient(this);
        Documents = new TerraScaleDocumentsClient(this);
        Tql = new TerraScaleTqlClient(this);
        Schemas = new TerraScaleSchemasClient(this);
        Changes = new TerraScaleChangesClient(this);
        Billing = new TerraScaleBillingClient(this);
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
    /// Organization billing, invoices, Stripe checkout, and Stripe portal operations.
    /// </summary>
    public TerraScaleBillingClient Billing { get; }

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

    internal async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var activity = StartActivity(method, requestUri);
        using var response = await SendWithRetriesAsync(
            method,
            requestUri,
            body,
            activity,
            cancellationToken).ConfigureAwait(false);

        SetResponseTags(activity, response);

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
            body: null,
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
            var data = new StringBuilder();

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
        object? body,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var requestId = CreateRequestId();
        var idempotencyKey = CreateIdempotencyKey(method);
        var attempt = 0;

        while (true)
        {
            using var request = await CreateRequestAsync(
                method,
                requestUri,
                body,
                requestId,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);

            try
            {
                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!ShouldRetryResponse(response, method, idempotencyKey, attempt))
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                AddRetryEvent(activity, attempt + 1, delay, $"HTTP {(int)response.StatusCode}");
                response.Dispose();
                await DelayRetryAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (CanRetry(method, idempotencyKey, attempt))
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
        object? body,
        string? requestId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, requestUri);
        try
        {
            ApplyRequestVersion(request);
            await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
            ApplyRequestHeaders(request, requestId, idempotencyKey);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: jsonOptions);
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

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
        return string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
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

        var bearerToken = options.BearerToken;
        if (string.IsNullOrWhiteSpace(bearerToken) && options.BearerTokenProvider is { } provider)
        {
            bearerToken = await provider(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new InvalidOperationException("The configured bearer token provider returned an empty bearer token.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
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
        int attempt) =>
        CanRetry(method, idempotencyKey, attempt) &&
        IsRetryableStatus(response.StatusCode);

    private bool CanRetry(HttpMethod method, string? idempotencyKey, int attempt) =>
        attempt < options.MaxRetryAttempts &&
        (IsSafeRetryMethod(method) || !string.IsNullOrWhiteSpace(idempotencyKey));

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
        activity.SetTag("server.address", "TerraScale.API");
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
}
