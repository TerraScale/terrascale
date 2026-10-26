using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.Import;

/// <summary>
/// An NDJSON source (docs/terrabase/import-ndjson.md): one JSON object per line, either an export line
/// <c>{"collection","id","document"}</c> or a document whose <c>collection</c> member places it; any <c>id</c> or <c>_id</c>
/// member stays a field and the document gets a generated ULID. Each collection is a table of schema <c>main</c>; a position is the byte offset of the next line.
/// </summary>
internal sealed class NdjsonFile : IImportSource
{
    // Every row holds the document body (a blob of JSON) and its id (null when the line names none).
    private static readonly IReadOnlyList<SourceColumn> Columns = [new() { Name = "document", Type = "json" }, new() { Name = "id", Type = "text" }];
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 64 };

    private readonly string _path;
    private readonly string? _collection;
    private readonly Dictionary<string, SourceTable> _tables = [];
    private readonly HashSet<string> _auditRecords = [];

    private NdjsonFile(string path, string? collection)
    {
        _path = path;
        _collection = collection;
    }

    public SourceSchema Schema { get; } = new();

    /// <summary>Reads the file once to find its collections; lines without one go to <paramref name="collection"/>.</summary>
    public static NdjsonFile Open(string path, string? collection, CancellationToken cancellationToken)
    {
        var file = new NdjsonFile(path, collection);
        foreach (var row in file.Rows(null, cancellationToken))
        {
            row.Table.Rows++;
        }

        return file;
    }

    public IEnumerable<SourceRow> Rows(ImportPosition? from, CancellationToken cancellationToken)
    {
        using var lines = new ByteLines(_path, from?.Offset ?? 0);
        while (Next(lines) is var (collection, id, body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (collection == ImportNames.Collection && IsAuditRecord(id, body))
            {
                if (_auditRecords.Add(id!))
                {
                    Schema.Skip("import_audit_record", collection + "/" + id);
                }

                continue;
            }

            yield return new SourceRow(Table(collection), [new(SourceKind.Blob, Blob: body), id is null ? SourceValue.Null : new(SourceKind.Text, Text: id)],
                Columns, null, new ImportPosition(lines.Position, null, null, 0, 0));
        }
    }

    // Imported databases export these two records too. They describe the old job, not the new destination.
    // Recognize both the fixed id and versioned record shape; other rows in this collection still block.
    private static bool IsAuditRecord(string? id, byte[] body)
    {
        if (id is not (ImportNames.JobId or ImportNames.StateId))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body, Options);
            var root = document.RootElement;
            if (root.GetPropertyCount() != 3 || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("rev", out var revision) || !root.TryGetProperty("record", out var text) || text.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            using var record = JsonDocument.Parse(text.GetString()!, Options);
            var value = record.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("v", out version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out number) || number != 1)
            {
                return false;
            }

            var fields = id == ImportNames.JobId ? TerraBaseJsonContext.Default.ImportJobRecord.Properties : TerraBaseJsonContext.Default.ImportStateRecord.Properties;
            if (value.GetPropertyCount() != fields.Count || fields.Any(field => !value.TryGetProperty(field.Name, out _)))
            {
                return false;
            }

            return id == ImportNames.JobId
                ? revision.ValueKind == JsonValueKind.Null && value.TryGetProperty("id", out var job) && job.ValueKind == JsonValueKind.String && EntityIds.IsValid(job.GetString())
                    && value.TryGetProperty("databaseId", out var database) && database.ValueKind == JsonValueKind.String && EntityIds.IsValid(database.GetString())
                    && value.TryGetProperty("sourceSha256", out var sha) && sha.ValueKind == JsonValueKind.String && sha.GetString() is { Length: 64 } hash && hash.All(char.IsAsciiHexDigitLower)
                : revision.ValueKind == JsonValueKind.String && EntityIds.IsValid(revision.GetString())
                    && value.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.String && rev.GetString() == revision.GetString()
                    && value.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() is
                        ImportStates.AwaitingSource or ImportStates.Analyzing or ImportStates.Ready or ImportStates.Rejected or ImportStates.Queued
                        or ImportStates.Running or ImportStates.Validating or ImportStates.Completed or ImportStates.Failed or ImportStates.Cancelled;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
    }

    private SourceTable Table(string collection)
    {
        if (!_tables.TryGetValue(collection, out var table))
        {
            if (_tables.Count >= ImportPlanner.MaxTables)
            {
                throw new SourceFormatException("too_many_tables", "an import holds at most 1000 collections");
            }

            table = new SourceTable { Schema = "main", Name = collection };
            if (!DatabaseName.TryParse(collection, out _))
            {
                table.Issues.Add(new("blocking", "invalid_collection_name", null, null, "a collection name is lowercase, at most 63 characters"));
            }

            _tables[collection] = table;
            Schema.Tables.Add(table);
        }

        return table;
    }

    // The next non-blank line as its collection, id and body; null at the end of the file.
    private (string Collection, string? Id, byte[] Body)? Next(ByteLines lines)
    {
        ReadOnlySpan<byte> line;
        do
        {
            if (!lines.TryRead(out line))
            {
                return null;
            }
        }
        while (line.Trim(" \t"u8).IsEmpty);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.ToArray(), Options);
        }
        catch (JsonException)
        {
            throw Malformed(lines.LineStart, "is not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(lines.LineStart, "is not a JSON object");
            }

            // An export line keeps its collection, id and document exactly.
            if (root.GetPropertyCount() == 3 && root.TryGetProperty("collection", out var named) && named.ValueKind == JsonValueKind.String
                && root.TryGetProperty("id", out var key) && root.TryGetProperty("document", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                return (named.GetString()!, Id(key, lines.LineStart), Encoding.UTF8.GetBytes(body.GetRawText()));
            }

            // options.collection places every document whole; otherwise the line's collection member does and is removed.
            string? collection = _collection;
            var drop = new List<string>(2);
            if (collection is null && root.TryGetProperty("collection", out named) && named.ValueKind == JsonValueKind.String)
            {
                collection = named.GetString();
                drop.Add("collection");
            }

            if (collection is null)
            {
                throw Malformed(lines.LineStart, "names no collection; add a collection member or set options.collection");
            }

            var buffer = new ArrayBufferWriter<byte>(line.Length);
            using (var writer = ImportMapping.Writer(buffer))
            {
                writer.WriteStartObject();
                foreach (var member in root.EnumerateObject().Where(member => !drop.Contains(member.Name)))
                {
                    member.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return (collection, null, buffer.WrittenSpan.ToArray());
        }
    }

    // A string id as it is, a number as its text, a MongoDB {"$oid": …} as its hex.
    private static string Id(JsonElement key, long offset) => key.ValueKind switch
    {
        JsonValueKind.String => key.GetString()!,
        JsonValueKind.Number => key.GetRawText(),
        JsonValueKind.Object when key.GetPropertyCount() == 1 && key.TryGetProperty("$oid", out var oid) && oid.ValueKind == JsonValueKind.String => oid.GetString()!,
        _ => throw Malformed(offset, "has an id that is not a string, a number or {\"$oid\": …}"),
    };

    private static SourceFormatException Malformed(long offset, string problem) =>
        new("malformed_source", $"the line at byte {offset.ToString(CultureInfo.InvariantCulture)} {problem}");
}
