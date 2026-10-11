using System.Buffers;
using System.IO.Pipelines;

namespace TerraScale.Endpoints;

/// <summary>Raw server-sent event stream entry points.</summary>
public static class Sse
{
    /// <summary>Returns a <c>text/event-stream</c> result that runs the handler for the request lifetime.</summary>
    /// <param name="handler">Writes events to the stream; completes when the stream should end.</param>
    public static IResult Stream(Func<SseClient, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new SseResult(handler);
    }
}

/// <summary>Writes server-sent events to the active HTTP response. Not safe for concurrent writes.</summary>
public class SseClient
{
    internal static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(1);

    private readonly PipeWriter writer;
    private readonly StringBuilder buffer = new();
    private readonly Encoder encoder = Encoding.UTF8.GetEncoder();

    internal SseClient(HttpResponse response)
    {
        this.writer = response.BodyWriter;
    }

    /// <summary>Sends one event using the standard event/id/retry/data lines and flushes it.</summary>
    /// <param name="eventType">The SSE event name.</param>
    /// <param name="dataLines">The payload lines, already split on line breaks.</param>
    /// <param name="eventId">The optional SSE id for replay.</param>
    /// <param name="retry">The optional reconnection delay; the 1000&nbsp;ms default is omitted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task SendEventAsync(
        string eventType,
        IEnumerable<string> dataLines,
        string? eventId = null,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(dataLines);
        if (ContainsLineBreak(eventType))
        {
            throw new ArgumentException("The SSE event type must be a single line.", nameof(eventType));
        }

        if (eventId is not null && ContainsLineBreak(eventId))
        {
            throw new ArgumentException("The SSE event id must be a single line.", nameof(eventId));
        }

        var buffer = this.buffer;
        buffer.Clear();
        buffer.Append("event: ").Append(eventType).Append('\n');
        if (eventId is not null)
        {
            buffer.Append("id: ").Append(eventId).Append('\n');
        }

        if (retry is { } retryValue && retryValue != DefaultRetry)
        {
            buffer.Append("retry: ").Append((long)retryValue.TotalMilliseconds).Append('\n');
        }

        foreach (var line in dataLines)
        {
            if (ContainsLineBreak(line))
            {
                throw new ArgumentException("Each SSE data line must already be split; it cannot contain line breaks.", nameof(dataLines));
            }

            buffer.Append("data: ").Append(line).Append('\n');
        }

        buffer.Append('\n');

        // Validation finished before anything reached the pipe, so a rejected event never leaves
        // a partial frame behind. The chunks are encoded straight into the response buffer.
        foreach (var chunk in buffer.GetChunks())
        {
            this.encoder.Convert(chunk.Span, this.writer, flush: false, out _, out _);
        }

        this.encoder.Convert([], this.writer, flush: true, out _, out _);
        return this.FlushAsync(cancellationToken);
    }

    /// <summary>Sends a comment line, useful for keepalive pings through proxies.</summary>
    public Task CommentAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (ContainsLineBreak(text))
        {
            throw new ArgumentException("The SSE comment must be a single line.", nameof(text));
        }

        this.writer.Write(": "u8);
        Encoding.UTF8.GetBytes(text, this.writer);
        this.writer.Write("\n\n"u8);
        return this.FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await this.writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool ContainsLineBreak(string value) => value.Contains('\n') || value.Contains('\r');
}

/// <summary>Executes a handler as a server-sent event stream response.</summary>
internal sealed class SseResult(Func<SseClient, CancellationToken, Task> handler) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        var cancellationToken = httpContext.RequestAborted;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        if (httpContext.Request.Protocol == HttpProtocol.Http11)
        {
            response.Headers.Connection = "keep-alive";
        }

        await response.StartAsync(cancellationToken).ConfigureAwait(false);
        var client = new SseClient(response);
        await handler(client, cancellationToken).ConfigureAwait(false);
    }
}
