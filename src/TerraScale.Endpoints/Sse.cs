using System.Buffers;
using System.IO.Pipelines;

namespace TerraScale.Endpoints;

public static class Sse
{
    public static IResult Stream(Func<SseClient, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new SseResult(handler);
    }
}

public sealed class SseClient
{
    internal static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(1);

    private readonly PipeWriter writer;
    private readonly StringBuilder buffer = new();
    private readonly Encoder encoder = Encoding.UTF8.GetEncoder();

    internal SseClient(HttpResponse response) => writer = response.BodyWriter;

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

        var local = buffer;
        local.Clear();
        local.Append("event: ").Append(eventType).Append('\n');
        if (eventId is not null)
        {
            local.Append("id: ").Append(eventId).Append('\n');
        }

        if (retry is { } retryValue && retryValue != DefaultRetry)
        {
            local.Append("retry: ").Append((long)retryValue.TotalMilliseconds).Append('\n');
        }

        foreach (var line in dataLines)
        {
            if (ContainsLineBreak(line))
            {
                throw new ArgumentException("Each SSE data line must already be split; it cannot contain line breaks.", nameof(dataLines));
            }

            local.Append("data: ").Append(line).Append('\n');
        }

        local.Append('\n');

        foreach (var chunk in local.GetChunks())
        {
            encoder.Convert(chunk.Span, writer, flush: false, out _, out _);
        }

        encoder.Convert([], writer, flush: true, out _, out _);
        return FlushAsync(cancellationToken);
    }

    public Task CommentAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (ContainsLineBreak(text))
        {
            throw new ArgumentException("The SSE comment must be a single line.", nameof(text));
        }

        writer.Write(": "u8);
        Encoding.UTF8.GetBytes(text, writer);
        writer.Write("\n\n"u8);
        return FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken cancellationToken) =>
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

    private static bool ContainsLineBreak(string value) => value.Contains('\n') || value.Contains('\r');
}

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
