using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TerraScale.DotNet.TerraBase;

public sealed partial class LiveShape
{
    private readonly TerraBaseConnection connection;
    private readonly string path;
    private readonly string? where;
    private readonly string? columns;
    private const string Protocol = "terrascale.shape.v1";
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(30);

    internal LiveShape(TerraBaseConnection connection, string databasePath, string table, ShapeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (!TableName().IsMatch(table)) throw new ArgumentException("A table name must be 1 to 63 ASCII identifier characters, starting with a letter or underscore.", nameof(table));
        if (options.Where is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.Where, nameof(options));
        if (options.Columns is { } names && (names.Count == 0 || names.Any(name => name is null || !ColumnName().IsMatch(name)) || names.Distinct(StringComparer.Ordinal).Count() != names.Count))
            throw new ArgumentException("Columns must be non-empty, distinct SQL column names.", nameof(options));
        this.connection = connection;
        path = databasePath + "/tables/" + table + "/shape";
        where = options.Where;
        columns = options.Columns is null ? null : string.Join(',', options.Columns);
    }

    public async IAsyncEnumerable<ShapeView> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queue = Channel.CreateBounded<ShapeView>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var producer = ProduceAsync(queue.Writer, lifetime.Token);
        try
        {
            await foreach (var view in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return view;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(ChannelWriter<ShapeView> writer, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(writer, cancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception error)
        {
            writer.TryComplete(error);
        }
    }

    private async Task RunAsync(ChannelWriter<ShapeView> writer, CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, TerraBaseSqlRecord>(StringComparer.Ordinal);
        var pending = new List<ShapePending>();
        string? handle = null, cursor = null;
        var offset = "-1";
        var local = "0";
        var live = false;
        var useHints = false;
        var retryHintsAt = DateTimeOffset.MinValue;
        var attempt = 0;
        ClientWebSocket? socket = null;
        ShapeView View(ShapeStatus status)
        {
            if (offset.StartsWith("snap.", StringComparison.Ordinal)) return new([], offset, status);
            var merged = new Dictionary<string, TerraBaseSqlRecord>(rows, StringComparer.Ordinal);
            foreach (var transaction in pending) Apply(merged, transaction.Ops);
            return new(Array.AsReadOnly(merged.Values.ToArray()), offset, status);
        }
        await writer.WriteAsync(View(ShapeStatus.Loading), cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var poll = live;
                    if (live && useHints && connection.TimeProvider.GetUtcNow() >= retryHintsAt)
                    {
                        try
                        {
                            if (socket is null)
                            {
                                socket = await connection.ConnectWebSocketAsync(path + "/ws" + Query(handle, offset, false, null, local), Protocol, cancellationToken).ConfigureAwait(false);
                                await ReadyAsync(socket, cancellationToken).ConfigureAwait(false);
                            }
                            while (true)
                            {
                                var hint = await HintAsync(socket, cancellationToken).ConfigureAwait(false);
                                if (hint.GetProperty("handle").GetString() != handle) throw new TerraBaseConflictException(HttpStatusCode.Conflict, "Shape lineage changed.", "must_refetch", null);
                                var type = hint.GetProperty("type").GetString();
                                if (type == "provisional" && hint.GetProperty("offset").GetString() == offset && hint.GetProperty("local").GetString() != local) break;
                                if (type == "new_data")
                                {
                                    var next = hint.GetProperty("next_offset").GetString();
                                    if (!long.TryParse(next, CultureInfo.InvariantCulture, out var nextOffset)) throw InvalidResponse();
                                    if (nextOffset > long.Parse(offset, CultureInfo.InvariantCulture))
                                    {
                                        poll = false;
                                        break;
                                    }
                                }
                                if (type is not ("provisional" or "new_data")) throw InvalidResponse();
                            }
                        }
                        catch (Exception error) when (error is TerraBaseException refusal
                            ? refusal.ErrorCode != "must_refetch" && !Terminal(refusal) : Transient(error))
                        {
                            socket?.Dispose();
                            socket = null;
                            retryHintsAt = connection.TimeProvider.GetUtcNow() + Pause(error, attempt++);
                            await writer.WriteAsync(View(ShapeStatus.Reconnecting), cancellationToken).ConfigureAwait(false);
                        }
                    }
                    var response = await connection.ExchangeAsync(path + Query(handle, offset, poll, cursor, local), null, null, async opened =>
                    {
                        var message = opened.Response;
                        if (message.StatusCode == HttpStatusCode.TemporaryRedirect)
                        {
                            var original = opened.RequestUri;
                            var location = message.Headers.Location ?? throw InvalidResponse();
                            var target = new Uri(original, location);
                            if (handle is not null || target.GetLeftPart(UriPartial.Authority) != original.GetLeftPart(UriPartial.Authority) || target.AbsolutePath != original.AbsolutePath)
                                throw InvalidResponse();
                            var query = target.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.Ordinal);
                            handle = query["handle"];
                            offset = query["offset"];
                            if (!Handle().IsMatch(handle) || !SnapshotOffset().IsMatch(offset)) throw InvalidResponse();
                            return (Page: (ShapePage?)null, Cursor: (string?)null, Hints: false);
                        }
                        if (!message.IsSuccessStatusCode) throw await TerraBaseException.FromResponseAsync(message, null, opened.Token).ConfigureAwait(false);
                        var body = await connection.ReadBodyAsync(message, opened.Token).ConfigureAwait(false);
                        var page = TerraBaseConnection.ReadResponse(body, ShapeJsonContext.Default.ShapePage, message.StatusCode);
                        return (Page: (ShapePage?)page, Cursor: Header(message, "TB-Shape-Cursor"), Hints: Header(message, "TB-Shape-Hints") == Protocol);
                    }, cancellationToken).ConfigureAwait(false);
                    if (response.Page is not { } page) continue;
                    if (!Handle().IsMatch(page.Handle) || handle is not null && page.Handle != handle || !Offset().IsMatch(page.NextOffset)) throw InvalidResponse();
                    handle = page.Handle;
                    foreach (var change in page.Changes)
                    {
                        Apply(rows, change.Ops);
                        pending.RemoveAll(transaction => change.Confirmed?.Contains(transaction.Id) == true || change.Revert?.Contains(transaction.Id) == true);
                    }
                    var previousLocal = local;
                    if (page.Provisional is { } provisional)
                    {
                        pending = [.. provisional];
                        local = page.Local ?? throw InvalidResponse();
                    }
                    var unchanged = offset == page.NextOffset && previousLocal == local;
                    offset = page.NextOffset;
                    live = page.UpToDate;
                    cursor = response.Cursor ?? cursor;
                    useHints |= response.Hints;
                    attempt = 0;
                    if (!offset.StartsWith("snap.", StringComparison.Ordinal))
                        await writer.WriteAsync(View(live ? ShapeStatus.Live : ShapeStatus.Loading), cancellationToken).ConfigureAwait(false);
                    if (poll && unchanged)
                        await Task.Delay(TimeSpan.FromMilliseconds(1000 + Random.Shared.NextDouble() * 250), connection.TimeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TerraBaseException error) when (error.ErrorCode == "must_refetch")
                {
                    socket?.Dispose();
                    socket = null;
                    rows.Clear();
                    pending.Clear();
                    handle = cursor = null;
                    offset = "-1";
                    local = "0";
                    live = false;
                    await writer.WriteAsync(View(ShapeStatus.Loading), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (Transient(error))
                {
                    socket?.Dispose();
                    socket = null;
                    await writer.WriteAsync(View(ShapeStatus.Reconnecting), cancellationToken).ConfigureAwait(false);
                    await Task.Delay(Pause(error, attempt++), connection.TimeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally { socket?.Dispose(); }
    }

    private string Query(string? handle, string offset, bool live, string? cursor, string local) => TerraBaseConnection.Query(
        ("where", where), ("columns", columns), ("handle", handle), ("offset", offset),
        ("live", live ? "true" : null), ("cursor", live ? cursor : null), ("local", live ? local : null));

    private static void Apply(Dictionary<string, TerraBaseSqlRecord> rows, IReadOnlyList<ShapeOperation> ops)
    {
        foreach (var op in ops)
        {
            if (op.Move is not (null or "in" or "out")) throw InvalidResponse();
            if (op.Move == "out" || op.Move is null && op.Op == "delete") rows.Remove(op.Key);
            else if (op.Move == "in" || op.Op is "insert" or "update")
                rows[op.Key] = op.Row ?? throw InvalidResponse();
            else throw InvalidResponse();
        }
    }

    private static async Task<JsonElement> HintAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        var length = 0;
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            length += received.Count;
            if (received.MessageType != WebSocketMessageType.Text || !received.EndOfMessage && length == buffer.Length) throw InvalidResponse();
        } while (!received.EndOfMessage);
        var frame = TerraBaseConnection.ReadResponse(buffer.AsSpan(0, length), TerraBaseJsonContext.Wire.JsonElement, HttpStatusCode.OK);
        if (frame.GetProperty("type").GetString() == "error")
        {
            var code = frame.GetProperty("code").GetString();
            using var response = new HttpResponseMessage(code switch
            {
                "invalid_key" or "key_revoked" or "key_expired" => HttpStatusCode.Unauthorized,
                "forbidden_scope" or "rule_denied" or "column_not_readable" or "rules_invalid" => HttpStatusCode.Forbidden,
                "must_refetch" => HttpStatusCode.Conflict,
                "shape_busy" => HttpStatusCode.TooManyRequests,
                _ => HttpStatusCode.ServiceUnavailable,
            })
            { Content = new StringContent("{\"error\":" + frame.GetRawText() + "}") };
            if (frame.TryGetProperty("retry_after", out var retry) && retry.ValueKind == JsonValueKind.Number && retry.TryGetDouble(out var seconds)
                && double.IsFinite(seconds) && seconds >= 0)
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(Math.Min(seconds, MaxPause.TotalSeconds)));
            throw await TerraBaseException.FromResponseAsync(response, null, cancellationToken).ConfigureAwait(false);
        }
        return frame;
    }

    private async Task ReadyAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(10), connection.TimeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            var ready = await HintAsync(socket, deadline.Token).ConfigureAwait(false);
            if (ready.GetProperty("type").GetString() != "ready") throw InvalidResponse();
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && expiry.IsCancellationRequested)
        {
            throw new TimeoutException("Shape hint connection timed out.", error);
        }
    }

    private static bool Terminal(TerraBaseException error) => error.ErrorCode is
        "invalid_request" or "unsupported_table" or "unknown_database" or "unknown_table" or "invalid_key" or "key_revoked" or "key_expired" or "forbidden_scope" or "rule_denied" or "column_not_readable" or "rules_invalid"
        || error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool Transient(Exception error) => error switch
    {
        TerraBaseException refusal => !Terminal(refusal) && (int?)refusal.StatusCode is 408 or 429 or 500 or 502 or 503 or 504,
        HttpRequestException or WebSocketException or TimeoutException => true,
        _ => false,
    };

    private static TimeSpan Pause(Exception error, int attempt) => error is TerraBaseException { RetryAfter: { } retry } ? (retry < MaxPause ? retry : MaxPause)
        : TimeSpan.FromMilliseconds(Math.Min(MaxPause.TotalMilliseconds, 250 * Math.Pow(2, Math.Min(attempt, 8))) * (0.5 + Random.Shared.NextDouble() / 2));
    private static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    private static TerraBaseException InvalidResponse() => new(HttpStatusCode.OK, "TerraBase returned an invalid shape response.", "invalid_response", null);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex TableName();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnName();
    [GeneratedRegex("^h1-[a-f0-9]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex Handle();
    [GeneratedRegex("^snap\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotOffset();
    [GeneratedRegex("^(snap\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)|0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
}
