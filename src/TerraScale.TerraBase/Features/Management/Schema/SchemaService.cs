using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Schema;

/// <summary>Infers fields from the first live documents of a collection in id order.</summary>
internal sealed partial class SchemaService(IStorageEngine engine) : ISchemaService
{
    private const int MaxSampledDocuments = 1000;
    private const int PageSize = 100;
    private const long MaxSampledBytes = 8 * 1024 * 1024;
    private const int MaxDepth = 8;
    private const int MaxFields = 10_000;
    private const int MaxPathBytes = 1024;
    private const long MaxFieldPathBytes = 1024 * 1024;
    private const long MaxResponseBytes = 8 * 1024 * 1024;

    // Same code as a document query over the engine read budget.
    private static readonly Validation QueryTooExpensive = new("query too expensive", "invalid_query");
    private static readonly Validation SchemaTooExpensive = new("schema too expensive", "invalid_query");

    public async ValueTask<ListCollectionsResult> ListCollectionsAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return new CollectionList([]);
        }

        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                return new CollectionList(await transaction.ListCollectionStatsAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (QueryCapacityException)
            {
                return QueryTooExpensive;
            }
        }
    }

    public async ValueTask<IReadOnlyList<CollectionCount>?> CountCollectionsAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.CountCollectionsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<InferSchemaResult> InferAsync(TenantContext database, string collection, CancellationToken cancellationToken)
    {
        var transaction = await BeginAsync(database, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return new NotFound();
        }

        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                return await InferAsync(transaction, collection, cancellationToken).ConfigureAwait(false);
            }
            catch (QueryCapacityException)
            {
                return SchemaTooExpensive;
            }
        }
    }

    private static async ValueTask<InferSchemaResult> InferAsync(IStorageTransaction transaction, string collection, CancellationToken cancellationToken)
    {
        if (await transaction.GetCollectionAsync(collection, cancellationToken).ConfigureAwait(false) is null)
        {
            return new NotFound();
        }

        var documents = await transaction.CountDocumentsAsync(collection, cancellationToken).ConfigureAwait(false);

        var fields = new SortedDictionary<string, FieldCounts>(StringComparer.Ordinal);
        var sampled = 0;
        long bytes = 0;
        long fieldPathBytes = 0;
        string? after = null;
        var full = false;
        while (!full && sampled < MaxSampledDocuments && bytes < MaxSampledBytes)
        {
            var page = await transaction.ListAsync(
                collection, after, Math.Min(PageSize, MaxSampledDocuments - sampled), MaxSampledBytes - bytes, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents)
            {
                // A page may start with a document larger than the budget left; stop before parsing it.
                if (bytes + document.Json.Length > MaxSampledBytes)
                {
                    full = true;
                    break;
                }

                using var json = JsonDocument.Parse(document.Json);
                if (!Walk(json.RootElement, prefix: null, prefixBytes: 0, depth: 1, fields, ref fieldPathBytes, cancellationToken))
                {
                    return SchemaTooExpensive;
                }

                sampled++;
                bytes += document.Json.Length;
            }

            if (full || !page.HasMore)
            {
                break;
            }

            after = page.Documents[^1].Id;
        }

        var schema = new CollectionSchema(
            collection,
            documents,
            sampled,
            [.. fields.Select(field => new SchemaField(field.Key, field.Value.Present, field.Value.Types))]);
        // Field/path limits also bound this serialization, including worst-case JSON escaping.
        if (JsonSerializer.SerializeToUtf8Bytes(schema, TerraBaseJsonContext.Default.CollectionSchema).LongLength > MaxResponseBytes)
        {
            return SchemaTooExpensive;
        }

        return schema;
    }

    // Documents preserve duplicate members. Use the first member, including its subtree, like SQL's json_extract
    // and projections; later duplicates do not contribute types or children. Do not descend into arrays or below MaxDepth.
    // False when the field table would exceed its bounds.
    private static bool Walk(
        JsonElement value, string? prefix, int prefixBytes, int depth, SortedDictionary<string, FieldCounts> fields,
        ref long fieldPathBytes, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = member.Name;
            // Check before quoting or concatenating: a huge parent name with thousands of children otherwise
            // expands a small stored document into gigabytes of paths.
            if (name.Length > MaxPathBytes)
            {
                return false;
            }

            if (!seen.Add(name))
            {
                continue;
            }

            var identifier = Identifier().IsMatch(name);
            var pathBytes = prefixBytes + (prefix is null ? 0 : 1) + Encoding.UTF8.GetByteCount(name)
                + (identifier ? 0 : 2 + name.Count(character => character == '"'));
            if (pathBytes > MaxPathBytes)
            {
                return false;
            }

            var segment = identifier ? name : Quote(name);
            var path = prefix is null ? segment : prefix + "." + segment;
            if (!fields.TryGetValue(path, out var field))
            {
                if (fields.Count == MaxFields || fieldPathBytes + pathBytes > MaxFieldPathBytes)
                {
                    return false;
                }

                fieldPathBytes += pathBytes;
                fields[path] = field = new FieldCounts();
            }

            var type = TypeOf(member.Value);
            field.Present++;
            field.Types[type] = field.Types.GetValueOrDefault(type) + 1;
            if (member.Value.ValueKind == JsonValueKind.Object && depth < MaxDepth
                && !Walk(member.Value, path, pathBytes, depth + 1, fields, ref fieldPathBytes, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    // Member names that are not plain identifiers are double-quoted (SQL style, inner quotes doubled), so a literal
    // "a.b" member and a nested a → b never share a path.
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    private static string TypeOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "null",
    };

    private ValueTask<IStorageTransaction?> BeginAsync(TenantContext database, CancellationToken cancellationToken) =>
        engine.BeginTransactionAsync(database, write: false, cancellationToken);

    private sealed class FieldCounts
    {
        public int Present { get; set; }

        public SortedDictionary<string, int> Types { get; } = [with(StringComparer.Ordinal)];
    }
}
