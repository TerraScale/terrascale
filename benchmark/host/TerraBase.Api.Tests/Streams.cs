// <copyright file="Streams.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>The SSE client half of the stream tests: reads server-sent events off a live response.</summary>
public sealed class SseReader : IAsyncDisposable
{
    private readonly HttpResponseMessage response;
    private readonly StreamReader reader;

    private SseReader(HttpResponseMessage response, StreamReader reader)
    {
        this.response = response;
        this.reader = reader;
    }

    /// <summary>Opens one SSE stream.</summary>
    /// <param name="client">The caller's client.</param>
    /// <param name="query">The stream query (collections, from).</param>
    /// <returns>The reader over the live response.</returns>
    public static async Task<SseReader> OpenAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/v1/streams{(query.Length > 0 ? $"?{query}" : string.Empty)}", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"the stream refused to open: {(int)response.StatusCode} {body}");
        }

        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return new SseReader(response, new StreamReader(stream, Encoding.UTF8));
    }

    /// <summary>Reads the next event, or <c>null</c> when the stream ended.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The event.</returns>
    public async Task<SseEvent?> ReadAsync(CancellationToken cancellationToken = default)
    {
        string? id = null;
        var name = "message";
        var data = new StringBuilder();
        string? line;
        while ((line = await this.reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (id is not null || data.Length > 0)
                {
                    return new SseEvent(id, name, JsonDocument.Parse(data.ToString()));
                }

                continue;
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line["id: ".Length..];
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.AppendLine(line["data: ".Length..]);
            }
        }

        return null;
    }

    /// <summary>Reads the next event of the given name, skipping the others.</summary>
    /// <param name="name">The event name.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The event.</returns>
    public async Task<SseEvent> ExpectAsync(string name, CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        while (await this.ReadAsync(linked.Token).ConfigureAwait(false) is { } next)
        {
            if (next.Event == name)
            {
                return next;
            }
        }

        throw new TimeoutException($"the stream ended before a '{name}' event");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        this.reader.Dispose();
        this.response.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>One server-sent event.</summary>
/// <param name="Id">The event id (the resume sequence).</param>
/// <param name="Event">The event name.</param>
/// <param name="Data">The parsed data document.</param>
public sealed record SseEvent(string? Id, string Event, JsonDocument Data);

/// <summary>The WebSocket client half of the stream tests.</summary>
public static class Ws
{
    /// <summary>Opens one WebSocket stream.</summary>
    /// <param name="baseAddress">The host base address.</param>
    /// <param name="principal">The backend principal.</param>
    /// <param name="tenant">The backend tenant.</param>
    /// <param name="query">The stream query (collections, from).</param>
    /// <returns>The opened socket.</returns>
    public static async Task<ClientWebSocket> OpenAsync(Uri baseAddress, string principal, string tenant, string query)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-TB-Principal", principal);
        socket.Options.SetRequestHeader("X-TB-Tenant", tenant);
        var builder = new UriBuilder(baseAddress)
        {
            Scheme = "ws",
            Path = "/v1/streams/ws",
            Query = query,
        };
        await socket.ConnectAsync(builder.Uri, CancellationToken.None).ConfigureAwait(false);
        return socket;
    }

    /// <summary>Reads the next JSON message, or <c>null</c> when the socket closed.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The parsed message.</returns>
    public static async Task<JsonDocument?> ReadAsync(ClientWebSocket socket, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var received = new List<byte>();
        while (true)
        {
            var chunk = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token).ConfigureAwait(false);
            if (chunk.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            received.AddRange(buffer[..chunk.Count]);
            if (chunk.EndOfMessage)
            {
                return JsonDocument.Parse(received.ToArray());
            }
        }
    }

    /// <summary>Reads the next message of the given kind, skipping the others.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="kind">The message kind.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The parsed message.</returns>
    public static async Task<JsonDocument> ExpectAsync(ClientWebSocket socket, string kind, CancellationToken cancellationToken = default)
    {
        while (await ReadAsync(socket, cancellationToken).ConfigureAwait(false) is { } message)
        {
            if (message.RootElement.GetProperty("kind").GetString() == kind)
            {
                return message;
            }
        }

        throw new TimeoutException($"the socket closed before a '{kind}' message");
    }
}
