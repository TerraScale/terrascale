using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace TerraScale.Datastar.DependencyInjection;

public interface IDatastarService
{
    Task StartServerEventStreamAsync();
    Task StartServerEventStreamAsync(CancellationToken cancellationToken);
    Task StartServerEventStreamAsync(IEnumerable<KeyValuePair<string, string>> additionalHeaders, CancellationToken cancellationToken = default);
    Task StartServerEventStreamAsync(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> additionalHeaders, CancellationToken cancellationToken = default);
    Task PatchElementsAsync(string elements, PatchElementsOptions options);
    Task PatchElementsAsync(string elements, CancellationToken cancellationToken = default);
    Task PatchElementsAsync(string elements, PatchElementsOptions options, CancellationToken cancellationToken = default);
    Task RemoveElementAsync(string selector, CancellationToken cancellationToken = default);
    Task RemoveElementAsync(string selector, RemoveElementOptions options);
    Task RemoveElementAsync(string selector, RemoveElementOptions options, CancellationToken cancellationToken = default);
    Task PatchSignalsAsync<T>(T signals, CancellationToken cancellationToken = default);
    Task PatchSignalsAsync<T>(T signals, JsonSerializerOptions options, CancellationToken cancellationToken = default);
    Task PatchSignalsAsync<T>(T signals, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default);
    Task PatchSignalsAsync<T>(T signals, JsonTypeInfo<T> typeInfo, PatchSignalsOptions patchOptions, CancellationToken cancellationToken = default);
    Task PatchSignalsAsync<T>(T signals, PatchSignalsOptions patchOptions, CancellationToken cancellationToken = default);
    Task PatchSignalsAsync<T>(T signals, JsonSerializerOptions options, PatchSignalsOptions patchOptions, CancellationToken cancellationToken = default);
    Task ExecuteScriptAsync(string script, CancellationToken cancellationToken = default);
    Task ExecuteScriptAsync(string script, ExecuteScriptOptions options, CancellationToken cancellationToken = default);
    Stream GetSignalsStream();
    Task<string?> ReadSignalsAsync(CancellationToken cancellationToken = default);
    Task<T?> ReadSignalsAsync<T>(CancellationToken cancellationToken = default);
    Task<T?> ReadSignalsAsync<T>(JsonSerializerOptions options, CancellationToken cancellationToken = default);
    Task<T?> ReadSignalsAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default);
}

internal sealed class DatastarService(
    IHttpContextAccessor contextAccessor,
    IOptions<DatastarJsonOptions> jsonOptions) : IDatastarService
{
    private HttpContext Context => contextAccessor.HttpContext ?? throw new InvalidOperationException("No active HTTP context.");
    private HttpResponse Response => Context.Response;
    private CancellationToken RequestAborted => Context.RequestAborted;
    private JsonSerializerOptions SignalOptions => jsonOptions.Value.SignalsJsonSerializerOptions;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private bool started;

    public Task StartServerEventStreamAsync() => StartServerEventStreamAsync(RequestAborted);

    public async Task StartServerEventStreamAsync(CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (started) return;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            if (Context.Request.Protocol == HttpProtocol.Http11)
                Response.Headers.Connection = "keep-alive";
            await Response.StartAsync(cancellationToken).ConfigureAwait(false);
            await Response.BodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            started = true;
        }
        finally { writeLock.Release(); }
    }

    public Task StartServerEventStreamAsync(IEnumerable<KeyValuePair<string, string>> additionalHeaders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(additionalHeaders);
        return StartServerEventStreamAsync(additionalHeaders.Select(pair => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(pair.Key, pair.Value)), cancellationToken);
    }

    public async Task StartServerEventStreamAsync(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> additionalHeaders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(additionalHeaders);
        foreach (var header in additionalHeaders) Response.Headers.TryAdd(header.Key, header.Value);
        await StartServerEventStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task PatchElementsAsync(string elements, CancellationToken cancellationToken = default) => PatchElementsAsync(elements, new PatchElementsOptions(), cancellationToken);
    public Task PatchElementsAsync(string elements, PatchElementsOptions options) => PatchElementsAsync(elements, options, RequestAborted);
    public Task PatchElementsAsync(string elements, PatchElementsOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elements); ArgumentNullException.ThrowIfNull(options);
        var lines = new List<string>();
        if (options.Selector is not null) lines.Add($"selector {options.Selector}");
        if (options.PatchMode != Consts.DefaultElementPatchMode) lines.Add($"mode {Consts.EnumToString(options.PatchMode)}");
        if (options.UseViewTransition)
        {
            lines.Add("useViewTransition true");
            if (options.ViewTransitionSelector is not null) lines.Add($"viewTransitionSelector {options.ViewTransitionSelector}");
        }
        if (options.Namespace != Consts.DefaultPatchElementNamespace) lines.Add($"namespace {Consts.EnumToString(options.Namespace)}");
        lines.AddRange(DataLines("elements", elements));
        return SendEventAsync(Consts.EnumToString(EventType.PatchElements), lines, options.EventId, options.Retry, cancellationToken);
    }

    public Task RemoveElementAsync(string selector, CancellationToken cancellationToken = default) => RemoveElementAsync(selector, new RemoveElementOptions(), cancellationToken);
    public Task RemoveElementAsync(string selector, RemoveElementOptions options) => RemoveElementAsync(selector, options, RequestAborted);
    public Task RemoveElementAsync(string selector, RemoveElementOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(options);
        var lines = new List<string> { $"selector {selector}", "mode remove" };
        if (options.UseViewTransition) lines.Add("useViewTransition true");
        return SendEventAsync(Consts.EnumToString(EventType.PatchElements), lines, options.EventId, options.Retry, cancellationToken);
    }

    public Task PatchSignalsAsync<T>(T signals, CancellationToken cancellationToken = default) => PatchSignalsAsync(signals, SignalOptions, new PatchSignalsOptions(), cancellationToken);
    public Task PatchSignalsAsync<T>(T signals, JsonSerializerOptions options, CancellationToken cancellationToken = default) => PatchSignalsAsync(signals, options, new PatchSignalsOptions(), cancellationToken);
    public Task PatchSignalsAsync<T>(T signals, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) => PatchSignalsAsync(signals, typeInfo, new PatchSignalsOptions(), cancellationToken);
    public Task PatchSignalsAsync<T>(T signals, PatchSignalsOptions patchOptions, CancellationToken cancellationToken = default) => PatchSignalsAsync(signals, SignalOptions, patchOptions, cancellationToken);
    public Task PatchSignalsAsync<T>(T signals, JsonSerializerOptions options, PatchSignalsOptions patchOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(patchOptions);
        var json = JsonSerializer.SerializeToUtf8Bytes(signals, options);
        return SendSignalEventAsync(json, patchOptions, cancellationToken);
    }

    public Task PatchSignalsAsync<T>(T signals, JsonTypeInfo<T> typeInfo, PatchSignalsOptions patchOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo); ArgumentNullException.ThrowIfNull(patchOptions);
        var json = JsonSerializer.SerializeToUtf8Bytes(signals, typeInfo);
        return SendSignalEventAsync(json, patchOptions, cancellationToken);
    }

    public Task ExecuteScriptAsync(string script, CancellationToken cancellationToken = default) => ExecuteScriptAsync(script, new ExecuteScriptOptions(), cancellationToken);
    public Task ExecuteScriptAsync(string script, ExecuteScriptOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script); ArgumentNullException.ThrowIfNull(options);
        var attributes = string.Join(' ', options.Attributes.Select(a => $"{a.Key}=\"{HtmlEncoder.Default.Encode(a.Value)}\""));
        var open = $"<script{(options.AutoRemove ? " data-effect=\"el.remove()\"" : string.Empty)}{(attributes.Length == 0 ? string.Empty : " " + attributes)}>";
        var lines = new List<string> { "selector body", "mode append" };
        lines.AddRange(DataLines("elements", open + script + "</script>"));
        return SendEventAsync(Consts.EnumToString(EventType.PatchElements), lines, options.EventId, options.Retry, cancellationToken);
    }

    private async Task SendEventAsync(string type, IEnumerable<string> lines, string? eventId, TimeSpan retry, CancellationToken cancellationToken)
    {
        await StartServerEventStreamAsync(cancellationToken).ConfigureAwait(false);
        ValidateSingleLine(type, nameof(type));
        if (eventId is not null) ValidateSingleLine(eventId, nameof(eventId));
        if (retry < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retry));
        var builder = new ArrayBufferWriter<byte>();
        WriteUtf8(builder, "event: "); WriteUtf8(builder, type); WriteUtf8(builder, "\n");
        if (eventId is not null) { WriteUtf8(builder, "id: "); WriteUtf8(builder, eventId); WriteUtf8(builder, "\n"); }
        if (retry != Consts.DefaultSseRetryDuration) { WriteUtf8(builder, "retry: "); WriteUtf8(builder, ((long)retry.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)); WriteUtf8(builder, "\n"); }
        foreach (var line in lines) { ValidateSingleLine(line, nameof(lines)); WriteUtf8(builder, "data: "); WriteUtf8(builder, line); WriteUtf8(builder, "\n"); }
        WriteUtf8(builder, "\n");
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Response.BodyWriter.WriteAsync(builder.WrittenMemory, cancellationToken).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    private async Task SendSignalEventAsync(
        ReadOnlyMemory<byte> json,
        PatchSignalsOptions options,
        CancellationToken cancellationToken)
    {
        await StartServerEventStreamAsync(cancellationToken).ConfigureAwait(false);
        var builder = new ArrayBufferWriter<byte>();
        WriteUtf8(builder, "event: "); WriteUtf8(builder, Consts.EnumToString(EventType.PatchSignals)); WriteUtf8(builder, "\n");
        if (options.EventId is not null) { ValidateSingleLine(options.EventId, nameof(options.EventId)); WriteUtf8(builder, "id: "); WriteUtf8(builder, options.EventId); WriteUtf8(builder, "\n"); }
        if (options.Retry < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.Retry));
        if (options.Retry != Consts.DefaultSseRetryDuration) { WriteUtf8(builder, "retry: "); WriteUtf8(builder, ((long)options.Retry.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)); WriteUtf8(builder, "\n"); }
        if (options.OnlyIfMissing)
        {
            WriteUtf8(builder, "data: onlyIfMissing true\n");
        }
        WriteDataLines(builder, "signals"u8, json.Span);
        WriteUtf8(builder, "\n");
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Response.BodyWriter.WriteAsync(builder.WrittenMemory, cancellationToken).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    public Stream GetSignalsStream()
    {
        var request = Context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
            return request.Query.TryGetValue(Consts.DatastarKey, out var value) ? new MemoryStream(Encoding.UTF8.GetBytes(value.ToString())) : Stream.Null;
        return request.Body;
    }

    public async Task<string?> ReadSignalsAsync(CancellationToken cancellationToken = default)
    {
        var request = Context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
            return request.Query.TryGetValue(Consts.DatastarKey, out var value) ? value.ToString() : "{}";
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ReadSignalsAsync<T>(CancellationToken cancellationToken = default) => await ReadSignalsAsync<T>(SignalOptions, cancellationToken).ConfigureAwait(false);
    public async Task<T?> ReadSignalsAsync<T>(JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        var json = await ReadSignalsAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, options);
    }

    public async Task<T?> ReadSignalsAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var json = await ReadSignalsAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize(json, typeInfo);
    }

    private static List<string> DataLines(string prefix, string value)
    {
        List<string> result = [];
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (true)
        {
            var index = remaining.IndexOfAny('\r', '\n');
            var line = index < 0 ? remaining : remaining[..index];
            result.Add(line.Length == 0 ? prefix : string.Concat(prefix, " ", line.ToString()));
            if (index < 0) return result;
            var separator = remaining[index];
            remaining = remaining[(index + 1)..];
            if (separator == '\r' && remaining.Length > 0 && remaining[0] == '\n') remaining = remaining[1..];
        }
    }

    private static void WriteUtf8(IBufferWriter<byte> destination, ReadOnlySpan<char> value)
    {
        var bytes = Encoding.UTF8.GetBytes(value, destination.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length)));
        destination.Advance(bytes);
    }

    private static void WriteDataLines(IBufferWriter<byte> destination, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> value)
    {
        while (true)
        {
            var index = value.IndexOfAny((byte)'\r', (byte)'\n');
            var line = index < 0 ? value : value[..index];
            WriteUtf8(destination, "data: ");
            WriteBytes(destination, prefix);
            if (line.Length > 0)
            {
                WriteUtf8(destination, " ");
                WriteBytes(destination, line);
            }
            WriteUtf8(destination, "\n");
            if (index < 0) return;
            var separator = value[index];
            value = value[(index + 1)..];
            if (separator == '\r' && value.Length > 0 && value[0] == '\n') value = value[1..];
        }
    }

    private static void WriteBytes(IBufferWriter<byte> destination, ReadOnlySpan<byte> value)
    {
        value.CopyTo(destination.GetSpan(value.Length));
        destination.Advance(value.Length);
    }

    private static void ValidateSingleLine(string value, string parameterName)
    {
        if (value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("The value must be a single line.", parameterName);
    }
}
