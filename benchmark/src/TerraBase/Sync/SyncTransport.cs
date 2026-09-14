using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TerraBase.Sync;

public sealed record SyncTransportOptions
{
    public Uri BaseUri { get; init; }
    public string? BearerToken { get; init; }

    public SyncTransportOptions(Uri baseUri, string? bearerToken = null)
    {
        if (baseUri is null || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sync transport requires an HTTPS base URI.", nameof(baseUri));
        BaseUri = baseUri;
        BearerToken = bearerToken;
    }
}

public sealed record SseEvent(string? Id, string? Event, string Data);

/// <summary>HTTPS, WSS and SSE transport for the public sync engine.</summary>
public sealed class SyncTransport : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly SyncTransportOptions options;

    public SyncTransport(SyncTransportOptions options, HttpClient? httpClient = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        http = httpClient ?? new HttpClient();
        ownsClient = httpClient is null;
    }

    public async Task<JsonDocument> GetSnapshotAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SseEvent> ReadSseAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path, "text/event-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? id = null, eventName = null;
        var data = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (!string.IsNullOrEmpty(line))
            {
                if (line.StartsWith("id:", StringComparison.Ordinal)) id = line[3..].TrimStart();
                else if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].TrimStart();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); }
                continue;
            }
            if (data.Length > 0) { yield return new SseEvent(id, eventName, data.ToString()); data.Clear(); eventName = null; }
        }
        if (data.Length > 0) yield return new SseEvent(id, eventName, data.ToString());
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(options.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WebSocket sync transport requires an HTTPS base URI so it can upgrade to WSS.");
        var uri = new Uri(options.BaseUri, path);
        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeWss, Port = uri.IsDefaultPort ? -1 : uri.Port };
        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(options.BearerToken)) socket.Options.SetRequestHeader("Authorization", $"Bearer {options.BearerToken}");
        await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        return socket;
    }

    public ValueTask DisposeAsync()
    {
        if (ownsClient) http.Dispose();
        return ValueTask.CompletedTask;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string accept)
    {
        if (!string.Equals(options.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sync transport requires an HTTPS base URI.");
        var request = new HttpRequestMessage(method, new Uri(options.BaseUri, path));
        request.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrWhiteSpace(options.BearerToken)) request.Headers.Authorization = new("Bearer", options.BearerToken);
        return request;
    }
}
