using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// Sends TerraBase requests over a caller-owned <see cref="HttpClient"/> with one bearer secret and a per-request
/// deadline. TerraBase documents no retryable failure and rejects <c>Idempotency-Key</c>, so every request is sent once.
/// </summary>
internal sealed class TerraBaseConnection
{
    /// <summary>
    /// Default bound of one response body. A page holds up to 1 MiB + 8 MiB of stored JSON, and TerraBase's default
    /// encoder escapes characters such as <c>&lt;</c> (6 bytes) or CJK (6 bytes for 3) on the way out.
    /// </summary>
    public const int DefaultMaxResponseBytes = 64 * 1024 * 1024;

    private const int MinResponseBytes = 64 * 1024;
    private const int MaxResponseBytesLimit = 1024 * 1024 * 1024;
    private const int MaxSecretLength = 4096;
    private static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly IPNetwork NetBirdNetwork = IPNetwork.Parse("100.64.0.0/10");

    private readonly HttpClient http;
    private readonly ITerraBaseEndpointResolver endpointResolver;
    private readonly AuthenticationHeaderValue? authorization;
    private readonly Func<CancellationToken, ValueTask<string>>? accessTokenProvider;
    private readonly bool anonymous;
    private readonly bool publicAnonymous;
    private readonly TimeSpan timeout;
    private readonly TimeProvider timeProvider;
    private readonly int maxResponseBytes;
    private readonly string? regionPin;

    internal TimeProvider TimeProvider => timeProvider;

    internal int MaxResponseBytes => maxResponseBytes;

    public TerraBaseConnection(HttpClient http, Uri? baseAddress, ITerraBaseEndpointResolver? endpointResolver, string? secret, string secretName, TimeSpan timeout, int maxResponseBytes,
        Func<CancellationToken, ValueTask<string>>? accessTokenProvider = null, bool anonymousAllowed = false, string? region = null, TimeProvider? timeProvider = null, bool publicAnonymous = false)
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

            // Without an explicit origin the public origin routes the pinned request; with one, the pin still holds the request to it.
            baseAddress ??= endpointResolver is null ? TerraBaseRegion.PublicOrigin : null;
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

        // Anonymous requests follow the pinned producer's plain-HTTP private-listener contract.
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
        this.timeout = timeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maxResponseBytes = maxResponseBytes;
    }

    /// <summary>Escapes one caller-supplied path segment.</summary>
    public static string Segment(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        return value is "." or ".."
            ? throw new ArgumentException($"{name} cannot be \".\" or \"..\".", name)
            : Uri.EscapeDataString(value);
    }

    /// <summary>Builds <c>?after=…&amp;limit=…</c>-style query strings from the parameters that are set.</summary>
    public static string Query(params (string Name, string? Value)[] parameters)
    {
        var set = parameters.Where(parameter => parameter.Value is not null).Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(parameter.Value!)}");
        var query = string.Join('&', set);
        return query.Length == 0 ? string.Empty : "?" + query;
    }

    public static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    public static HttpContent Json<T>(T body, JsonTypeInfo<T> typeInfo, string mediaType = "application/json")
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, typeInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    /// <summary>Sends a request and reads its JSON answer.</summary>
    public async Task<(T Value, HttpStatusCode Status)> SendAsync<T>(
        HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> typeInfo, Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken)
    {
        T? value = default;
        var status = await SendAsync(method, path, content, headers, async (response, token) =>
        {
            using var body = new BoundedHttpBodyStream(
                maxResponseBytes, () => new TerraBaseException(response.StatusCode, "TerraBase returned a response larger than MaxResponseBytes.", "invalid_response", null), token);
            await response.Content.CopyToAsync(body, token).ConfigureAwait(false);
            value = ReadResponse(body.Written.Span, typeInfo, response.StatusCode);
        }, cancellationToken).ConfigureAwait(false);
        return (value!, status);
    }

    internal static T ReadResponse<T>(ReadOnlySpan<byte> body, JsonTypeInfo<T> typeInfo, HttpStatusCode status)
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo)
                ?? throw new JsonException("The response cannot be null.");
        }
        catch (JsonException)
        {
            throw new TerraBaseException(status, "TerraBase returned an invalid response body.", "invalid_response", null);
        }
    }

    /// <summary>Sends a request whose success has no body (<c>204</c>).</summary>
    public Task SendAsync(HttpMethod method, string path, HttpContent? content, Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken) =>
        SendAsync(method, path, content, headers, static (_, _) => Task.CompletedTask, cancellationToken);

    private async Task<HttpStatusCode> SendAsync(
        HttpMethod method, string path, HttpContent? content, Action<HttpRequestHeaders>? headers,
        Func<HttpResponseMessage, CancellationToken, Task> read, CancellationToken cancellationToken)
    {
        using var opened = await OpenAsync(method, path, content, headers, "application/json", streaming: false, cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await read(opened.Response, opened.Token).ConfigureAwait(false);
            }
            finally
            {
                // Cancellation takes precedence even when synchronous response parsing fails.
                opened.Token.ThrowIfCancellationRequested();
            }
            return opened.Response.StatusCode;
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
            // Caller cancellation takes precedence over parsing failures.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal Task<OpenResponse> OpenChangesAsync(string path, Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken) =>
        OpenAsync(HttpMethod.Get, path, null, headers, "text/event-stream", streaming: true, cancellationToken);

    internal Task<OpenResponse> OpenExportAsync(string path, CancellationToken cancellationToken) =>
        OpenAsync(HttpMethod.Get, path, null, null, "application/x-ndjson", streaming: true, cancellationToken);

    /// <summary>
    /// Sends a GET and lets <paramref name="read"/> handle every status, including redirects and errors, within the
    /// request deadline. <paramref name="origin"/> pins the request to a previously validated origin instead of
    /// resolving one.
    /// </summary>
    internal Task<T> ExchangeAsync<T>(string path, Uri? origin, Action<HttpRequestHeaders>? headers,
        Func<OpenResponse, Task<T>> read, CancellationToken cancellationToken) =>
        ExchangeAsync(HttpMethod.Get, path, null, origin, headers, read, cancellationToken);

    /// <summary>Sends once and handles every status with the existing deadline, bounds and response ownership.</summary>
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

    /// <summary>Reads a response body bounded by the connection's response limit.</summary>
    internal async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var body = new BoundedHttpBodyStream(
            maxResponseBytes, () => new TerraBaseException(response.StatusCode, "TerraBase returned a response larger than the client's limit.", "invalid_response", null), cancellationToken);
        await response.Content.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
        return body.ToArray();
    }

    private async Task<OpenResponse> OpenAsync(HttpMethod method, string path, HttpContent? content,
        Action<HttpRequestHeaders>? headers, string accept, bool streaming, CancellationToken cancellationToken, Uri? pinnedOrigin = null, bool throwOnError = true)
    {
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
            if (accessTokenProvider is not null)
            {
                var token = await accessTokenProvider(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                ValidateSecret(token, nameof(TerraBaseDocumentsClientOptions.AccessTokenProvider));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            request.RequestUri = new Uri(origin, path);
            // Null request headers do not suppress HttpClient's inherited defaults.
            if (publicAnonymous && (http.DefaultRequestHeaders.Contains("Authorization") || http.DefaultRequestHeaders.Contains("Cookie")))
                throw new InvalidOperationException("Anonymous TerraBase requests require an HttpClient without default Authorization or Cookie headers; use a separate credential-free HttpClient with TerraBaseHttp.CreateHandler().");
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (throwOnError && !response.IsSuccessStatusCode)
                throw await TerraBaseException.FromResponseAsync(response, deadline.Token).ConfigureAwait(false);
            // Stream lifetime belongs to enumeration, not the ordinary JSON request timeout.
            if (streaming) expiry.CancelAfter(Timeout.InfiniteTimeSpan);
            deadline.Token.ThrowIfCancellationRequested();
            // An already-running expiry callback may still fire; acquired streams belong only to caller cancellation.
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

    internal sealed class OpenResponse(HttpRequestMessage request, HttpResponseMessage response, CancellationTokenSource deadline, CancellationTokenSource expiry,
        CancellationToken token) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public Uri RequestUri => request.RequestUri!;
        public CancellationToken Token { get; } = token;
        public void Dispose()
        {
            Response.Dispose();
            request.Dispose();
            deadline.Dispose();
            expiry.Dispose();
        }
    }

    private static void ValidateSecret(string? secret, string secretName)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaxSecretLength || secret.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException($"{secretName} must be 1 to {MaxSecretLength} characters without whitespace or control characters.", secretName);
        }
    }

    internal static Uri Origin(Uri? baseAddress)
    {
        if (baseAddress is not { IsAbsoluteUri: true } || !(baseAddress.Scheme == Uri.UriSchemeHttps || (baseAddress.Scheme == Uri.UriSchemeHttp && IsPrivate(baseAddress)))
            || baseAddress.UserInfo.Length != 0 || baseAddress.Query.Length != 0 || baseAddress.Fragment.Length != 0
            || baseAddress.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "BaseAddress must be the HTTPS origin of a TerraBase client listener (plain HTTP only for NetBird 100.64.0.0/10 addresses or loopback), without credentials, path, query or fragment.", nameof(baseAddress));
        }

        return baseAddress;
    }

    // DNS suffixes do not prove private routing; allow only NetBird address literals and loopback.
    private static bool IsPrivate(Uri address) =>
        address.IsLoopback || IPAddress.TryParse(address.IdnHost, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetwork && NetBirdNetwork.Contains(ip);
}
