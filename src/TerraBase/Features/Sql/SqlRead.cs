using System.Globalization;
using System.Text;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using SqlParser.Tokens;
using TerraBase.Engine;

namespace TerraBase.Features.Sql;

/// <summary><c>SELECT</c>, <c>VALUES</c> or <c>WITH … SELECT</c>: one engine read.</summary>
/// <param name="Query">The validated query; its placeholders are <c>$p1</c>, <c>$p2</c>, ….</param>
/// <param name="Tables">SQL tables it reads (every table reference that is not one of its CTEs), lowercased.</param>
internal sealed record ReadSql(Query Query, IReadOnlyList<string> Tables) : ParsedSql
{
    public override bool IsWrite => false;
}

/// <summary>Parameter numbering, read validation and the engine program of a read.</summary>
internal static class SqlRead
{
    public const int MaxParameters = 1024;
    public const int MaxRows = 10_000;
    public const long MaxBytes = 8 * 1024 * 1024;

    // Core scalar, aggregate, window, date/time, math and JSON functions of SQLite. Table-valued functions, blobs,
    // connection state and the engine's own tb_ functions are absent.
    private static readonly HashSet<string> Functions =
    [
        with(StringComparer.Ordinal),
        "abs", "char", "coalesce", "concat", "concat_ws", "format", "glob", "hex", "ifnull", "iif", "instr", "length", "like",
        "likelihood", "likely", "lower", "ltrim", "max", "min", "nullif", "octet_length", "printf", "quote", "random", "replace",
        "round", "rtrim", "sign", "soundex", "substr", "substring", "trim", "typeof", "unicode", "unlikely", "upper",
        "avg", "count", "group_concat", "string_agg", "sum", "total",
        "row_number", "rank", "dense_rank", "percent_rank", "cume_dist", "ntile", "lag", "lead", "first_value", "last_value", "nth_value",
        "date", "time", "datetime", "julianday", "unixepoch", "strftime", "timediff",
        "acos", "asin", "atan", "atan2", "ceil", "ceiling", "cos", "exp", "floor", "ln", "log", "log10", "log2", "mod", "pi", "pow",
        "power", "sin", "sqrt", "tan", "trunc",
        "json", "json_array", "json_array_length", "json_extract", "json_insert", "json_object", "json_patch", "json_remove",
        "json_replace", "json_set", "json_type", "json_valid", "json_quote", "json_group_array", "json_group_object",
    ];

    /// <summary>
    /// Renames every parameter <c>$pN</c>, N being SQLite's number for it (https://sqlite.org/lang_expr.html#varparam): a
    /// <c>?</c> takes the largest number so far plus one, <c>?NNN</c> takes NNN, a name takes the next number the first time
    /// it appears. Returns the names in number order: <c>?NNN</c>, <c>:a</c>, <c>@a</c> or <c>$a</c>, or null for <c>?</c>.
    /// </summary>
    public static (string Sql, List<string?> Names) Number(string sql)
    {
        var tokens = new Tokenizer().Tokenize(sql, new SQLiteDialect());
        var names = new List<string?>();
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var text = new StringBuilder(sql.Length + 16);
        for (var i = 0; i < tokens.Count; i++)
        {
            string? name;
            switch (tokens[i])
            {
                case Placeholder { Value: "?" }:
                    name = null;
                    break;
                case Placeholder { Value: ['?', .. var digits] }:
                    name = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= MaxParameters
                        ? "?" + number.ToString(CultureInfo.InvariantCulture)
                        : throw new FormatException("?NNN takes a number from 1 to 1024");
                    break;
                case Placeholder { Value: var placeholder }:
                    name = placeholder;
                    break;
                case Colon or AtSign when i + 1 < tokens.Count && tokens[i + 1] is Word { QuoteStyle: null } word:
                    name = (tokens[i] is Colon ? ":" : "@") + word.Value;
                    i++;
                    break;
                default:
                    text.Append(tokens[i]);
                    continue;
            }

            int index;
            if (name is ['?', .. var position])
            {
                index = int.Parse(position, CultureInfo.InvariantCulture);
            }
            else if (name is null || !numbers.TryGetValue(name, out index))
            {
                index = names.Count + 1;
                if (name is not null)
                {
                    numbers.Add(name, index);
                }
            }

            if (index > MaxParameters)
            {
                throw new FormatException("a statement takes at most 1024 parameters");
            }

            while (names.Count < index)
            {
                names.Add(null);
            }

            names[index - 1] ??= name;
            text.Append("$p").Append(index.ToString(CultureInfo.InvariantCulture));
        }

        return (text.ToString(), names);
    }

    /// <summary>Validates a query: table references, functions and top-level clauses.</summary>
    public static ReadSql Read(Query query)
    {
        if (query != new Query(query.Body) { With = query.With, OrderBy = query.OrderBy, Limit = query.Limit, Offset = query.Offset })
        {
            throw new FormatException("this query clause is not supported");
        }

        // Rows a recursive CTE generates pass no table scan, so the work budget would never see them.
        if (query.With is { Recursive: true })
        {
            throw new FormatException("WITH RECURSIVE is not supported");
        }

        var validator = new Validator();
        ((IElement)query).Visit(validator);
        var ctes = query.With?.CteTables.Select(cte => cte.Alias.Name.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal) ?? [];
        return new ReadSql(query, [.. validator.Tables.Where(table => !ctes.Contains(table)).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The engine program for <paramref name="read"/> over <paramref name="tables"/>; when <paramref name="describe"/>, a
    /// program that returns its columns and no rows.
    /// </summary>
    public static SqlProgram Program(ReadSql read, IReadOnlyDictionary<string, SqlTableDefinition> tables, IReadOnlyList<object?> parameters, bool describe)
    {
        var statement = (read.Query with { With = null }).ToSql();
        return new SqlProgram(
            [.. read.Tables.Select(name => new SqlProgramTable(name, [.. tables[name].Columns.Select(column => column.Name)]))],
            read.Query.With is { } with ? string.Join(", ", with.CteTables.Select(cte => cte.ToSql())) : null,
            describe ? $"SELECT * FROM ({statement}) LIMIT 0" : statement,
            parameters, MaxRows, MaxBytes);
    }

    private sealed class Validator : Visitor
    {
        public List<string> Tables { get; } = [];

        public override ControlFlow PreVisitTableFactor(TableFactor relation)
        {
            switch (relation)
            {
                case TableFactor.Table { Args: null, WithHints: null or [], Version: null, Partitions: null or [], WithOrdinality: false } table:
                    Tables.Add(SqlParse.TableName(table.Name));
                    break;
                case TableFactor.Table:
                    throw new FormatException("table-valued functions and table options are not supported");
                case TableFactor.Derived { SubQuery.With: not null }:
                    throw new FormatException("WITH is supported only at the start of a statement");
                case TableFactor.Derived or TableFactor.NestedJoin:
                    break;
                default:
                    throw new FormatException("this kind of FROM item is not supported");
            }

            return ControlFlow.Continue;
        }

        public override ControlFlow PreVisitExpression(Expression expression)
        {
            switch (expression)
            {
                case Expression.Function { Name.Values: [var name] } when Functions.Contains(name.Value.ToLowerInvariant()):
                    break;
                case Expression.Function:
                    throw new FormatException("a function the statement calls is not supported");
                case Expression.Subquery { Query.With: not null } or Expression.Exists { SubQuery.With: not null } or Expression.InSubquery { SubQuery.With: not null }:
                    throw new FormatException("WITH is supported only at the start of a statement");
                case Expression.CompoundIdentifier { Idents.Count: > 2 }:
                    throw new FormatException("columns are named c or t.c");
                case Expression.LiteralValue { Value: Value.HexStringLiteral }:
                    throw new FormatException("blob values are not supported");
            }

            return ControlFlow.Continue;
        }
    }
}
