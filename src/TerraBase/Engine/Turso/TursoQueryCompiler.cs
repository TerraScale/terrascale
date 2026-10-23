using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cysharp.Text;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Compiles a <see cref="DocumentQuery"/> into one Turso statement over the live rows of a <see cref="RowSource"/>,
/// so filtering, ordering, keyset pagination and the limit all run inside Turso. Every client value is a bound
/// parameter; a path that is a valid <see cref="DocumentField"/> is embedded as the literal an index keys on.
/// </summary>
/// <remarks>
/// Every predicate evaluates to 0 or 1, never NULL, so <c>NOT</c> stays a plain complement:
/// type tests use <c>IS</c>, and value comparisons run only after a matching type test. Values are read with
/// <c>json_extract(doc, '$.path')</c>, the expression an index keys on, never <c>-&gt;&gt;</c>; <c>json_type</c> only guards
/// the JSON type, as a residual term. An <c>IN</c> list of up to 64 values becomes equalities joined by <c>OR</c>, and a prefix a half-open
/// range, because the Turso planner uses an expression index for neither <c>IN</c> nor a prefix test
/// (docs/terrabase/turso-spike.md). Sorts order by JSON type first, so an index serves filters, not sorts.
/// </remarks>
internal sealed class TursoQueryCompiler(SqliteParameterCollection parameters, RowSource rows)
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
        // The keyset condition repeats the key expressions: it sits in the same WHERE as the scan, not in an outer query,
        // because Turso does not flatten a subquery in FROM and would read the whole collection for every page.
        var keys = new List<(string Expression, bool Descending)>();
        using var columns = ZString.CreateStringBuilder();
        foreach (var (key, index) in query.Sort.Select((key, index) => (key, index)))
        {
            var ordinal = index.ToString(CultureInfo.InvariantCulture);
            var (rank, value) = (Rank(key.Path), Scalar(key.Path));
            columns.AppendFormat(", {0} AS k{1}r, {2} AS k{1}v", rank, ordinal, value);
            keys.Add((rank, key.Descending));
            keys.Add((value, key.Descending));
        }

        var leadingId = query.Sort.Count > 0 && query.Sort[0].Path.IsId;
        var after = leadingId && query.After is { } position
            ? $"id {(query.Sort[0].Descending ? "<" : ">")} {Parameter(position.Id)}"
            : After(keys, query.After);
        if (query.Near is not null)
        {
            // tb_near (NearDistance) is registered for this statement; +Infinity sorts last and means no comparable vector.
            columns.AppendFormat(", tb_near({0}) AS k0v", rows.Json);
        }

        using var sql = ZString.CreateStringBuilder();
        // Keep the non-deterministic visit before residual predicates; seek constraints still bound the scan.
        sql.AppendFormat("""
            SELECT id, {4} AS version, octet_length({5}) AS size{0}
            FROM {6} WHERE {1}{7} AND {2} AND {3}
            ORDER BY
            """, columns.ToString(), observe ? "tb_query_scan(id) AND " : "", ScanGuardedFilter(query.Filter), after, rows.Version, rows.Json, rows.From, rows.Live);
        if (leadingId)
        {
            // Ids are unique within a collection: later sort keys cannot break a tie. Ordering by the
            // constant type rank would force a full sorter; ordering only by id uses the live index.
            sql.AppendFormat(" id {0} LIMIT {1};", query.Sort[0].Descending ? "DESC" : "ASC", Parameter((long)query.Limit + 1));
            return sql.ToString();
        }

        for (var i = 0; i < keys.Count; i++)
        {
            sql.AppendFormat(" k{0}{1} {2},", (i / 2).ToString(CultureInfo.InvariantCulture), i % 2 == 0 ? "r" : "v", keys[i].Descending ? "DESC" : "ASC");
        }

        if (query.Near is not null)
        {
            sql.Append(" k0v ASC,");
        }

        sql.AppendFormat(" id ASC LIMIT {0};", Parameter((long)query.Limit + 1));
        return sql.ToString();
    }

    /// <summary>
    /// Columns: rows examined and live matches among them. The LIMIT sits in the scan, ahead of the filter and of the
    /// tombstone test. A separate bounded index scan detects an overflow row without loading its body or
    /// evaluating its filter: only the first <paramref name="maxExamined"/> rows contribute matches.
    /// </summary>
    public string CompileCount(DocumentFilter? filter, long maxExamined, bool observe = false) => rows == RowSource.System || rows == RowSource.Empty
        ? $"""
        SELECT count(*) + EXISTS (
            SELECT 1 FROM {Counted} {(observe ? "AND tb_query_scan(id)" : "")} ORDER BY id LIMIT 1 OFFSET {Parameter(maxExamined)}
        ), coalesce(sum(matched), 0) FROM (
            SELECT {BudgetedFilter(filter, includeTombstones: true)} AS matched
            FROM {Counted} {(observe ? "AND tb_query_scan(id)" : "")} ORDER BY id LIMIT {Parameter(maxExamined)}
        );
        """
        // A client collection's table holds live documents only: its tombstones are _tb_state rows, so the count examines
        // the collection's state rows, in id order, and looks each one's document up. The caller flushed pending states.
        : $"""
        SELECT count(*) + EXISTS (
            SELECT 1 FROM _tb_state WHERE collection = $collection {(observe ? "AND tb_query_scan(id)" : "")} ORDER BY id LIMIT 1 OFFSET {Parameter(maxExamined)}
        ), coalesce(sum(matched), 0) FROM (
            SELECT {BudgetedFilter(filter, includeTombstones: true)} AS matched
            FROM (SELECT id AS examined FROM _tb_state WHERE collection = $collection ORDER BY id LIMIT {Parameter(maxExamined)})
            LEFT JOIN {rows.From} ON {rows.From}.id = examined {(observe ? "WHERE tb_query_scan(examined)" : "")}
        );
        """;

    // A count examines a system collection's tombstones too.
    private string Counted => rows == RowSource.System ? "documents WHERE collection = $collection" : $"{rows.From} WHERE 1";

    // A plain AND keeps the filter's terms visible to the planner, so an index can seek them; the guard still precedes
    // the residual JSON predicates in Turso's WHERE evaluation. tb_qos_scan also stops the statement once its budget is
    // spent or it is cancelled: Turso has no progress handler.
    private string ScanGuardedFilter(DocumentFilter? filter) => $"tb_qos_scan(coalesce(octet_length({rows.Json}), 0)) AND {Filter(filter)}";

    // CASE fixes evaluation order: account a candidate before any JSON predicate parses its body.
    // This counts document candidates, not SQLite VM instructions, index entries or physical disk bytes.
    private string BudgetedFilter(DocumentFilter? filter, bool includeTombstones = false)
    {
        var predicate = includeTombstones ? $"CASE WHEN {rows.Json} IS NOT NULL THEN {Filter(filter)} ELSE 0 END" : Filter(filter);
        return $"CASE WHEN tb_qos_scan(coalesce(octet_length({rows.Json}), 0)) THEN {predicate} ELSE 0 END";
    }

    private string Filter(DocumentFilter? filter) => filter switch
    {
        null => "1",
        // Balanced: Turso refuses an expression deeper than 100, and a flat chain of 256 terms parses 256 deep.
        AndFilter and => Balanced([.. and.Children.Select(Filter)], " AND "),
        OrFilter or => Balanced([.. or.Children.Select(Filter)], " OR "),
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

    // A list of at most this many values becomes equalities an index can seek. A filter has at most 256 nodes, so this
    // bounds the statement to 16384 such parameters, within Turso's 32766.
    private const int MaxSeekValues = 64;

    // One equality per value, as a balanced OR tree. A longer list is one parameter, a JSON array read with json_each
    // and matched per JSON type, which no planner turns into index seeks.
    private string In(InFilter filter)
    {
        if (filter.Values.Count <= MaxSeekValues)
        {
            return Balanced([.. filter.Values.Select(value => Compare(new CompareFilter(filter.Path, Comparison.Equal, value)))], " OR ");
        }

        var values = Parameter(JsonArray(filter.Values));
        var path = filter.Path;
        var literals = $"coalesce({Type(path)} IN (SELECT type FROM json_each({values}) WHERE type IN ('null', 'true', 'false')), 0)";
        var numbers = $"(coalesce({Type(path)} IN ('integer', 'real'), 0) AND {Extract(path)} IN (SELECT value FROM json_each({values}) WHERE type IN ('integer', 'real')))";
        var texts = $"({Type(path)} IS 'text' AND {Extract(path)} IN (SELECT value FROM json_each({values}) WHERE type = 'text'))";
        return filter.Values.All(static value => value.Kind == ScalarKind.Number)
            ? numbers
            : $"({literals} OR {numbers} OR {texts})";
    }

    /// <summary>
    /// Joins <paramref name="terms"/> with <paramref name="separator"/> as a balanced tree of parentheses, because
    /// Turso nests a flat <c>a OR b OR c</c> one level deeper per term.
    /// </summary>
    internal static string Balanced(IReadOnlyList<string> terms, string separator) => terms.Count == 1
        ? terms[0]
        : $"({Balanced([.. terms.Take(terms.Count / 2)], separator)}{separator}{Balanced([.. terms.Skip(terms.Count / 2)], separator)})";

    // Strings with the prefix are exactly those in [prefix, successor): BINARY collation compares UTF-8 bytes, which order
    // like code points. An empty prefix, or one of U+10FFFF only, has no upper bound.
    private string Prefix(PrefixFilter prefix)
    {
        var value = Extract(prefix.Path);
        var range = $"{value} >= {Parameter(prefix.Prefix)}";
        if (PrefixSuccessor(prefix.Prefix) is { } successor)
        {
            range += $" AND {value} < {Parameter(successor)}";
        }

        return $"({Type(prefix.Path)} IS 'text' AND {range})";
    }

    /// <summary>The least string greater than every string that starts with <paramref name="prefix"/>, if any.</summary>
    internal static string? PrefixSuccessor(string prefix)
    {
        var runes = prefix.EnumerateRunes().ToList();
        while (runes.Count > 0 && runes[^1].Value == 0x10FFFF)
        {
            runes.RemoveAt(runes.Count - 1);
        }

        if (runes.Count == 0)
        {
            return null;
        }

        // The next scalar value: surrogate code points are not runes.
        var next = runes[^1].Value + 1;
        runes[^1] = new Rune(next == 0xD800 ? 0xE000 : next);
        return string.Concat(runes.Select(rune => rune.ToString()));
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
        return $"({Type(contains.Path)} IS 'array' AND EXISTS (SELECT 1 FROM json_each({rows.Json}, {Path(contains.Path)}) AS element WHERE {element}))";
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

    private string Type(DocumentPath path) => path.IsId ? "'text'" : $"json_type({rows.Json}, {Path(path)})";

    private string Extract(DocumentPath path) => path.IsId ? "id" : $"json_extract({rows.Json}, {Path(path)})";

    // A path that is a valid field is the literal an index keys on ('$.a.b', as in CreateIndex); any other is a bound
    // parameter. Each of its segments is a quoted object label, so a numeric segment is a member name, never an array index.
    private string Path(DocumentPath path)
    {
        if (path.Segments!.All(static segment => !segment.Contains('.', StringComparison.Ordinal))
            && DocumentField.TryParse(string.Join('.', path.Segments!), out var field))
        {
            return $"'$.{field.Path}'";
        }

        using var text = ZString.CreateStringBuilder();
        text.Append('$');
        foreach (var segment in path.Segments!)
        {
            // A JSON string, control characters escaped too: Turso decodes JSON escapes in quoted labels, while a
            // raw NUL would end the bound path text.
            text.Append(".\"");
            text.Append(JsonEncodedText.Encode(segment, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value);
            text.Append('"');
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
        // The native binding passes long strings NUL-terminated. Blob binding
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
