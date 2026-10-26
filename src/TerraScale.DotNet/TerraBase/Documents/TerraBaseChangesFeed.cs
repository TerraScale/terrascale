using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A committed change; deduplicate at-least-once delivery by (Origin, Version, Collection, Id).</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Id">Document key.</param>
/// <param name="Op">upsert or delete.</param>
/// <param name="Version">Decimal operation stamp, preserved as text.</param>
/// <param name="Origin">Original node, 16 hexadecimal digits.</param>
/// <param name="Document">Current document on the serving machine; null if absent, including an upsert since deleted.</param>
public sealed record TerraBaseChange(string Collection, string Id, string Op, string Version, string Origin, JsonElement? Document);

/// <summary>A change or a heartbeat checkpoint. Preserve non-null Cursor from both kinds before resuming in any region.</summary>
/// <param name="Cursor">Opaque per-origin vector checkpoint, or null before any checkpoint arrives. Id-less changes retain the prior checkpoint, so an interrupted group replays. Never compare, decode or merge it.</param>
/// <param name="Change">Change data; null for cursor events.</param>
/// <param name="Region">Validated X-TerraBase-Region of this connection, when supplied.</param>
public sealed record TerraBaseChangesEvent(string? Cursor, TerraBaseChange? Change, string? Region);

internal sealed record TerraBaseChangeCursor(string Cursor);
internal sealed record TerraBaseStreamError(TerraBaseStreamErrorDetail Error);
internal sealed record TerraBaseStreamErrorDetail(string Code, string? Message);

public sealed partial class TerraBaseDatabase
{
    // Producer ChangeCursor: base64url of 17 + 18 + 63 + (56 * 256) bytes, including scope.
    private const int MaxChangesCursorLength = 19246;

    /// <summary>
    /// Streams change and cursor events with documents:read. No cursor replays retained history; now starts at the head.
    /// LastEventId overrides cursor. Resume with the SAME collections filter and checkpoints from BOTH event kinds.
    /// A clean end (30 minutes or token expiry) requires reconnecting; errors never reset or reconnect automatically.
    /// 400 invalid_cursor rejects multiple Last-Event-ID values; 410 cursor_expired includes malformed, old or differently scoped cursors.
    /// For 410, explicitly re-read and restart with now; the SDK never resets the checkpoint itself.
    /// Includes document mutations only, not collection/index metadata or documents removed by a collection drop.
    /// Dispose the enumerator to close the response. The 30-second deadline covers acquisition; stalled reads stop after 45 seconds.
    /// </summary>
    public async IAsyncEnumerable<TerraBaseChangesEvent> StreamChangesAsync(string? cursor = null, string? lastEventId = null,
        IReadOnlyList<string>? collections = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (lastEventId is not null && (lastEventId.Length == 0 || lastEventId.Length > MaxChangesCursorLength || lastEventId.Any(char.IsControl)))
            throw new ArgumentException($"LastEventId must be one non-empty checkpoint without control characters, at most {MaxChangesCursorLength} characters.", nameof(lastEventId));
        if (collections is not null && (collections.Count is 0 or > 64 || collections.Any(name => string.IsNullOrEmpty(name) || name.Contains(',') || name.Any(char.IsControl))))
            throw new ArgumentException("Collections must hold 1 to 64 non-empty names without commas or control characters.", nameof(collections));
        var query = TerraBaseConnection.Query(("cursor", lastEventId is null ? cursor : null), ("collections", collections is null ? null : string.Join(',', collections)));
        using var opened = await connection.OpenChangesAsync(path + "/changes" + query,
            lastEventId is null ? null : headers => headers.Add("Last-Event-ID", lastEventId), cancellationToken).ConfigureAwait(false);
        var response = opened.Response;
        var region = TerraBaseException.RegionHeader(response, "X-TerraBase-Region");
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw InvalidStream(region);
        using var stream = new BoundedSseStream(await response.Content.ReadAsStreamAsync(opened.Token).ConfigureAwait(false), connection.TimeProvider, opened.Token);
        var parser = SseParser.Create(stream, static (type, bytes) => (Type: type, Data: bytes.ToArray()));
        await foreach (var item in parser.EnumerateAsync(opened.Token).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (type, data) = item.Data;
            if (type == "error")
            {
                var error = ReadStreamData(data, Json.TerraBaseStreamError, region);
                var status = error.Error.Code switch
                {
                    "cursor_expired" => HttpStatusCode.Gone,
                    "unknown_database" or "not_found" => HttpStatusCode.NotFound,
                    "tenant_memory" => HttpStatusCode.TooManyRequests,
                    "storage_busy" => HttpStatusCode.ServiceUnavailable,
                    _ => HttpStatusCode.OK,
                };
                using var terminal = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
                if (region is not null) terminal.Headers.Add("X-TerraBase-Region", region);
                throw await TerraBaseException.FromResponseAsync(terminal, opened.Token).ConfigureAwait(false);
            }
            if (type is not ("change" or "cursor")) continue;
            var id = item.EventId ?? parser.LastEventId;
            if (id.Length > MaxChangesCursorLength || item.EventId == "" || (type == "cursor" && item.EventId is null)) throw InvalidStream(region);
            TerraBaseChange? change = null;
            if (type == "change") change = ReadStreamData(data, Json.TerraBaseChange, region);
            else if (ReadStreamData(data, Json.TerraBaseChangeCursor, region).Cursor != id) throw InvalidStream(region);
            yield return new TerraBaseChangesEvent(id.Length == 0 ? null : id, change, region);
        }
    }

    private static T ReadStreamData<T>(byte[] data, JsonTypeInfo<T> typeInfo, string? region)
    {
        try { return TerraBaseConnection.ReadResponse(data, typeInfo, HttpStatusCode.OK); }
        catch (TerraBaseException error) { error.Region = region; throw; }
    }

    private static TerraBaseException InvalidStream(string? region) =>
        new(HttpStatusCode.OK, "TerraBase returned an invalid changes stream.", "invalid_response", null) { Region = region };
}
