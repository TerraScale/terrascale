using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One exported live document.</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Id">Document id, a ULID.</param>
/// <param name="Document">The stored document.</param>
public sealed record TerraBaseExportLine(string Collection, string Id, JsonElement Document)
{
    /// <summary>The cursor that resumes an export after this document.</summary>
    public TerraBaseExportCursor Cursor => new(Collection, Id);
}

/// <summary>Resumes an export after this document (<c>after_collection</c>, <c>after_id</c>).</summary>
/// <param name="Collection">Collection of the last line received.</param>
/// <param name="Id">Id of the last line received.</param>
public sealed record TerraBaseExportCursor(string Collection, string Id);

public sealed partial class TerraBaseDatabase
{
    private const string NdjsonMediaType = "application/x-ndjson";

    /// <summary>
    /// Streams every live document as NDJSON (<c>GET …/export</c>) with documents:read, in collection then id order, from
    /// one read snapshot. To resume an interrupted export, pass the last received line's <see cref="TerraBaseExportLine.Cursor"/>;
    /// the resumed part reads a new snapshot. A connection lost mid-stream throws, and a final line without its newline is
    /// <c>invalid_response</c>: no line is delivered twice or half. RequestTimeout covers acquisition only; dispose the
    /// enumerator or cancel to close the response. Each line is bounded by MaxResponseBytes.
    /// </summary>
    public async IAsyncEnumerable<TerraBaseExportLine> ExportAsync(TerraBaseExportCursor? after = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = string.Empty;
        if (after is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(after.Collection, nameof(after));
            query = TerraBaseConnection.Query(("after_collection", after.Collection), ("after_id", TerraScaleIds.Validate(after.Id, nameof(after))));
        }
        using var opened = await connection.OpenExportAsync(path + "/export" + query, cancellationToken).ConfigureAwait(false);
        var response = opened.Response;
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, NdjsonMediaType, StringComparison.OrdinalIgnoreCase))
            throw InvalidExport();
        var reader = PipeReader.Create(await response.Content.ReadAsStreamAsync(opened.Token).ConfigureAwait(false));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(opened.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                var lines = new List<TerraBaseExportLine>();
                while (buffer.PositionOf((byte)'\n') is { } end)
                {
                    var line = buffer.Slice(0, end);
                    if (line.Length > connection.MaxResponseBytes) throw InvalidExport();
                    lines.Add(TerraBaseConnection.ReadResponse(line.IsSingleSegment ? line.FirstSpan : line.ToArray(), Json.TerraBaseExportLine, HttpStatusCode.OK));
                    buffer = buffer.Slice(buffer.GetPosition(1, end));
                }
                if (buffer.Length > connection.MaxResponseBytes || (result.IsCompleted && !buffer.IsEmpty)) throw InvalidExport();
                reader.AdvanceTo(buffer.Start, buffer.End);
                foreach (var line in lines) yield return line;
                if (result.IsCompleted) yield break;
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static TerraBaseException InvalidExport() =>
        new(HttpStatusCode.OK, "TerraBase returned an invalid or incomplete export stream.", "invalid_response", null);
}
