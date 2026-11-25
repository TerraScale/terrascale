using System.Buffers;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TerraScale.Platform.Features.Shapes;

public readonly union ParseShapeQueryOutcome(ShapeQuery, PlatformInvalidRequest);

public readonly union ShapeSnapshotOutcome(ShapeSnapshot, PlatformUnavailable);

public sealed partial record ShapeQuery(string Table, string? Where, string? Columns)
{
    public const int MaxWhere = 4096;
    public const int MaxColumns = 2048;
    public const int MaxRows = 50_000;
    public const string KeyColumn = "__tb_key";

    public static readonly PlatformInvalidRequest BadTable = new("table must be an ordinary table name of at most 63 letters, digits and underscores");

    public static readonly PlatformInvalidRequest BadFilter = new(
        $"where and columns are each at most once, non-empty, where at most {MaxWhere} and columns at most {MaxColumns} characters");

    public static readonly PlatformInvalidRequest UnsafeWhere = new(
        "where may use this table's columns, literals, operators, CASE, CAST, COLLATE and the functions abs, coalesce, ifnull, iif, nullif, length, "
        + "octet_length, lower, upper, trim, ltrim, rtrim, substr, substring, instr, round, ceil, ceiling, floor, sign, typeof, min, max, likely, "
        + "unlikely, likelihood, like, glob, date, time, datetime, julianday, unixepoch, strftime, json_extract, json_type, json_valid and "
        + "json_array_length; no subqueries, parameters or comments",
        "unsafe_where");

    private static readonly string[] ForbiddenWords =
    [
        "select", "from", "union", "intersect", "except", "join", "with", "insert", "update", "delete", "drop", "create", "alter",
        "attach", "detach", "pragma", "vacuum", "values", "returning", "window", "over", "limit", "offset", "order", "group",
        "having", "reindex", "analyze", "random", "randomblob", "load_extension", "exists", "raise", "regexp", "match",
    ];

    private static readonly FrozenSet<string> AllowedFunctions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "abs", "coalesce", "ifnull", "iif", "nullif", "length", "octet_length", "lower", "upper", "trim", "ltrim", "rtrim", "substr", "substring",
        "instr", "round", "ceil", "ceiling", "floor", "sign", "typeof", "min", "max", "likely", "unlikely", "likelihood", "like", "glob",
        "date", "time", "datetime", "julianday", "unixepoch", "strftime", "json_extract", "json_type", "json_valid", "json_array_length");

    private static readonly FrozenSet<string> CallKeywords = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase, "and", "or", "not", "in", "is", "cast", "case", "when", "then", "else", "between", "like", "glob", "escape");

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    private static readonly SearchValues<string> ForbiddenWordSet = SearchValues.Create(ForbiddenWords, StringComparison.OrdinalIgnoreCase);

    public static ParseShapeQueryOutcome From(string table, IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsTable(table))
        {
            return BadTable;
        }

        string? where = null;
        string? columns = null;
        if (query.TryGetValue("where", out var wheres))
        {
            if (wheres.Count != 1 || wheres[0] is not { Length: > 0 and <= MaxWhere } text)
            {
                return BadFilter;
            }

            where = text;
        }

        if (query.TryGetValue("columns", out var lists))
        {
            if (lists.Count != 1 || lists[0] is not { Length: > 0 and <= MaxColumns } text)
            {
                return BadFilter;
            }

            columns = text;
        }

        if (where is not null && !IsSafeWhere(where))
        {
            return UnsafeWhere;
        }

        if (columns is not null && ColumnList(columns) is null)
        {
            return new PlatformInvalidRequest("columns must be a comma-separated list of column names");
        }

        return new ShapeQuery(table, where, columns);
    }

    public static bool IsSafeFilter(string? where) => where is null || (where.Length is > 0 and <= MaxWhere && IsSafeWhere(where));

    public static bool IsTable(string? table) =>
        table is { Length: > 0 and <= 63 } && !char.IsAsciiDigit(table[0]) && table.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_')
        && !table.StartsWith("_tb_", StringComparison.OrdinalIgnoreCase) && !table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string>? ColumnList(string columns)
    {
        var names = columns.Split(',', StringSplitOptions.TrimEntries);
        return names.Length is > 0 and <= 128 && names.All(static name => ColumnPattern().IsMatch(name)) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length
            ? names
            : null;
    }

    public string QueryString() =>
        (Where is { } where ? "&where=" + Uri.EscapeDataString(where) : "") + (Columns is { } columns ? "&columns=" + Uri.EscapeDataString(columns) : "");

    public SqlHttpStatement Select()
    {
        var projection = Columns is { } list && ColumnList(list) is { } names ? string.Join(", ", names.Select(Quote)) : "*";
        var sql = $"SELECT rowid AS {KeyColumn}, {projection} FROM {Quote(Table)}" + (Where is { } where ? $" WHERE ({where})" : "") + $" ORDER BY rowid LIMIT {MaxRows + 1}";
        return SqlValues.Statement(sql);
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public bool IncludesRowid =>
        Columns is { } list && ColumnList(list)?.Any(static name => string.Equals(name, "rowid", StringComparison.OrdinalIgnoreCase)) == true;

    public static ShapeSnapshotOutcome Snapshot(SqlHttpStatementResult result, bool includeRowid = false)
    {
        if (result.Rows.Count > MaxRows)
        {
            return new PlatformUnavailable($"the shape holds more than {MaxRows} rows", "shape_too_large");
        }

        if (result.Columns.Count == 0 || result.Columns[0].Name != KeyColumn)
        {
            return new PlatformUnavailable("TerraBase answered a shape query without its key column", "terrabase_protocol");
        }

        var skip = !includeRowid && result.Columns.Count > 2 && string.Equals(result.Columns[1].Name, "rowid", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var columns = result.Columns.Skip(skip).Select(static column => column.Name).ToArray();
        var rows = result.RowsOf().Select(row => new ShapeRowEntry(row.Text(0), RowObject(columns, row, skip))).ToArray();
        return new ShapeSnapshot(columns, rows, Digest(columns, rows));
    }

    public static JsonElement RowObject(IReadOnlyList<string> columns, SqlRow row, int skip)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            for (var index = 0; index < columns.Count; index++)
            {
                writer.WritePropertyName(columns[index]);
                row.Raw(index + skip).WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenSpan, PlatformJsonContext.Default.JsonElement);
    }

    public static string Digest(IReadOnlyList<string> columns, IReadOnlyList<ShapeRowEntry> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(string.Join('\0', columns) + "\n"));
        foreach (var row in rows)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(row.Key + "\0" + row.Row.GetRawText() + "\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public (string DerivedHandle, string ShapeId) Lineage(Ulid organization, Ulid project, Ulid database, string viewerKey, IReadOnlyList<string> columns)
    {
        var fields = string.Join('\0', organization, project, database, Table, Encoded(Where), Encoded(Columns), viewerKey, string.Join(',', columns));
        return ("h1-" + Hash("TB-SHAPE-HANDLE-V4", fields, 8), Hash("TB-SHAPE-V4", fields, 16));
    }

    public static string NewHandle() => "h1-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    private static string Encoded(string? value) => value is null ? "-" : "=" + value;

    public static bool IsHandle(string? handle) =>
        handle is { Length: 19 } && handle.StartsWith("h1-", StringComparison.Ordinal) && !handle.AsSpan(3).ContainsAnyExcept(LowerHex);

    private static string Hash(string domain, string fields, int bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(domain + '\0' + fields)).AsSpan(0, bytes));

    private static bool IsSafeWhere(string where)
    {
        if (where.Contains(';', StringComparison.Ordinal) || where.Contains("--", StringComparison.Ordinal) || where.Contains("/*", StringComparison.Ordinal)
            || where.Contains('?', StringComparison.Ordinal) || where.Any(char.IsControl))
        {
            return false;
        }

        var depth = 0;
        var index = 0;
        var typeName = false;
        while (index < where.Length)
        {
            var character = where[index];
            if (character == ' ')
            {
                index++;
                continue;
            }

            if (character is '\'' or '"')
            {
                typeName = false;
                if (QuotedEnd(where, index) is not { } end)
                {
                    return false;
                }

                var name = where[(index + 1)..(end - 1)];
                index = end;
                if (Calls(where, index) && (character == '\'' || !AllowedFunctions.Contains(name.Replace("\"\"", "\"", StringComparison.Ordinal))))
                {
                    return false;
                }

                continue;
            }

            if (char.IsAsciiLetterOrDigit(character) || character == '_')
            {
                var start = index;
                while (index < where.Length && (char.IsAsciiLetterOrDigit(where[index]) || where[index] == '_' || (where[index] == '.' && char.IsAsciiDigit(where[start]))))
                {
                    index++;
                }

                var word = where[start..index];
                var calls = Calls(where, index);
                if (ForbiddenWordSet.Contains(word) || (calls && !typeName && !AllowedFunctions.Contains(word) && !CallKeywords.Contains(word))
                    || (!calls && string.Equals(word, "in", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                typeName = string.Equals(word, "as", StringComparison.OrdinalIgnoreCase) || (typeName && !calls);
                continue;
            }

            if (!char.IsAscii(character) || character is '[' or '`' or ':' or '@' or '$' or '#')
            {
                return false;
            }

            typeName = false;
            depth += character switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth < 0)
            {
                return false;
            }

            index++;
        }

        return depth == 0;
    }

    private static int? QuotedEnd(string text, int open)
    {
        var quote = text[open];
        var index = open + 1;
        while (index < text.Length)
        {
            if (text[index] != quote)
            {
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return null;
    }

    private static bool Calls(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && text[index] == '(';
    }

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnPattern();
}

public static class ShapeViewers
{
    public static string? Key(AccessContext access, string? rulesVersion)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!access.Grant.EndUser)
        {
            return "";
        }

        return access.Token is { } token ? "user:" + token.Digest + "|" + rulesVersion : null;
    }
}
