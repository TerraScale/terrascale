using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Databases;

namespace TerraScale.Dashboard.Demo;

// Test-only TerraBase changes producer (I2 contracts/terrabase changes.sse) fed by this fixture's own document writes.
// Each database keeps a bounded log with its own sequence; cursors encode project/database:sequence. A stream stays open
// until the producer's 30 minute limit, and its writer stops at the first heartbeat after the reader goes away.
sealed partial class TestDocuments
{
    private const int RetainedChanges = 1024;
    private const string Origin = "0000000000000001";
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15), StreamLifetime = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, ChangeLog> logs = [];
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ChangeLog
    {
        public long Sequence, Floor;
        public readonly Queue<(long Sequence, string Collection, string Id, string Op, JsonElement? Document)> Items = new();
    }

    // Callers hold the collections lock.
    private void RecordChange(DatabaseScope scope, string database, string collection, string id, string op)
    {
        var key = scope.ProjectId + "/" + database;
        if (!logs.TryGetValue(key, out var log)) logs[key] = log = new();
        var document = op == "delete" ? null : collections.GetValueOrDefault(key + "/" + collection)?.GetValueOrDefault(id)?.Document.Clone();
        log.Items.Enqueue((++log.Sequence, collection, id, op, document));
        if (log.Items.Count > RetainedChanges) log.Floor = log.Items.Dequeue().Sequence;
        changed.TrySetResult();
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The caller has already authorized a documents:read project bearer for this project.
    public HttpResponseMessage Changes(string projectId, string database, string? region, HttpRequestMessage request)
    {
        var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
        var resume = request.Headers.TryGetValues("Last-Event-ID", out var values) ? values.Single()
            : query.TryGetValue("cursor", out var cursor) ? cursor.ToString() : null;
        var filter = query.TryGetValue("collections", out var names) ? names.ToString().Split(',').ToHashSet(StringComparer.Ordinal) : null;
        var key = projectId + "/" + database;
        long start;
        ChangeLog source;
        lock (collections)
        {
            if (!logs.TryGetValue(key, out var log)) logs[key] = log = new();
            source = log;
            var (head, floor) = (log.Sequence, log.Floor);
            if (resume is null) start = floor;
            else if (resume == "now") start = head;
            else if (DecodeCursor(resume, key) is { } decoded) start = decoded;
            else return ChangesError(HttpStatusCode.BadRequest, "invalid_cursor", "The cursor does not belong to this database.");
            if (start < floor || start > head) return ChangesError(HttpStatusCode.Gone, "cursor_expired", "Retained changes no longer cover the cursor.");
        }
        var pipe = new Pipe();
        _ = ProduceChangesAsync(pipe.Writer, key, source, start, filter);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
        response.Content.Headers.ContentType = new("text/event-stream");
        response.Headers.CacheControl = new() { NoStore = true };
        if (region is not null) response.Headers.Add("X-TerraBase-Region", region);
        return response;
    }

    private async Task ProduceChangesAsync(PipeWriter writer, string key, ChangeLog source, long position, HashSet<string>? filter)
    {
        using var lifetime = new CancellationTokenSource(StreamLifetime);
        Exception? failure = null;
        var text = new StringBuilder();
        Cursor(text, key, position);
        try
        {
            while (true)
            {
                Task signal;
                lock (collections)
                {
                    var log = logs.GetValueOrDefault(key);
                    // Dropped, recreated or trimmed past this reader: end cleanly so its reconnect receives 410 cursor_expired.
                    if (log != source || position < source.Floor || position > source.Sequence) break;
                    var delivered = position;
                    foreach (var item in log?.Items.Where(item => item.Sequence > position).ToArray() ?? [])
                    {
                        position = item.Sequence;
                        if (filter?.Contains(item.Collection) == false) continue;
                        text.Append("event: change\nid: ").Append(EncodeCursor(key, position)).Append("\ndata: ").Append(JsonSerializer.Serialize(new
                        {
                            collection = item.Collection,
                            id = item.Id,
                            op = item.Op,
                            version = item.Sequence.ToString(CultureInfo.InvariantCulture),
                            origin = Origin,
                            document = item.Document
                        })).Append("\n\n");
                        delivered = position;
                    }
                    // Filtered-out changes still advance the reader's checkpoint.
                    if (delivered != position) Cursor(text, key, position);
                    signal = changed.Task;
                }
                if (text.Length > 0)
                {
                    var flushed = await writer.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), lifetime.Token);
                    text.Clear();
                    if (flushed.IsCompleted) break;
                }
                try { await signal.WaitAsync(Heartbeat, lifetime.Token); }
                catch (TimeoutException) { text.Append(": heartbeat\n\n"); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally { await writer.CompleteAsync(failure); }
    }

    private static void Cursor(StringBuilder text, string key, long position)
    {
        var cursor = EncodeCursor(key, position);
        text.Append("event: cursor\nid: ").Append(cursor).Append("\ndata: ").Append(JsonSerializer.Serialize(new { cursor })).Append("\n\n");
    }

    private static string EncodeCursor(string key, long position) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(key + ":" + position.ToString(CultureInfo.InvariantCulture)));

    private static long? DecodeCursor(string cursor, string key)
    {
        try
        {
            var text = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
            return text.StartsWith(key + ":", StringComparison.Ordinal) &&
                long.TryParse(text[(key.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var position) ? position : null;
        }
        catch (FormatException) { return null; }
    }

    private static HttpResponseMessage ChangesError(HttpStatusCode status, string code, string message) =>
        new(status) { Content = JsonContent.Create(new { error = new { code, message } }) };
}
