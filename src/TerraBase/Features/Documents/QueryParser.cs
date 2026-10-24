using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>A parsed query request.</summary>
/// <param name="Filter">Filter, or <see langword="null"/> for every document.</param>
/// <param name="Sort">Sort keys.</param>
/// <param name="Project">Paths to keep, or <see langword="null"/> for whole documents.</param>
/// <param name="Limit">Page size.</param>
/// <param name="Digest">Binds cursors to this database, collection, filter, sort and projection.</param>
/// <param name="Near">Vector similarity order replacing <paramref name="Sort"/>.</param>
/// <param name="Aggregate">Groups instead of documents.</param>
/// <param name="Explain">Also report how the query ran.</param>
internal sealed record ParsedQuery(
    DocumentFilter? Filter, IReadOnlyList<SortKey> Sort, IReadOnlyList<DocumentPath>? Project, int Limit, string Digest, NearSort? Near = null,
    DocumentAggregate? Aggregate = null, bool Explain = false);

/// <summary>Parses the query v1 grammar shared by queries and transaction assertions.</summary>
internal static class QueryParser
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 1000;

    private const int MaxCursorChars = 1024 * 1024;

    private const string Code = "invalid_query";
    private const int MaxFilterDepth = 8;
    private const int MaxFilterNodes = 256;
    private const int MaxChildren = 64;
    private const int MaxValues = 256;
    private const int MaxSortKeys = 4;
    private const int MaxNearLimit = 1000;
    private const int MaxAggregateValues = 16;
    private const int MaxNearDimensions = 4096;
    private const int MaxProjection = 64;
    private const int MaxSegments = 16;
    private const int MaxPathLength = 4096;

    /// <summary>A query request as an engine query, or the first rule it breaks (<c>invalid_query</c>).</summary>
    public static Valid<ParsedQuery> CheckParse(TenantContext database, string collection, QueryRequest request)
    {
        try
        {
            return Parsed(database, collection, request);
        }
        catch (FormatException error)
        {
            return new Validation(error.Message, Code);
        }
    }

    private static ParsedQuery Parsed(TenantContext database, string collection, QueryRequest request)
    {
        if (request.Explain.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("explain is true or false");
        }

        if (request.Limit is < 1 or > MaxLimit)
        {
            throw Invalid("limit must be between 1 and 1000");
        }

        var sort = request.Sort ?? [];
        var nodes = 0;
        if (sort.Count > MaxSortKeys)
        {
            throw Invalid("sort has at most 4 keys");
        }

        if (request.Project is { Count: > MaxProjection })
        {
            throw Invalid("project has at most 64 paths");
        }

        var filter = request.Filter.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : Filter(request.Filter, 1, ref nodes);
        var near = sort.Any(static key => key.Near is not null) ? Near(sort, request) : null;
        var keys = (near is null ? sort : []).Select(key => new SortKey(key.Path.ValueKind == JsonValueKind.String && key.Path.GetString() == "$score" ? DocumentPath.Id : Path(key.Path), key.Order switch
        {
            null or "asc" => false,
            "desc" => true,
            _ => throw Invalid("sort order is asc or desc"),
        }, key.Path.ValueKind == JsonValueKind.String && key.Path.GetString() == "$score")).ToList();
        if (keys.Any(key => key.Score) && (keys is not [{ Score: true, Descending: true }] || Searches(filter).Count() != 1))
        {
            throw Invalid("$score requires exactly one search filter and is the only sort key, with order desc");
        }

        if (near is not null && Searches(filter).Any())
        {
            throw Invalid("search cannot be combined with near");
        }

        // A repeated path cannot break a tie in its earlier occurrence, regardless of direction.
        // At most four keys: compare decoded segments directly, including embedded NUL.
        for (var i = keys.Count - 1; i > 0; i--)
        {
            var path = keys[i].Path;
            if (keys.Take(i).Any(key => path.IsId ? key.Path.IsId
                : key.Path.Segments is { } segments && path.Segments!.SequenceEqual(segments, StringComparer.Ordinal)))
            {
                keys.RemoveAt(i);
            }
        }

        // Id is unique: keys after it can never break a tie or contribute to a continuation.
        var idKey = keys.FindIndex(key => key.Path.IsId);
        if (idKey >= 0)
        {
            keys.RemoveRange(idKey + 1, keys.Count - idKey - 1);
        }

        var project = request.Project is null ? null : Projection.Normalize(request.Project.Select(Path));
        var digest = Digest(database, collection, Normalize(request.Filter), JsonSerializer.Serialize(sort, TerraBaseJsonContext.Default.IReadOnlyListQuerySortRequest),
            request.Project is null ? "" : JsonSerializer.Serialize(request.Project, TerraBaseJsonContext.Default.IReadOnlyListJsonElement));
        return new ParsedQuery(filter, keys, project, request.Limit ?? DefaultLimit, digest, near, request.Aggregate is { } aggregate ? Aggregate(aggregate, request) : null,
            request.Explain.ValueKind == JsonValueKind.True);
    }

    private static DocumentAggregate Aggregate(QueryAggregateRequest aggregate, QueryRequest request)
    {
        if (request.Sort is not null || request.Project is not null || request.Cursor is not null || request.Count is not null)
        {
            throw Invalid("aggregate takes no sort, project, cursor or count");
        }

        if (aggregate.GroupBy is { Count: > MaxSortKeys })
        {
            throw Invalid("group_by has at most 4 paths");
        }

        var values = new List<(string Op, DocumentPath Path)>();
        foreach (var (op, paths) in (ReadOnlySpan<(string, IReadOnlyList<string>?)>)[("sum", aggregate.Sum), ("avg", aggregate.Avg), ("min", aggregate.Min), ("max", aggregate.Max)])
        {
            foreach (var pointer in paths ?? [])
            {
                if (pointer == "$id" || (paths!.Count(other => other == pointer) > 1))
                {
                    throw Invalid($"{op} takes distinct JSON Pointers");
                }

                values.Add((op, Path(JsonSerializer.SerializeToElement(pointer, TerraBaseJsonContext.Default.String))));
            }
        }

        return values.Count > MaxAggregateValues
            ? throw Invalid("an aggregate has at most 16 sum, avg, min and max paths")
            : new DocumentAggregate([.. (aggregate.GroupBy ?? []).Select(Path)], values);
    }

    private static NearSort Near(IReadOnlyList<QuerySortRequest> sort, QueryRequest request)
    {
        if (sort is not [{ Near: { } near } key] || key.Path.ValueKind != JsonValueKind.Undefined || key.Order is not null)
        {
            throw Invalid("near is the only sort key, without path or order");
        }

        if (request.Cursor is not null || request.Limit is > MaxNearLimit)
        {
            throw Invalid("near returns one page of at most 1000 documents, without a cursor");
        }

        if (near.Lat is not null || near.Lon is not null)
        {
            return near.Vector is not null || near.Metric is not null ? throw Invalid("a geo near takes lat and lon, without vector or metric")
                : Path(near.Path) is { IsId: false } point ? new NearSort(point, [], false, Point(near.Lat, near.Lon))
                : throw Invalid("near path is a JSON Pointer");
        }

        if (near.Vector is not { Count: >= 1 and <= MaxNearDimensions } vector || vector.Any(static value => Math.Abs(value) > float.MaxValue))
        {
            throw Invalid("near vector has 1 to 4096 numbers within the float32 range");
        }

        var euclidean = near.Metric switch
        {
            null or "cosine" => false,
            "l2" => true,
            _ => throw Invalid("near metric is cosine or l2"),
        };
        if (!euclidean && vector.All(static value => (float)value == 0))
        {
            throw Invalid("a cosine near vector is not all zeros");
        }

        var path = Path(near.Path);
        return path.IsId ? throw Invalid("near path is a JSON Pointer") : new NearSort(path, vector, euclidean);
    }

    /// <summary>Parses a filter, or answers the first rule it breaks; <paramref name="code"/> names the error of a bad one.</summary>
    public static Valid<DocumentFilter> CheckFilter(JsonElement filter, string code = Code)
    {
        var nodes = 0;
        try
        {
            return Filter(filter, 1, ref nodes);
        }
        catch (FormatException error)
        {
            return new Validation(error.Message, code);
        }
    }

    /// <summary>
    /// Parses an RFC 6901 JSON Pointer of object members (1 to 16 segments, at most 4 KiB);
    /// <see langword="null"/> when <paramref name="pointer"/> is not one.
    /// </summary>
    /// <summary>
    /// The segments of an RFC 6901 JSON Pointer of object members (1 to 16 segments, at most 4 KiB), or the rule it
    /// breaks: <paramref name="invalid"/> when it is not one.
    /// </summary>
    public static Valid<IReadOnlyList<string>> CheckPointer(string? pointer, string code, string invalid)
    {
        if (ParsePointer(pointer, out var tooDeep) is not { } parsed)
        {
            return new Validation(tooDeep ? TooDeep : invalid, code);
        }

        IReadOnlyList<string> segments = parsed;
        return segments;
    }

    private const string TooDeep = "a path has at most 16 segments";

    // null when the pointer is not one, also when it has too many segments (tooDeep).
    private static string[]? ParsePointer(string? pointer, out bool tooDeep)
    {
        tooDeep = false;
        if (pointer is null || pointer.Length > MaxPathLength || !pointer.StartsWith('/'))
        {
            return null;
        }

        var segments = pointer[1..].Split('/');
        if (segments.Length > MaxSegments)
        {
            tooDeep = true;
            return null;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Replace("~0", "", StringComparison.Ordinal).Replace("~1", "", StringComparison.Ordinal).Contains('~', StringComparison.Ordinal))
            {
                return null;
            }

            segments[i] = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }

        return segments;
    }

    /// <summary>
    /// Binds a cursor to a query: SHA-256 over its scope and normalized members. The scope is the database's id, unique
    /// across tenants, so a cursor never applies to another tenant's database of the same name.
    /// </summary>
    public static string Digest(TenantContext database, string collection, params string[] members)
    {
        ArgumentNullException.ThrowIfNull(database);
        var text = string.Join('\0', [database.DatabaseId!, collection, .. members]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))[..16]);
    }

    /// <summary>Encodes the position after <paramref name="row"/> as an opaque cursor, or answers why it cannot be one.</summary>
    public static Valid<string> CheckCursor(string digest, QueryRow row, byte[]? requestBody = null)
    {
        // Four keys of at most 64 KiB UTF-8 remain below the 4 MiB request cap even after JSON
        // escaping (at most six bytes per source byte) and base64 expansion.
        if (row.Keys.OfType<string>().Any(text => Encoding.UTF8.GetByteCount(text) > 64 * 1024))
        {
            return SortRequiresIndex("cursor sort values are at most 64 KiB of UTF-8");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(digest);
            writer.WriteStringValue(row.Document.Id);
            foreach (var key in row.Keys)
            {
                switch (key)
                {
                    case long number:
                        writer.WriteNumberValue(number);
                        break;
                    case double number:
                        writer.WriteNumberValue(number);
                        break;
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    default:
                        writer.WriteNullValue();
                        break;
                }
            }

            writer.WriteEndArray();
        }

        var cursor = Base64Url.EncodeToString(buffer.ToArray());
        if (cursor.Length > MaxCursorChars)
        {
            return SortRequiresIndex("encoded cursors are at most 1 MiB");
        }

        if (requestBody is not null)
        {
            // A continuation repeats filter/sort/project. Bound the complete compact request, replacing
            // any previous cursor, so even a large filter cannot make the emitted page inaccessible.
            using var request = JsonDocument.Parse(requestBody);
            using var writer = new Utf8JsonWriter(Stream.Null);
            writer.WriteStartObject();
            foreach (var member in request.RootElement.EnumerateObject())
            {
                if (member.Name != "cursor")
                {
                    member.WriteTo(writer);
                }
            }

            writer.WriteString("cursor", cursor);
            writer.WriteEndObject();
            writer.Flush();
            if (writer.BytesCommitted > DocumentRoute.MaxRequestBytes)
            {
                return SortRequiresIndex("the query and cursor exceed the continuation request budget");
            }
        }

        return cursor;
    }

    private static Validation SortRequiresIndex(string message) => new(message, "sort_requires_index");

    /// <summary>Decodes a cursor of the query bound to <paramref name="digest"/> with <paramref name="sortKeys"/> sort keys.</summary>
    public static Valid<QueryPosition> CheckPosition(string cursor, string digest, int sortKeys)
    {
        try
        {
            if (cursor.Length > MaxCursorChars)
            {
                throw new FormatException("cursor too large");
            }

            using var json = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor));
            var items = json.RootElement.EnumerateArray().ToList();
            if (items.Count == 2 + (2 * sortKeys) && items[0].GetString() == digest
                && items[1].GetString() is { Length: > 0 and <= 256 } id && !id.Any(char.IsControl))
            {
                for (var i = 2; i < items.Count; i += 2)
                {
                    if (!items[i].TryGetInt64(out var rank) || rank is < 0 or > 6 || !ValidSortValue(rank, items[i + 1]))
                    {
                        throw new FormatException("invalid cursor sort keys");
                    }
                }

                return new QueryPosition([.. items.Skip(2).Select(Key)], id);
            }
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
        {
            // Falls through to invalid_cursor.
        }

        return new Validation("the cursor does not belong to this query", "invalid_cursor");
    }

    private static bool ValidSortValue(long rank, JsonElement value) => rank switch
    {
        4 => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        5 => value.ValueKind == JsonValueKind.String,
        _ => value.ValueKind == JsonValueKind.Null,
    };

    private static object? Key(JsonElement key) => key.ValueKind switch
    {
        JsonValueKind.Number when key.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => key.GetDouble(),
        JsonValueKind.String => key.GetString(),
        JsonValueKind.Null => null,
        _ => throw new FormatException("a cursor key is a scalar"),
    };

    private static DocumentFilter Filter(JsonElement filter, int depth, ref int nodes)
    {
        if (depth > MaxFilterDepth || ++nodes > MaxFilterNodes)
        {
            throw Invalid("a filter nests at most 8 levels and has at most 256 nodes");
        }

        if (filter.ValueKind != JsonValueKind.Object || filter.GetPropertyCount() != 1)
        {
            throw Invalid("a filter is an object with exactly one operator");
        }

        var member = filter.EnumerateObject().First();
        var operand = member.Value;
        switch (member.Name)
        {
            case "and" or "or":
                if (operand.ValueKind != JsonValueKind.Array || operand.GetArrayLength() is < 1 or > MaxChildren)
                {
                    throw Invalid($"{member.Name} takes 1 to 64 filters");
                }

                var children = new List<DocumentFilter>();
                foreach (var child in operand.EnumerateArray())
                {
                    children.Add(Filter(child, depth + 1, ref nodes));
                }

                return member.Name == "and" ? new AndFilter(children) : new OrFilter(children);
            case "not":
                return new NotFilter(Filter(operand, depth + 1, ref nodes));
            case "exists":
                return new ExistsFilter(Path(operand));
        }

        if (operand.ValueKind != JsonValueKind.Array || operand.GetArrayLength() != 2)
        {
            throw Invalid("a scalar filter takes [path, value]");
        }

        var path = Path(operand[0]);
        var value = operand[1];
        switch (member.Name)
        {
            case "eq":
                return new CompareFilter(path, Comparison.Equal, Scalar(value));
            case "ne":
                return new NotFilter(new CompareFilter(path, Comparison.Equal, Scalar(value)));
            case "lt" or "lte" or "gt" or "gte":
                var scalar = Scalar(value);
                if (scalar.Kind == ScalarKind.Null)
                {
                    throw Invalid("ordered comparisons reject null");
                }

                return new CompareFilter(path, member.Name switch
                {
                    "lt" => Comparison.Less,
                    "lte" => Comparison.LessOrEqual,
                    "gt" => Comparison.Greater,
                    _ => Comparison.GreaterOrEqual,
                }, scalar);
            case "in" or "nin":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > MaxValues)
                {
                    throw Invalid($"{member.Name} takes [path, [1 to 256 values]]");
                }

                DocumentFilter any = new InFilter(path, [.. value.EnumerateArray().Select(item => Scalar(item))]);
                return member.Name == "in" ? any : new NotFilter(any);
            case "prefix":
                return value.ValueKind == JsonValueKind.String
                    ? new PrefixFilter(path, value.GetString()!)
                    : throw Invalid("prefix takes [path, string]");
            case "contains":
                return new ContainsFilter(path, Scalar(value));
            case "within":
                return path.IsId ? throw Invalid("within takes a JSON Pointer") : Within(path, value);
            case "search":
                if (path.IsId || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                    || Encoding.UTF8.GetByteCount(value.GetString()!) > 4096 || value.GetString()!.Contains('\0', StringComparison.Ordinal))
                {
                    throw Invalid("search takes [JSON Pointer, nonempty terms of at most 4096 UTF-8 bytes without NUL]");
                }

                var nesting = 0;
                foreach (var character in value.GetString()!)
                {
                    if (character == '(' && ++nesting > 64)
                    {
                        throw Invalid("search terms nest at most 64 parentheses");
                    }

                    if (character == ')')
                    {
                        nesting = Math.Max(0, nesting - 1);
                    }
                }

                return new SearchFilter(path, value.GetString()!);
            default:
                throw Invalid("unknown filter operator");
        }
    }

    private static GeoPoint Point(double? lat, double? lon) => lat is >= -90 and <= 90 && lon is >= -180 and <= 180
        ? new GeoPoint(lat.Value, lon.Value)
        : throw Invalid("lat is -90 to 90 and lon is -180 to 180");

    // {"lat","lon","meters"} is a circle; {"south","west","north","east"} a box, crossing the antimeridian when west > east.
    private static WithinFilter Within(DocumentPath path, JsonElement area)
    {
        double? Number(string name)
        {
            return area.ValueKind == JsonValueKind.Object && area.TryGetProperty(name, out var member)
                && member.ValueKind == JsonValueKind.Number && member.TryGetDouble(out var number) ? number : null;
        }

        if (area.ValueKind == JsonValueKind.Object && area.GetPropertyCount() == 4)
        {
            var (south, west, north, east) = (Number("south"), Number("west"), Number("north"), Number("east"));
            Point(south, west);
            Point(north, east);
            return south <= north ? new WithinFilter(path, south!.Value, west!.Value, north!.Value, east!.Value)
                : throw Invalid("within box has south <= north");
        }

        if (area.ValueKind != JsonValueKind.Object || area.GetPropertyCount() != 3 || Number("meters") is not { } meters || meters is not (> 0 and <= 20_000_000))
        {
            throw Invalid("within takes [path, {lat, lon, meters (0 to 20000000)}] or [path, {south, west, north, east}]");
        }

        // The circle's bounding box: latitude spans meters along a meridian; longitude widens toward the poles,
        // and a box touching a pole or crossing the antimeridian keeps every longitude.
        var center = Point(Number("lat"), Number("lon"));
        var degrees = meters / GeoDistance.EarthRadius * 180 / Math.PI;
        var (low, high) = (center.Lat - degrees, center.Lat + degrees);
        var span = low <= -90 || high >= 90 ? 180
            : Math.Asin(Math.Min(1, Math.Sin(meters / GeoDistance.EarthRadius) / Math.Cos(center.Lat * Math.PI / 180))) * 180 / Math.PI;
        var (minLon, maxLon) = center.Lon - span < -180 || center.Lon + span > 180 ? (-180.0, 180.0) : (center.Lon - span, center.Lon + span);
        return new WithinFilter(path, Math.Max(-90, low), minLon, Math.Min(90, high), maxLon, center, meters);
    }

    internal static IEnumerable<SearchFilter> Searches(DocumentFilter? filter) => Leaves(filter).OfType<SearchFilter>();

    internal static IEnumerable<DocumentFilter> Leaves(DocumentFilter? filter) => filter switch
    {
        null => [],
        AndFilter and => and.Children.SelectMany(Leaves),
        OrFilter or => or.Children.SelectMany(Leaves),
        NotFilter not => Leaves(not.Child),
        _ => [filter],
    };

    private static DocumentPath Path(JsonElement path)
    {
        if (path.ValueKind == JsonValueKind.Object && path.TryGetProperty("lower", out _))
        {
            throw Invalid("lower is not supported yet");
        }

        if (path.ValueKind == JsonValueKind.String && path.GetString() == "$id")
        {
            return DocumentPath.Id;
        }

        return new DocumentPath(ParsePointer(path.ValueKind == JsonValueKind.String ? path.GetString() : null, out var tooDeep)
            ?? throw Invalid(tooDeep ? TooDeep : "a path is \"$id\" or a JSON Pointer of 1 to 16 segments"));
    }

    private static ScalarValue Scalar(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return new ScalarValue(ScalarKind.Null, null);
            case JsonValueKind.True or JsonValueKind.False:
                return new ScalarValue(ScalarKind.Boolean, value.GetBoolean());
            case JsonValueKind.String:
                return new ScalarValue(ScalarKind.Text, value.GetString());
            case JsonValueKind.Number:
                if (RequestJson.CheckValue(value, Code) is Validation limits)
                {
                    throw Invalid(limits.Message);
                }

                if (value.TryGetInt64(out var integer))
                {
                    return new ScalarValue(ScalarKind.Number, integer);
                }

                return value.TryGetDouble(out var real) && double.IsFinite(real)
                    ? new ScalarValue(ScalarKind.Number, real)
                    : throw Invalid("a number is out of range");
            default:
                throw Invalid("values are JSON scalars");
        }
    }

    private static string Normalize(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "" : JsonSerializer.Serialize(element, TerraBaseJsonContext.Default.JsonElement);

    // The translation rejects a node from deep in the filter tree; the Check… entry points catch it and answer the rule
    // with their code, so it never leaves this class.
    private static FormatException Invalid(string message) => new(message);
}
