using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TerraBase.Engine.Sqlite;

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
internal sealed class SqliteQueryCompiler(SqliteParameterCollection parameters, bool budgetScans = false)
{
    // Sort rank by JSON type: missing < null < false < true < numbers < strings < arrays/objects.
    private const string RankCases =
        "WHEN 'null' THEN 1 WHEN 'false' THEN 2 WHEN 'true' THEN 3 WHEN 'integer' THEN 4 WHEN 'real' THEN 4 " +
        "WHEN 'text' THEN 5 WHEN 'array' THEN 6 WHEN 'object' THEN 6 ELSE 0 END";

    private readonly Dictionary<string, string> _paths = [];

    /// <summary>
    /// Columns: id, version, JSON byte length, then per sort key its type rank and scalar value.
    /// One extra row beyond the limit tells whether another page exists.
    /// </summary>
    public string Compile(DocumentQuery query, bool observe = false)
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
        // Keep the non-deterministic visit before residual predicates; seek constraints still bound the scan.
        sql.Append(CultureInfo.InvariantCulture, $"""
            SELECT * FROM (
                SELECT id, version, octet_length(body) AS size{columns}
                FROM documents INDEXED BY documents_live WHERE collection = $collection AND {(observe ? "tb_query_scan() AND " : "")}body IS NOT NULL AND {BudgetedFilter(query.Filter)}
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
    public string CompileCount(DocumentFilter? filter, long maxExamined, bool observe = false) =>
        $"""
        SELECT count(*) + EXISTS (
            SELECT 1 FROM documents WHERE collection = $collection {(observe ? "AND tb_query_scan()" : "")} ORDER BY id LIMIT 1 OFFSET {Parameter(maxExamined)}
        ), coalesce(sum(matched), 0) FROM (
            SELECT {BudgetedFilter(filter, includeTombstones: true)} AS matched
            FROM documents WHERE collection = $collection {(observe ? "AND tb_query_scan()" : "")} ORDER BY id LIMIT {Parameter(maxExamined)}
        );
        """;

    /// <summary>
    /// A snapshot at commit <paramref name="offset"/>, streamed in id order after <c>$after</c>: one row per document id
    /// of the collection's generation, from <c>change_log_births</c>. Columns: the id; whether it was first written at
    /// or before the offset; for such an id, the version of its last write at or before the offset, and that write's
    /// body and JSON byte length when it is a live document that matches <paramref name="filter"/> (NULL otherwise).
    /// An id first written later costs its births entry only: its last-write lookup is skipped by a constant-false
    /// term, so its revisions are never read and the filter never runs. The walk stops at the generation's highest
    /// id at the offset (<c>change_log_high</c>), so ids appended later in id order are never visited. The caller
    /// bounds rows read.
    /// </summary>
    public string CompileSnapshot(DocumentFilter? filter, long generation, long offset)
    {
        var at = Parameter(offset);
        var lineage = Parameter(generation);
        return $"""
            SELECT b.id, b.version <= {at}, write.version, CASE WHEN write.matches THEN write.body END, write.size
            FROM change_log_births b
            LEFT JOIN (
                SELECT sequence, document_version AS version, body, octet_length(body) AS size, body IS NOT NULL AND {BudgetedFilter(filter)} AS matches
                FROM change_log
            ) write ON write.sequence = (
                SELECT last.sequence FROM change_log last INDEXED BY change_log_by_document
                WHERE b.version <= {at} AND last.collection = $collection AND last.generation = {lineage} AND last.id = b.id
                    AND last.version <= {at}
                ORDER BY last.version DESC, last.sequence DESC LIMIT 1
            )
            WHERE b.collection = $collection AND b.generation = {lineage} AND b.id > $after
                AND b.id <= (
                    SELECT high.id FROM change_log_high high
                    WHERE high.collection = $collection AND high.generation = {lineage} AND high.version <= {at}
                    ORDER BY high.version DESC, high.id DESC LIMIT 1)
            ORDER BY b.id;
            """;
    }

    /// <summary>
    /// Columns: id, document merge version, body, local history offset. Each document's last write in every commit of the
    /// collection's current generation with a local history offset in
    /// (<paramref name="after"/>, <paramref name="through"/>], in (history offset, write) order; the body is NULL for
    /// tombstones and for documents <paramref name="filter"/> rejects.
    /// </summary>
    public string CompileChanges(DocumentFilter? filter, long after, long through) =>
        $"""
        SELECT id, document_version, CASE WHEN matches THEN body END, version, octet_length(body) FROM (
            SELECT id, version, document_version, body, body IS NOT NULL AND {BudgetedFilter(filter)} AS matches, sequence FROM change_log c
            WHERE collection = $collection AND generation = (SELECT generation FROM collections WHERE name = $collection)
                AND version > {Parameter(after)} AND version <= {Parameter(through)}
                AND sequence = (
                    SELECT max(last.sequence) FROM change_log last INDEXED BY change_log_by_document
                    WHERE last.collection = c.collection AND last.generation = c.generation AND last.id = c.id AND last.version = c.version)
        ) ORDER BY version, sequence;
        """;

    // CASE fixes evaluation order: account a candidate before any JSON predicate parses its body.
    // This counts document candidates, not SQLite VM instructions, index entries or physical disk bytes.
    private string BudgetedFilter(DocumentFilter? filter, bool includeTombstones = false)
    {
        var predicate = includeTombstones ? $"CASE WHEN body IS NOT NULL THEN {Filter(filter)} ELSE 0 END" : Filter(filter);
        return budgetScans ? $"CASE WHEN tb_qos_scan(coalesce(octet_length(body), 0)) THEN {predicate} ELSE 0 END" : predicate;
    }

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
        return filter.Values.All(static value => value.Kind == ScalarKind.Number)
            ? numbers
            : $"({literals} OR {numbers} OR {texts})";
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
