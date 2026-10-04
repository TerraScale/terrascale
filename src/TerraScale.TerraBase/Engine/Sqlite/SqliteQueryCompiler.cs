using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// Compiles a <see cref="DocumentQuery"/> into one SQLite statement over the <c>documents</c> table,
/// so filtering, ordering, keyset pagination and the limit all run inside SQLite (<c>json_type</c>,
/// <c>json_extract</c>, <c>json_each</c>). Every client value, including paths, is a bound parameter.
/// </summary>
/// <remarks>
/// Every predicate evaluates to 0 or 1, never NULL, so <c>NOT</c> stays a plain complement:
/// type tests use <c>IS</c>, and value comparisons run only after a matching type test.
/// ponytail: no secondary indexes yet, so filters and sorts scan the collection's primary-key range;
/// expression indexes belong to the Indexing slice.
/// </remarks>
internal sealed class SqliteQueryCompiler(SqliteParameterCollection parameters)
{
    // Sort rank by JSON type: missing < null < false < true < numbers < strings < arrays/objects.
    private const string RankCases =
        "WHEN 'null' THEN 1 WHEN 'false' THEN 2 WHEN 'true' THEN 3 WHEN 'integer' THEN 4 WHEN 'real' THEN 4 " +
        "WHEN 'text' THEN 5 WHEN 'array' THEN 6 WHEN 'object' THEN 6 ELSE 0 END";

    private readonly Dictionary<string, string> _paths = [];

    /// <summary>
    /// Columns: id, version, body, JSON byte length, then per sort key its type rank and scalar value.
    /// One extra row beyond the limit tells whether another page exists.
    /// </summary>
    public string Compile(DocumentQuery query)
    {
        var keys = new List<(string Expression, bool Descending)>();
        var columns = new StringBuilder();
        foreach (var (key, index) in query.Sort.Select((key, index) => (key, index)))
        {
            columns.Append(CultureInfo.InvariantCulture, $", {Rank(key.Path)} AS k{index}r, {Scalar(key.Path)} AS k{index}v");
            keys.Add(($"k{index}r", key.Descending));
            keys.Add(($"k{index}v", key.Descending));
        }

        var leadingId = query.Sort.Count > 0 && query.Sort[0].Path.IsId;
        var after = leadingId && query.After is { } position
            ? $"id {(query.Sort[0].Descending ? "<" : ">")} {Parameter(position.Id)}"
            : After(keys, query.After);
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"""
            SELECT * FROM (
                SELECT id, version, body, length(CAST(body AS BLOB)) AS size{columns}
                FROM documents INDEXED BY documents_live WHERE collection = $collection AND body IS NOT NULL AND {Filter(query.Filter)}
            ) WHERE {after}
            ORDER BY
            """);
        if (leadingId)
        {
            // Ids are unique within a collection: later sort keys cannot break a tie. Ordering by the
            // constant type rank would force a full sorter; ordering only by id uses the live index.
            sql.Append(CultureInfo.InvariantCulture, $" id {(query.Sort[0].Descending ? "DESC" : "ASC")} LIMIT {Parameter((long)query.Limit + 1)};");
            return sql.ToString();
        }

        foreach (var (expression, descending) in keys)
        {
            sql.Append(CultureInfo.InvariantCulture, $" {expression} {(descending ? "DESC" : "ASC")},");
        }

        sql.Append(CultureInfo.InvariantCulture, $" id ASC LIMIT {Parameter((long)query.Limit + 1)};");
        return sql.ToString();
    }

    /// <summary>
    /// Columns: rows examined and live matches among them. The LIMIT sits in the scan, ahead of the filter and of the
    /// tombstone test. A separate bounded index scan detects an overflow row without loading its body or
    /// evaluating its filter: only the first <paramref name="maxExamined"/> rows contribute matches.
    /// </summary>
    public string CompileCount(DocumentFilter? filter, long maxExamined) =>
        $"""
        SELECT count(*) + EXISTS (
            SELECT 1 FROM documents WHERE collection = $collection ORDER BY id LIMIT 1 OFFSET {Parameter(maxExamined)}
        ), coalesce(sum(matched), 0) FROM (
            SELECT CASE WHEN body IS NOT NULL THEN {Filter(filter)} ELSE 0 END AS matched
            FROM documents WHERE collection = $collection ORDER BY id LIMIT {Parameter(maxExamined)}
        );
        """;

    private string Filter(DocumentFilter? filter) => filter switch
    {
        null => "1",
        AndFilter and => "(" + string.Join(" AND ", and.Children.Select(Filter)) + ")",
        OrFilter or => "(" + string.Join(" OR ", or.Children.Select(Filter)) + ")",
        NotFilter not => $"NOT {Filter(not.Child)}",
        CompareFilter compare => Compare(compare),
        InFilter @in => In(@in),
        ExistsFilter exists => exists.Path.IsId ? "1" : $"({Type(exists.Path)} IS NOT NULL)",
        PrefixFilter prefix => Prefix(prefix),
        ContainsFilter contains => Contains(contains),
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.GetType().Name, "unknown filter"),
    };

    private string Compare(CompareFilter compare)
    {
        var path = compare.Path;
        var value = compare.Value;
        var op = compare.Op switch
        {
            Comparison.Equal => "=",
            Comparison.Less => "<",
            Comparison.LessOrEqual => "<=",
            Comparison.Greater => ">",
            Comparison.GreaterOrEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(compare), compare.Op, "unknown comparison"),
        };
        return value.Kind switch
        {
            // Booleans order false < true, so compare them as 0 and 1.
            ScalarKind.Null => $"({Type(path)} IS 'null')",
            ScalarKind.Boolean when compare.Op == Comparison.Equal => $"({Type(path)} IS '{((bool)value.Value! ? "true" : "false")}')",
            ScalarKind.Boolean => $"(coalesce({Type(path)} IN ('true', 'false'), 0) AND ({Type(path)} IS 'true') {op} {Parameter((bool)value.Value! ? 1L : 0L)})",
            ScalarKind.Number => $"(coalesce({Type(path)} IN ('integer', 'real'), 0) AND {Extract(path)} {op} {Parameter(value.Value!)})",
            _ => $"({Type(path)} IS 'text' AND {Extract(path)} {op} {Parameter(value.Value!)})",
        };
    }

    // One parameter for any number of values: a JSON array read with json_each, matched per JSON type.
    private string In(InFilter filter)
    {
        var values = Parameter(JsonArray(filter.Values));
        var path = filter.Path;
        var literals = $"coalesce({Type(path)} IN (SELECT type FROM json_each({values}) WHERE type IN ('null', 'true', 'false')), 0)";
        var numbers = $"(coalesce({Type(path)} IN ('integer', 'real'), 0) AND {Extract(path)} IN (SELECT value FROM json_each({values}) WHERE type IN ('integer', 'real')))";
        var texts = $"({Type(path)} IS 'text' AND {Extract(path)} IN (SELECT value FROM json_each({values}) WHERE type = 'text'))";
        return $"({literals} OR {numbers} OR {texts})";
    }

    // Byte-wise on BLOBs: text length() and substr() stop at an embedded NUL.
    private string Prefix(PrefixFilter prefix)
    {
        var parameter = Parameter(Encoding.UTF8.GetBytes(prefix.Prefix));
        return $"({Type(prefix.Path)} IS 'text' AND substr(CAST({Extract(prefix.Path)} AS BLOB), 1, length({parameter})) = {parameter})";
    }

    private static string JsonArray(IReadOnlyList<ScalarValue> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                switch (value.Value)
                {
                    case bool flag:
                        writer.WriteBooleanValue(flag);
                        break;
                    case long integer:
                        writer.WriteNumberValue(integer);
                        break;
                    case double real:
                        writer.WriteNumberValue(real);
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

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private string Contains(ContainsFilter contains)
    {
        if (contains.Path.IsId)
        {
            return "0";
        }

        var value = contains.Value;
        var element = value.Kind switch
        {
            ScalarKind.Null => "element.type = 'null'",
            ScalarKind.Boolean => $"element.type = '{((bool)value.Value! ? "true" : "false")}'",
            ScalarKind.Number => $"element.type IN ('integer', 'real') AND element.value = {Parameter(value.Value!)}",
            _ => $"element.type = 'text' AND element.value = {Parameter(value.Value!)}",
        };
        return $"({Type(contains.Path)} IS 'array' AND EXISTS (SELECT 1 FROM json_each(body, {Path(contains.Path)}) AS element WHERE {element}))";
    }

    private string Rank(DocumentPath path) => path.IsId ? "5" : $"CASE {Type(path)} {RankCases}";

    private string Scalar(DocumentPath path) =>
        path.IsId ? "id" : $"CASE WHEN {Type(path)} IN ('integer', 'real', 'text') THEN {Extract(path)} END";

    // Rows strictly after the position in (keys..., id ASC) order. A NULL sort value only meets
    // another NULL of the same rank, where IS treats them as equal and ">" as not greater.
    private string After(List<(string Expression, bool Descending)> keys, QueryPosition? after)
    {
        if (after is null)
        {
            return "1";
        }

        var values = after.Keys.Select(value => value is null ? "NULL" : Parameter(value)).ToList();
        var branches = new List<string>();
        for (var i = 0; i <= keys.Count; i++)
        {
            var equal = Enumerable.Range(0, i).Select(j => $"{keys[j].Expression} IS {values[j]}");
            var strict = i == keys.Count
                ? $"id > {Parameter(after.Id)}"
                : $"{keys[i].Expression} {(keys[i].Descending ? "<" : ">")} {values[i]}";
            branches.Add("(" + string.Join(" AND ", equal.Append(strict)) + ")");
        }

        return "(" + string.Join(" OR ", branches) + ")";
    }

    private string Type(DocumentPath path) => path.IsId ? "'text'" : $"json_type(body, {Path(path)})";

    private string Extract(DocumentPath path) => path.IsId ? "id" : $"json_extract(body, {Path(path)})";

    // Each segment is a quoted object label, so a numeric segment is a member name, never an array index.
    private string Path(DocumentPath path)
    {
        var text = new StringBuilder("$");
        foreach (var segment in path.Segments!)
        {
            // A JSON string, control characters escaped too: SQLite decodes JSON escapes in quoted labels, while a
            // raw NUL would end the bound path text.
            text.Append(".\"").Append(JsonEncodedText.Encode(segment, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value).Append('"');
        }

        var key = text.ToString();
        if (!_paths.TryGetValue(key, out var name))
        {
            name = Parameter(key);
            _paths[key] = name;
        }

        return name;
    }

    private string Parameter(object value)
    {
        var name = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        // SQLitePCL binds long strings through a NUL-terminated representation. Blob binding
        // carries an explicit length; cast it back to TEXT for the same comparison semantics.
        if (value is string text && text.Contains('\0', StringComparison.Ordinal))
        {
            parameters.AddWithValue(name, Encoding.UTF8.GetBytes(text));
            return $"CAST({name} AS TEXT)";
        }

        parameters.AddWithValue(name, value);
        return name;
    }
}
