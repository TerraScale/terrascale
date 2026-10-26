using System.Globalization;
using System.Text;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Tokens;
using TerraBase.Engine;

namespace TerraBase.Features.Sql;

/// <summary><c>SELECT</c>, <c>VALUES</c> or <c>WITH … SELECT</c>: one engine read.</summary>
/// <param name="Query">The validated query; its placeholders are <c>$p1</c>, <c>$p2</c>, ….</param>
/// <param name="Tables">SQL tables it reads (every table reference that is not one of its CTEs), lowercased.</param>
internal sealed record ReadSql(Query Query, IReadOnlyList<string> Tables) : ParsedSql
{
    public override bool IsWrite => false;

    /// <summary>Catalog relations it reads (<see cref="SqlCatalog.Relations"/>), defined as CTEs when it runs.</summary>
    public IReadOnlyList<string> Catalog { get; init; } = [];
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
        "abs", "char", "coalesce", "glob", "hex", "ifnull", "iif", "instr", "length", "like",
        "likelihood", "likely", "lower", "ltrim", "max", "min", "nullif", "octet_length", "quote", "random",
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
        // SqlParserCS 0.6.5 uses U+FFFF as EOF and can discard the remaining input during tokenization.
        if (sql.Contains(char.MaxValue))
        {
            throw new FormatException("U+FFFF is not supported in SQL text");
        }

        var tokens = new Tokenizer().Tokenize(sql, new SqlDialect());
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
                // SqlParserCS has no infix GLOB: a GLOB b is read as a SIMILAR TO b, which SqlGuard renders as glob(b, a).
                // SQLite has no SIMILAR TO, so a written one is refused and every SIMILAR TO is a GLOB.
                case Word { QuoteStyle: null, Value: var glob } when glob.Equals("GLOB", StringComparison.OrdinalIgnoreCase) && Next(tokens, i) is not LeftParen:
                    text.Append("SIMILAR TO");
                    continue;
                case Word { QuoteStyle: null, Value: var similar } when similar.Equals("SIMILAR", StringComparison.OrdinalIgnoreCase):
                    throw new FormatException("SIMILAR TO is not supported; use GLOB or LIKE");
                // SQLite's default schema: main.t and main.t.c are t and t.c (Prisma qualifies every name).
                case Word { Value: var schema } when schema.Equals("main", StringComparison.OrdinalIgnoreCase)
                    && i + 2 < tokens.Count && tokens[i + 1] is Period && tokens[i + 2] is Word && (i == 0 || tokens[i - 1] is not Period):
                    i++;
                    continue;
                // ORMs (Drizzle, Prisma) quote names with backticks, drizzle-kit and SQLAlchemy also with [ ]: one without
                // quote characters is the same name "quoted".
                case Word { QuoteStyle: '`' or '[', Value: var quoted } when !quoted.Contains(']', StringComparison.Ordinal) && !quoted.Contains('`', StringComparison.Ordinal) && !quoted.Contains('"', StringComparison.Ordinal):
                    text.Append('"').Append(quoted).Append('"');
                    continue;
                // SqlParserCS renders these back without escaping ([a]]b] as [a]b], N'a''b' as N'a'b'), so SQLite would
                // read other text than the validator saw: only plain, "…" and simple `…` or […] names, and only '…' strings, pass.
                case Word { QuoteStyle: not (null or '"') }:
                    throw new FormatException("names are plain, \"quoted\", `quoted` or [quoted] without quote characters");
                case StringToken literal when literal is not SingleQuotedString
                    && (literal.GetType().Name.EndsWith("String", StringComparison.Ordinal) || literal.GetType().Name.Contains("Literal", StringComparison.Ordinal)):
                    throw new FormatException("strings are '…' literals; other string and blob literals are not supported");
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

    // The next token that is not whitespace, or null.
    private static Token? Next(IList<Token> tokens, int i)
    {
        for (var next = i + 1; next < tokens.Count; next++)
        {
            if (tokens[next] is not Whitespace)
            {
                return tokens[next];
            }
        }

        return null;
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

        query = Pragmas(query);
        if (query.With?.CteTables.Count > SqlAffinityResolver.MaxCtes)
        {
            throw SqlAffinityBudget.Limit();
        }

        var ctes = query.With?.CteTables.Select(cte => cte.Alias.Name.Value).ToHashSet(SqlIdentifierComparer.Instance) ?? [];
        var validator = new Validator(ctes);
        ((IElement)query).Visit(validator);
        var tables = validator.Tables.Distinct(StringComparer.Ordinal).ToList();
        Rename(query, [with(ReferenceEqualityComparer.Instance)]);
        SqlGuard.Normalize(query);
        return new ReadSql(query, [.. tables.Where(table => !SqlCatalog.Relations.ContainsKey(table))])
        {
            Catalog = [.. tables.Where(SqlCatalog.Relations.ContainsKey)],
        };
    }

    // Every reference to a catalog relation, at any depth (subqueries in expressions included), names its CTE
    // (SqlCatalog.CteName) and keeps the name it was written with as its alias. SQLite resolves sqlite_master and the
    // pragma tables in a nested scope before a CTE of the same name, so the written name would read the database file's
    // own schema. Set in place, as SqlGuard does, on the freshly parsed tree.
    private static void Rename(object node, HashSet<object> seen)
    {
        if (!seen.Add(node))
        {
            return;
        }

        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType || property.PropertyType == typeof(string))
            {
                continue;
            }

            switch (property.GetValue(node))
            {
                case TableFactor.Table { Args: null, Name.Values: [var name] } table when SqlCatalog.Relations.ContainsKey(name.Value.ToLowerInvariant()) && property.CanWrite:
                    // Both names are a lowercase letter or _, then letters, digits or _ (catalog names, or an alias the tree parsed).
                    var alias = table.Alias?.ToSql() ?? name.Value.ToLowerInvariant();
                    property.SetValue(node, new Parser().ParseSql($"SELECT 1 FROM {SqlCatalog.CteName(name.Value.ToLowerInvariant())} AS {alias}", new SqlDialect())
                        is [Statement.Select { Query.Body: SetExpression.SelectExpression { Select.From: [{ Relation: { } renamed }] } }]
                        ? renamed
                        : throw new FormatException("a catalog reference does not render"));
                    break;
                case System.Collections.IList list:
                    foreach (var item in list)
                    {
                        if (item is not null && item.GetType().Namespace?.StartsWith("SqlParser", StringComparison.Ordinal) == true)
                        {
                            Rename(item, seen);
                        }
                    }

                    break;
                case { } child when child.GetType().Namespace?.StartsWith("SqlParser", StringComparison.Ordinal) == true:
                    Rename(child, seen);
                    break;
            }
        }
    }

    // SQLite's table-valued pragmas filter on their hidden arg column: FROM … pragma_x(e) AS p becomes FROM … pragma_x AS p
    // WHERE p.arg = e, in every SELECT of the statement, its CTEs and derived tables. Only inner joins move a condition
    // to WHERE unchanged.
    private static Query Pragmas(Query query) => query with
    {
        Body = Pragmas(query.Body),
        With = query.With is { } with ? with with { CteTables = [.. with.CteTables.Select(cte => cte with { Query = Pragmas(cte.Query) })] } : null,
    };

    private static SetExpression Pragmas(SetExpression body) => body switch
    {
        SetExpression.SelectExpression select => select with { Select = Pragmas(select.Select) },
        SetExpression.SetOperation operation => operation with { Left = Pragmas(operation.Left), Right = Pragmas(operation.Right) },
        SetExpression.QueryExpression nested => nested with { Query = Pragmas(nested.Query) },
        _ => body,
    };

    private static Select Pragmas(Select select)
    {
        if (select.From is not { } from)
        {
            return select;
        }

        var conditions = new List<Expression>();
        var rewritten = from.Select(item => item with
        {
            Relation = Factor(item.Relation!, inner: true),
            Joins = item.Joins is { } joins
                ? [.. joins.Select(join => join with { Relation = Factor(join.Relation!, join.JoinOperator is JoinOperator.Inner or JoinOperator.CrossJoin) })]
                : null,
        }).ToList();
        if (conditions.Count == 0)
        {
            return select;
        }

        var where = conditions.Aggregate((left, right) => new Expression.BinaryOp(left, BinaryOperator.And, right));
        return select with
        {
            From = [.. rewritten],
            Selection = select.Selection is { } existing ? new Expression.BinaryOp(new Expression.Nested(existing), BinaryOperator.And, where) : where,
        };

        TableFactor Factor(TableFactor factor, bool inner)
        {
            switch (factor)
            {
                case TableFactor.Table { Name.Values: [var name], Args.Arguments: var arguments } table when SqlCatalog.IsPragmaFunction(name.Value.ToLowerInvariant()):
                    if (!inner || arguments is not [FunctionArg.Unnamed { FunctionArgExpression: FunctionArgExpression.FunctionExpression { Expression: { } argument } }])
                    {
                        throw new FormatException("a pragma function takes one argument and joins with JOIN or a comma");
                    }

                    var alias = table.Alias?.Name ?? name;
                    conditions.Add(new Expression.BinaryOp(new Expression.CompoundIdentifier([alias, new Ident("arg")]), BinaryOperator.Eq, new Expression.Nested(argument)));
                    return table with { Args = null };
                case TableFactor.Derived derived:
                    return derived with { SubQuery = Pragmas(derived.SubQuery) };
                default:
                    return factor;
            }
        }
    }

    /// <summary>
    /// The engine program for <paramref name="read"/> over <paramref name="tables"/>; when <paramref name="describe"/>, a
    /// program that returns its columns and no rows.
    /// </summary>
    public static SqlProgram Program(
        ReadSql read, IReadOnlyDictionary<string, SqlTableDefinition> tables, IReadOnlyList<object?> parameters, bool describe, string? keyed = null)
    {
        Query query;
        try
        {
            // Normalize at validation, then copy before introducing guards or comparison-local bound slots: cached
            // statements and transaction replays must keep their original parameter numbers and values.
            query = new Parser().ParseSql(read.Query.ToSql(), new SqlDialect()) is [Statement.Select { Query: var copy }]
                ? copy
                : throw new FormatException("the statement cannot be copied");
            var affinities = SqlGuard.Affinity(query, tables, parameters.Count, keyed);
            SqlGuard.Apply(query, tables);
            parameters = [.. parameters, .. affinities.Select(binding => Bound(binding.Affinity, parameters[binding.Parameter - 1]))];
            // Defense in depth: the text the engine runs must parse back to the same statement.
            var full = query.ToSql();
            if (new Parser().ParseSql(full, new SqlDialect()) is not [Statement.Select { Query: var again }] || again.ToSql() != full)
            {
                throw new FormatException("the statement does not render back to itself");
            }
        }
        catch (Exception error) when (error is ParserException or TokenizeException)
        {
            throw new FormatException("the statement does not render back to itself");
        }

        var statement = (query with { With = null }).ToSql();
        return new SqlProgram(
            [.. read.Tables.Select(name => new SqlProgramTable(name, [.. tables[name].Columns.Select(column => column.Name)], name == keyed, TextKey(tables[name])))],
            query.With is { } with ? string.Join(", ", with.CteTables.Select(cte => cte.ToSql())) : null,
            describe ? $"SELECT * FROM ({statement}) LIMIT 0" : statement,
            parameters, MaxRows, MaxBytes);
    }

    // A single TEXT primary key is the document id verbatim.
    private static string? TextKey(SqlTableDefinition table) =>
        table.PrimaryKey is [var key] && table.Columns.First(column => column.Name == key).Affinity == SqlAffinity.Text ? key : null;

    // A bound parameter converted by the affinity of the column it is compared with.
    private static object? Bound(SqlAffinity affinity, object? value)
    {
        var converted = SqlGuard.ComparisonValue(affinity, value switch
        {
            long integer => SqlValue.Of(integer),
            double real => SqlValue.Of(real),
            string text => SqlValue.Of(text),
            _ => SqlValue.Null,
        });
        return converted.Kind switch
        {
            SqlValueKind.Integer => converted.Integer,
            SqlValueKind.Real => converted.Real,
            SqlValueKind.Text => converted.Text,
            _ => null,
        };
    }

    /// <summary>
    /// Declared types of the result columns that name a table column directly (<c>*</c>, <c>t.*</c>, <c>c</c>, <c>t.c</c>,
    /// aliased or not), as SQLite reports them; null for every other column, or for all when the shape is not a plain SELECT.
    /// </summary>
    public static string?[] DeclTypes(ReadSql read, IReadOnlyDictionary<string, SqlTableDefinition> tables, int count)
    {
        var types = new string?[count];
        if (read.Query.Body is not SetExpression.SelectExpression { Select: { From: { } from } select })
        {
            return types;
        }

        // Each FROM table by its alias (or name), in order; derived tables and CTEs have no declared types.
        var sources = new List<(string Alias, SqlTableDefinition? Table)>();
        foreach (var factor in from.SelectMany(join => (join.Joins ?? []).Select(item => item.Relation).Prepend(join.Relation)))
        {
            var table = factor is TableFactor.Table { Name.Values: [var name] } ? SqlIdentifierComparer.LowerAscii(name.Value) : null;
            var alias = factor?.Alias?.Name.Value ?? table;
            sources.Add((alias ?? "", table is not null && read.Tables.Contains(table) ? tables[table] : null));
        }

        var result = new List<string?>();
        foreach (var item in select.Projection)
        {
            switch (item)
            {
                case SelectItem.Wildcard:
                    result.AddRange(sources.SelectMany(source => source.Table?.Columns.Select(Type) ?? [null]));
                    break;
                case SelectItem.QualifiedWildcard { Name.Values: [var qualifier] }:
                    result.AddRange(sources.Where(source => SqlIdentifierComparer.Instance.Equals(source.Alias, qualifier.Value)).SelectMany(source => source.Table?.Columns.Select(Type) ?? [null]));
                    break;
                case SelectItem.UnnamedExpression { Expression: var expression }:
                    result.Add(Column(expression));
                    break;
                case SelectItem.ExpressionWithAlias { Expression: var expression }:
                    result.Add(Column(expression));
                    break;
                default:
                    return types;
            }
        }

        // A source whose columns are unknown (a CTE or subquery under *) makes positions unreliable.
        return result.Count == count ? [.. result] : types;

        string? Column(Expression expression)
        {
            var (qualifier, name) = expression switch
            {
                Expression.Identifier { Ident: var ident } => (null, ident.Value),
                Expression.CompoundIdentifier { Idents: [var table, var ident] } => (table.Value, ident.Value),
                _ => ((string?)null, (string?)null),
            };
            var matches = sources.Where(source => qualifier is null || SqlIdentifierComparer.Instance.Equals(source.Alias, qualifier))
                .Select(source => source.Table?.Columns.FirstOrDefault(column => SqlIdentifierComparer.Instance.Equals(column.Name, name)))
                .Where(column => column is not null).ToList();
            return name is not null && matches.Count == 1 ? Type(matches[0]) : null;
        }

        static string? Type(SqlColumnDefinition? column)
        {
            return column is { Type.Length: > 0 } ? column.Type : null;
        }
    }

    private sealed class Validator(HashSet<string> ctes) : Visitor
    {
        public List<string> Tables { get; } = [];

        public override ControlFlow PreVisitTableFactor(TableFactor relation)
        {
            switch (relation)
            {
                case TableFactor.Table { Args: null, WithHints: null or [], Version: null, Partitions: null or [], WithOrdinality: false, Name.Values: [var cte] }
                    when ctes.Contains(cte.Value):
                    break;
                case TableFactor.Table { Args: null, WithHints: null or [], Version: null, Partitions: null or [], WithOrdinality: false, Name.Values: [var only] }
                    when SqlCatalog.Relations.ContainsKey(only.Value.ToLowerInvariant()):
                    Tables.Add(only.Value.ToLowerInvariant());
                    break;
                case TableFactor.Table { Args: null, WithHints: null or [], Version: null, Partitions: null or [], WithOrdinality: false } table:
                    Tables.Add(SqlParse.TableName(table.Name));
                    break;
                case TableFactor.Table:
                    throw new FormatException("table-valued functions and table options are not supported");
                case TableFactor.Derived { SubQuery.With: not null }:
                    throw new FormatException("WITH is supported only at the start of a statement");
                // The visitor does not enter a FROM subquery: its tables, functions and literals are validated here, or
                // the statement would read the database file's own tables.
                case TableFactor.Derived { SubQuery: var subquery }:
                    ((IElement)subquery).Visit(this);
                    break;
                case TableFactor.NestedJoin:
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
                case Expression.LiteralValue { Value: Value.Number or Value.SingleQuotedString or Value.Null or Value.Boolean or Value.Placeholder }:
                    break;
                case Expression.LiteralValue:
                    throw new FormatException("this literal is not supported");
            }

            return ControlFlow.Continue;
        }
    }
}
