using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

internal sealed class TerraBaseConnection
{
    public const int DefaultMaxResponseBytes = 64 * 1024 * 1024;

    internal static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

    private const int MaxRetries = 2;
    private const int MinResponseBytes = 64 * 1024;
    private const int MaxResponseBytesLimit = 1024 * 1024 * 1024;
    private const int MaxSecretLength = 4096;
    internal static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient http;
    private readonly ITerraBaseEndpointResolver endpointResolver;
    private readonly AuthenticationHeaderValue? authorization;
    private readonly Func<CancellationToken, ValueTask<string>>? accessTokenProvider;
    private readonly Action<string>? rejectToken;
    private readonly bool anonymous;
    private readonly bool publicAnonymous;
    private readonly TimeSpan timeout;
    private readonly TimeProvider timeProvider;
    private readonly int maxResponseBytes;
    private readonly string? regionPin;

    internal TimeProvider TimeProvider => timeProvider;

    internal int MaxResponseBytes => maxResponseBytes;

    public TerraBaseConnection(HttpClient http, Uri? baseAddress, ITerraBaseEndpointResolver? endpointResolver, string? secret, string secretName, TimeSpan timeout, int maxResponseBytes,
        Func<CancellationToken, ValueTask<string>>? accessTokenProvider = null, bool anonymousAllowed = false, string? region = null, TimeProvider? timeProvider = null, bool publicAnonymous = false,
        Action<string>? rejectAccessToken = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.publicAnonymous = publicAnonymous;
        if (region is not null)
        {
            if (!TerraBaseRegion.IsValid(region))
            {
                throw new ArgumentException("Region must be a TerraBase region id: a lowercase letter, then lowercase letters, digits or hyphens, at most 63 characters, not ending in a hyphen (for example gru).", nameof(region));
            }

            baseAddress = TerraBaseRegion.OriginOrPublic(baseAddress, endpointResolver);
            regionPin = region;
        }

        if (baseAddress is not null && endpointResolver is not null)
        {
            throw new ArgumentException("Set either BaseAddress or EndpointResolver, not both.", nameof(endpointResolver));
        }

        this.endpointResolver = endpointResolver ?? new TerraBaseStaticEndpointResolver(Origin(baseAddress));
        if (secret is not null && accessTokenProvider is not null)
        {
            throw new ArgumentException($"Set either {secretName} or AccessTokenProvider, not both.", nameof(accessTokenProvider));
        }

        anonymous = anonymousAllowed && !publicAnonymous && secret is null && accessTokenProvider is null;
        if (accessTokenProvider is null && !anonymous && !publicAnonymous) ValidateSecret(secret, secretName);
        if (anonymous && baseAddress?.Scheme == Uri.UriSchemeHttps) ValidateSecret(secret, secretName);

        if (timeout <= TimeSpan.Zero || timeout > MaxRequestTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "RequestTimeout must be greater than zero and at most 10 minutes.");
        }

        if (maxResponseBytes is < MinResponseBytes or > MaxResponseBytesLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes), "MaxResponseBytes must be between 64 KiB and 1 GiB.");
        }

        authorization = secret is null ? null : new AuthenticationHeaderValue("Bearer", secret);
        this.accessTokenProvider = accessTokenProvider;
        rejectToken = rejectAccessToken;
        this.timeout = timeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maxResponseBytes = maxResponseBytes;
    }

    public static string Query(params (string Name, string? Value)[] parameters)
    {
        var set = new List<string>(parameters.Length);
        foreach (var (name, value) in parameters)
        {
            if (value is not null)
            {
                set.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }
        var query = string.Join('&', set);
        return query.Length == 0 ? string.Empty : "?" + query;
    }

    public static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    public static HttpContent Json<T>(T body, JsonTypeInfo<T> typeInfo, string mediaType = "application/json")
    {
        var content = new ByteArrayContent(TerraBaseRequestJson.Serialize(body, typeInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    public Task<(T Value, HttpStatusCode Status)> SendAsync<T>(
        HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> typeInfo, Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null, Action<HttpResponseMessage>? observe = null)
    {
        return SendAsync(method, path, content, headers, async (response, token) =>
        {
            observe?.Invoke(response);
            using var body = new BoundedHttpBodyStream(
                maxResponseBytes, () => new TerraBaseException(response.StatusCode, "TerraBase returned a response larger than the supported response bound.", "invalid_response", null), token);
            await response.Content.CopyToAsync(body, token).ConfigureAwait(false);
            return ReadResponse(body.Written.Span, typeInfo, response.StatusCode);
        }, cancellationToken, requestTimeout);
    }

    internal static T ReadResponse<T>(ReadOnlySpan<byte> body, JsonTypeInfo<T> typeInfo, HttpStatusCode status)
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException)
        {
            throw new TerraBaseException(status, "TerraBase returned an invalid response body.", "invalid_response", null);
        }

        return value ?? throw new TerraBaseException(status, "TerraBase returned an invalid response body.", "invalid_response", null);
    }

    public Task SendAsync(HttpMethod method, string path, HttpContent? content, Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken) =>
        SendAsync(method, path, content, headers, static (response, _) => Task.FromResult(response.StatusCode), cancellationToken);

    private async Task<(T Value, HttpStatusCode Status)> SendAsync<T>(
        HttpMethod method, string path, HttpContent? content, Action<HttpRequestHeaders>? headers,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken, TimeSpan? requestTimeout = null)
    {
        using var opened = await OpenAsync(method, path, content, headers, "application/json", streaming: false, cancellationToken,
            requestTimeout: requestTimeout).ConfigureAwait(false);
        try
        {
            try
            {
                return (await read(opened.Response, opened.Token).ConfigureAwait(false), opened.Response.StatusCode);
            }
            finally
            {
                opened.Token.ThrowIfCancellationRequested();
            }
        }
        catch (TerraBaseException error)
        {
            error.CaptureResponse(opened.Response);
            throw;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && opened.Token.IsCancellationRequested)
        {
            throw new TimeoutException("TerraBase did not answer within the request deadline.", error);
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal Task<T> ExchangeAsync<T>(string path, Uri? origin, Action<HttpRequestHeaders>? headers,
        Func<OpenResponse, Task<T>> read, CancellationToken cancellationToken) =>
        ExchangeAsync(HttpMethod.Get, path, null, origin, headers, read, cancellationToken);

    internal async Task<T> ExchangeAsync<T>(HttpMethod method, string path, HttpContent? content, Uri? origin,
        Action<HttpRequestHeaders>? headers, Func<OpenResponse, Task<T>> read, CancellationToken cancellationToken)
    {
        using var opened = await OpenAsync(method, path, content, headers, "application/json", streaming: false, cancellationToken, origin, throwOnError: false).ConfigureAwait(false);
        try
        {
            try
            {
                return await read(opened).ConfigureAwait(false);
            }
            finally
            {
                opened.Token.ThrowIfCancellationRequested();
            }
        }
        catch (TerraBaseException error)
        {
            error.CaptureResponse(opened.Response);
            throw;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && opened.Token.IsCancellationRequested)
        {
            throw new TimeoutException("TerraBase did not answer within the request deadline.", error);
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal async Task<Stream> OpenStreamAsync(string path, string accept, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(HttpMethod.Get, path, null, null, accept, streaming: true, cancellationToken).ConfigureAwait(false);
        try
        {
            var body = await opened.Response.Content.ReadAsStreamAsync(opened.Token).ConfigureAwait(false);
            return new OwnedResponseBodyStream(opened, body);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    internal async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var body = new BoundedHttpBodyStream(
            maxResponseBytes, () => new TerraBaseException(response.StatusCode, "TerraBase returned a response larger than the client's limit.", "invalid_response", null), cancellationToken);
        await response.Content.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
        return body.ToArray();
    }

    private async Task<OpenResponse> OpenAsync(HttpMethod method, string path, HttpContent? content,
        Action<HttpRequestHeaders>? headers, string accept, bool streaming, CancellationToken cancellationToken, Uri? pinnedOrigin = null, bool throwOnError = true,
        TimeSpan? requestTimeout = null)
    {
        var timeout = requestTimeout ?? this.timeout;
        var expiry = new CancellationTokenSource(timeout, timeProvider);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        var request = new HttpRequestMessage { Method = method, Content = content };
        HttpResponseMessage? response = null;
        try
        {
            request.Headers.Authorization = authorization;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            headers?.Invoke(request.Headers);
            if (regionPin is not null) request.Headers.Add(TerraBaseRegion.PinHeader, regionPin);
            var origin = pinnedOrigin ?? Origin(await endpointResolver.ResolveAsync(deadline.Token).ConfigureAwait(false));
            deadline.Token.ThrowIfCancellationRequested();
            if (anonymous && origin.Scheme == Uri.UriSchemeHttps)
                throw new InvalidOperationException("A client without a credential reaches only a private plain-HTTP TerraBase listener.");
            if (anonymous)
            {
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }
            else if (origin.Scheme == Uri.UriSchemeHttps)
            {
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }
            await AuthorizeAsync(request, deadline.Token).ConfigureAwait(false);
            request.RequestUri = new Uri(origin, path);
            if (publicAnonymous && (http.DefaultRequestHeaders.Contains("Authorization") || http.DefaultRequestHeaders.Contains("Cookie")))
                throw new InvalidOperationException("Anonymous TerraBase requests require an HttpClient without default Authorization or Cookie headers; use a separate credential-free HttpClient with TerraBaseHttp.CreateHandler().");
            var started = timeProvider.GetTimestamp();
            var read = method == HttpMethod.Get || method == HttpMethod.Head;
            var reauthorized = false;
            for (var attempt = 0; ; attempt++)
            {
                TerraBaseException? refusal = null;
                var reauthorize = false;
                ExceptionDispatchInfo? lost = null;
                try
                {
                    response = TerraScaleOrigin.Sent(await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false), origin);
                }
                catch (HttpRequestException error) when (read && attempt < MaxRetries && !deadline.IsCancellationRequested)
                {
                    lost = ExceptionDispatchInfo.Capture(error);
                }
                if (response is not null)
                {
                    if (!throwOnError || response.IsSuccessStatusCode) break;
                    refusal = await TerraBaseException.FromResponseAsync(response, null, deadline.Token).ConfigureAwait(false);
                    reauthorize = !reauthorized && request.Content is null or ByteArrayContent && Reject(request.Headers.Authorization?.Parameter, refusal);
                    reauthorized |= reauthorize;
                    if (!reauthorize && (attempt >= MaxRetries || !Retryable(method, request.Content, refusal))) throw refusal;
                }
                var delay = reauthorize ? TimeSpan.Zero : RetryDelay(refusal?.RetryAfter, attempt);
                if (delay >= timeout - timeProvider.GetElapsedTime(started))
                {
                    lost?.Throw();
                    throw (Exception?)refusal ?? new InvalidOperationException("A retry requires a failed request.");
                }
                var retry = new HttpRequestMessage(method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy, Content = request.Content };
                foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
                request.Content = null;
                response?.Dispose();
                response = null;
                request.Dispose();
                request = retry;
                await Task.Delay(delay, timeProvider, deadline.Token).ConfigureAwait(false);
                await AuthorizeAsync(request, deadline.Token).ConfigureAwait(false);
            }
            if (streaming) expiry.CancelAfter(Timeout.InfiniteTimeSpan);
            deadline.Token.ThrowIfCancellationRequested();
            return new OpenResponse(request, response, deadline, expiry, streaming ? cancellationToken : deadline.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            response?.Dispose();
            request.Dispose();
            deadline.Dispose();
            expiry.Dispose();
            throw new TimeoutException($"TerraBase did not answer within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.", error);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && error.InnerException is TimeoutException)
        {
            response?.Dispose();
            request.Dispose();
            deadline.Dispose();
            expiry.Dispose();
            throw new TimeoutException("TerraBase did not answer within the caller's HttpClient.Timeout.", error);
        }
        catch (Exception error)
        {
            var expired = deadline.IsCancellationRequested;
            response?.Dispose();
            request.Dispose();
            deadline.Dispose();
            expiry.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            if (expired) throw new TimeoutException("TerraBase did not answer within the request deadline.", error);
            throw;
        }
    }

    internal async Task<ClientWebSocket> ConnectWebSocketAsync(string path, string subprotocol, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? requestHeaders = null, TimeSpan? keepAliveTimeout = null)
    {
        using var expiry = new CancellationTokenSource(timeout, timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        var started = timeProvider.GetTimestamp();
        var socket = new ClientWebSocket();
        var reauthorized = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var origin = Origin(await endpointResolver.ResolveAsync(deadline.Token).ConfigureAwait(false));
                if (anonymous && origin.Scheme == Uri.UriSchemeHttps)
                    throw new InvalidOperationException("A client without a credential reaches only a private plain-HTTP TerraBase listener.");
                socket.Options.CollectHttpResponseDetails = true;
                socket.Options.AddSubProtocol(subprotocol);
                if (keepAliveTimeout is { } pongDeadline)
                {
                    socket.Options.KeepAliveInterval = pongDeadline / 2;
                    socket.Options.KeepAliveTimeout = pongDeadline;
                }
                if (anonymous)
                {
                    socket.Options.HttpVersion = HttpVersion.Version20;
                    socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                }

                var bearer = authorization?.Parameter;
                if (accessTokenProvider is not null)
                {
                    bearer = await accessTokenProvider(deadline.Token).ConfigureAwait(false);
                    ValidateSecret(bearer, nameof(TerraBaseDatabaseClientOptions.AccessTokenProvider));
                }

                if (bearer is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
                if (regionPin is not null) socket.Options.SetRequestHeader(TerraBaseRegion.PinHeader, regionPin);
                foreach (var (name, value) in requestHeaders ?? FrozenDictionary<string, string>.Empty) socket.Options.SetRequestHeader(name, value);
                var target = new UriBuilder(new Uri(origin, path)) { Scheme = origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
                var upgrade = new SentFromOrigin(http, target);
                try
                {
                    using var invoker = new HttpMessageInvoker(upgrade);
                    await socket.ConnectAsync(target, invoker, deadline.Token).ConfigureAwait(false);
                }
                catch (WebSocketException) when (upgrade.Refusal is not null)
                {
                    var refusal = upgrade.Refusal;
                    socket.Dispose();
                    if (!reauthorized && Reject(bearer, refusal))
                    {
                        reauthorized = true;
                        socket = new ClientWebSocket();
                        continue;
                    }

                    await SqlConnectRetry.WaitAsync(refusal, attempt, timeout - timeProvider.GetElapsedTime(started), timeProvider, deadline.Token).ConfigureAwait(false);
                    socket = new ClientWebSocket();
                    continue;
                }

                return socket.SubProtocol == subprotocol
                    ? socket
                    : throw new TerraBaseException(HttpStatusCode.SwitchingProtocols, "TerraBase did not select the SQL WebSocket subprotocol.", "invalid_response", null);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && expiry.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException("TerraBase did not accept the WebSocket within the request deadline.", error);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessTokenProvider is null) return;
        var token = await accessTokenProvider(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSecret(token, nameof(TerraBaseDatabaseClientOptions.AccessTokenProvider));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private bool Reject(string? token, TerraBaseException refusal)
    {
        if (rejectToken is null || token is null || refusal is not TerraBaseAuthenticationException || refusal is TerraBaseKeyRevokedException or TerraBaseKeyExpiredException)
            return false;
        rejectToken(token);
        return true;
    }

    private static bool Retryable(HttpMethod method, HttpContent? content, TerraBaseException error)
    {
        var read = method == HttpMethod.Get || method == HttpMethod.Head;
        return !error.OutcomeUncertain && (int)(error.StatusCode ?? 0) switch
        {
            503 => error.ErrorCode switch
            {
                "auth_unavailable" or "storage_busy" or "capacity" => read,
                _ => false,
            },
            429 => read && error.ErrorCode == "tenant_busy",
            409 => error is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.SerializationConflict } && content is ByteArrayContent,
            502 or 504 => read,
            _ => false,
        };
    }

    private static TimeSpan RetryDelay(TimeSpan? retryAfter, int attempt) => retryAfter is { } delay
        ? delay + TimeSpan.FromMilliseconds(Random.Shared.Next(250))
        : TimeSpan.FromMilliseconds(1 + Random.Shared.Next(250 << attempt));

    internal sealed class OpenResponse(HttpRequestMessage request, HttpResponseMessage response, CancellationTokenSource deadline, CancellationTokenSource expiry,
        CancellationToken token) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public Uri RequestUri => request.RequestUri ?? throw new InvalidOperationException("The TerraBase request has no URI.");
        public CancellationToken Token { get; } = token;
        public void Dispose()
        {
            Response.Dispose();
            request.Dispose();
            deadline.Dispose();
            expiry.Dispose();
        }
    }

    private sealed class OwnedResponseBodyStream(OpenResponse opened, Stream body) : Stream
    {
        public override bool CanRead => body.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            body.ReadAsync(buffer, cancellationToken);

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            body.CopyToAsync(destination, bufferSize, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
                opened.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await body.DisposeAsync().ConfigureAwait(false);
            opened.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class SentFromOrigin(HttpClient http, Uri origin) : HttpMessageHandler
    {
        public TerraBaseException? Refusal { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = TerraScaleOrigin.Sent(await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false), origin);
            var upgraded = response.Version.Major >= 2
                ? response.StatusCode == HttpStatusCode.OK
                : response.StatusCode == HttpStatusCode.SwitchingProtocols;
            if (!upgraded) Refusal = await TerraBaseException.FromResponseAsync(response, null, cancellationToken).ConfigureAwait(false);
            return response;
        }
    }

    private static void ValidateSecret(string? secret, string secretName)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaxSecretLength || secret.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException($"{secretName} must be 1 to {MaxSecretLength} characters without whitespace or control characters.", secretName);
        }
    }

    internal static Uri Origin(Uri? baseAddress) => TerraScaleOrigin.Parse(baseAddress, "BaseAddress", nameof(baseAddress), TerraScaleService.Api);
}
