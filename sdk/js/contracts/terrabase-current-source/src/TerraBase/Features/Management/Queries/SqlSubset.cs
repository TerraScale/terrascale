using System.Globalization;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Management.Queries;

/// <summary>A statement of the dashboard SQL subset, translated to an engine query.</summary>
/// <param name="Query">The engine query.</param>
/// <param name="Projection">Selected fields in order, without duplicates; <see langword="null"/> for <c>*</c>.</param>
/// <param name="HasLimit">Whether the statement has a <c>LIMIT</c>.</param>
public sealed record SqlStatement(SqlQuery Query, IReadOnlyList<DocumentField>? Projection, bool HasLimit);

/// <summary>
/// Parses the read-only subset <c>SELECT * | field, … FROM collection [WHERE …] [ORDER BY …] [LIMIT n]</c> (<c>WHERE</c>:
/// comparisons, <c>IN</c>, <c>IS [NOT] NULL</c>, prefix <c>LIKE 'text%'</c>, <c>AND</c>, <c>OR</c>, <c>NOT</c>) with
/// SqlParserCS and translates its syntax tree, rejecting every node outside the subset.
/// </summary>
/// <remarks>
/// The translator rejects a node by throwing <see cref="FormatException"/> from deep in the tree, like the parser's own
/// <see cref="ParserException"/>; <see cref="Parse"/> catches both, so no exception leaves this class.
/// </remarks>
internal static class SqlSubset
{
    public const int MaxSqlLength = 16 * 1024;
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;
    private const int MaxOrderKeys = 4;
    private const int MaxInValues = 256;

    // Bounds the engine statement: SQLite limits expression depth and parameter count.
    private const int MaxConditions = 256;
    private const int MaxValues = 1024;

    /// <summary>The statement, or the rule the SQL breaks; the message never quotes the client's text.</summary>
    public static Valid<SqlStatement> Parse(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > MaxSqlLength)
        {
            return new Validation("sql must be 1 to 16384 characters");
        }

        try
        {
            return Translate(new Parser().ParseSql(sql, new SQLiteDialect()));
        }
        catch (ParserException exception)
        {
            return new Validation(Syntax(exception.Line, exception.Column));
        }
        catch (TokenizeException exception)
        {
            return new Validation(Syntax(exception.Line, exception.Column));
        }
        catch (FormatException exception)
        {
            return new Validation(exception.Message);
        }
    }

    // The parser's own messages quote the client's tokens; only the position is reported.
    private static string Syntax(long line, long column) =>
        line > 0 ? $"SQL syntax error at line {line}, column {column}" : "SQL syntax error";

    private static SqlStatement Translate(Sequence<Statement> statements)
    {
        if (statements.Count != 1)
        {
            throw new FormatException("exactly one statement is supported");
        }

        if (statements[0] is not Statement.Select { Query: var query })
        {
            throw new FormatException("only SELECT statements are supported");
        }

        if (query.With is not null)
        {
            throw new FormatException("WITH is not supported");
        }

        if (query.Body is not SetExpression.SelectExpression { Select: var select })
        {
            throw new FormatException("UNION, INTERSECT, EXCEPT, VALUES and parenthesized queries are not supported");
        }

        if (query.Offset is not null || query.Fetch is not null)
        {
            throw new FormatException("OFFSET and FETCH are not supported");
        }

        if (select.Distinct is not null || select.Top is not null)
        {
            throw new FormatException("DISTINCT and TOP are not supported");
        }

        if (select.GroupBy is not null || select.Having is not null)
        {
            throw new FormatException("GROUP BY and HAVING are not supported");
        }

        // The syntax tree is made of records: comparing with one built from the supported parts rejects every other clause.
        if (query != new Query(query.Body) { OrderBy = query.OrderBy, Limit = query.Limit }
            || select != new Select(select.Projection) { From = select.From, Selection = select.Selection })
        {
            throw new FormatException("only SELECT, FROM, WHERE, ORDER BY and LIMIT clauses are supported");
        }

        var filter = select.Selection is null ? null : Filter(select.Selection);
        if (filter is not null && Count(filter) is var (conditions, values) && (conditions > MaxConditions || values > MaxValues))
        {
            throw new FormatException("WHERE supports at most 256 conditions and 1024 values");
        }

        var sqlQuery = new SqlQuery(Collection(select.From), filter, Order(query.OrderBy), Limit(query.Limit));
        return new SqlStatement(sqlQuery, Projection(select.Projection), query.Limit is not null);
    }

    private static string Collection(Sequence<TableWithJoins>? from)
    {
        if (from is null || from.Count == 0)
        {
            throw new FormatException("FROM is required");
        }

        if (from is not [{ Relation: var relation, Joins: null or [] }])
        {
            throw new FormatException("joins and multiple collections are not supported");
        }

        if (relation is not TableFactor.Table table)
        {
            throw new FormatException("FROM supports only a collection name, not subqueries or functions");
        }

        if (table != new TableFactor.Table(table.Name))
        {
            throw new FormatException("aliases and table options are not supported");
        }

        return table.Name.Values is [var name] && DatabaseName.TryParse(name.Value, out var collection)
            ? collection.Value
            : throw new FormatException("the collection must be a lowercase name of at most 63 characters");
    }

    private static List<DocumentField>? Projection(Sequence<SelectItem> items)
    {
        if (items is [SelectItem.Wildcard wildcard])
        {
            return wildcard.WildcardAdditionalOptions == new WildcardAdditionalOptions()
                ? null
                : throw new FormatException("* options are not supported");
        }

        return [.. items.Select(item => item is SelectItem.UnnamedExpression { Expression: var expression }
            ? Field(expression)
            : throw new FormatException("select either * or a list of fields, without aliases")).Distinct()];
    }

    private static List<SqlOrder> Order(OrderBy? order)
    {
        if (order is null)
        {
            return [];
        }

        if (order is not { Interpolate: null, Expressions: { Count: <= MaxOrderKeys } keys })
        {
            throw new FormatException("ORDER BY supports at most 4 fields");
        }

        return [.. keys.Select(key => key is { NullsFirst: null, WithFill: null }
            ? new SqlOrder(ComparableField(key.Expression), key.Asc == false)
            : throw new FormatException("ORDER BY supports only ASC and DESC"))];
    }

    private static int Limit(Expression? limit) =>
        limit switch
        {
            null => DefaultLimit,
            Expression.LiteralValue { Value: Value.Number { Value: var text } }
                when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= MaxLimit => value,
            _ => throw new FormatException("LIMIT must be an integer from 1 to 1000"),
        };

    private static SqlFilter Filter(Expression expression)
    {
        switch (expression)
        {
            case Expression.Nested { Expression: var inner }:
                return Filter(inner);
            case Expression.UnaryOp { Op: UnaryOperator.Not, Expression: var inner }:
                return new SqlNotFilter(Filter(inner));
            case Expression.BinaryOp { Op: BinaryOperator.And or BinaryOperator.Or } logical:
                // A chain a AND b AND c parses left-deep; walking its spine keeps the recursion shallow.
                var parts = new List<SqlFilter>();
                var current = expression;
                while (current is Expression.BinaryOp { Op: var op } chain && op == logical.Op)
                {
                    parts.Add(Filter(chain.Right));
                    current = chain.Left;
                }

                parts.Add(Filter(current));
                parts.Reverse();
                return logical.Op == BinaryOperator.And ? new SqlAndFilter(parts) : new SqlOrFilter(parts);
            case Expression.BinaryOp { Op: var op, Left: var left, Right: var right } when Comparison(op) is { } comparison:
                return new SqlCompareFilter(ComparableField(left), comparison, [Literal(right)]);
            case Expression.InList { Expression: var field, List: var list, Negated: var negated }:
                if (list.Count is < 1 or > MaxInValues)
                {
                    throw new FormatException("IN takes 1 to 256 values");
                }

                var compare = new SqlCompareFilter(ComparableField(field), SqlOperator.In, [.. list.Select(Literal)]);
                return negated ? new SqlNotFilter(compare) : compare;
            case Expression.Like { Expression: { } field, Negated: var negated, Pattern: var pattern } like:
                var prefix = Prefix(like, pattern);
                var starts = new SqlCompareFilter(ComparableField(field), SqlOperator.Prefix, [prefix]);
                return negated ? new SqlNotFilter(starts) : starts;
            case Expression.IsNull { Expression: var field }:
                return new SqlCompareFilter(ComparableField(field), SqlOperator.IsNull, []);
            case Expression.IsNotNull { Expression: var field }:
                return new SqlCompareFilter(ComparableField(field), SqlOperator.IsNotNull, []);
            default:
                throw new FormatException($"{Describe(expression)} not supported in WHERE");
        }
    }

    // Only a prefix pattern: it compiles to an index range. Unlike SQLite's LIKE (and like PostgreSQL's), it is case-sensitive.
    private static string Prefix(Expression.Like like, Expression pattern) =>
        like.EscapeChar is null
            && pattern is Expression.LiteralValue { Value: Value.SingleQuotedString { Value: var text } }
            && text.EndsWith('%') && text.AsSpan(0, text.Length - 1).IndexOfAny('%', '_') < 0
            ? text[..^1]
            : throw new FormatException("LIKE supports only a prefix pattern 'text%', without other % or _ and without ESCAPE");

    private static SqlOperator? Comparison(BinaryOperator op) => op switch
    {
        BinaryOperator.Eq => SqlOperator.Equal,
        BinaryOperator.NotEq => SqlOperator.NotEqual,
        BinaryOperator.Lt => SqlOperator.Less,
        BinaryOperator.LtEq => SqlOperator.LessOrEqual,
        BinaryOperator.Gt => SqlOperator.Greater,
        BinaryOperator.GtEq => SqlOperator.GreaterOrEqual,
        _ => null,
    };

    private static DocumentField Field(Expression expression)
    {
        var path = expression switch
        {
            Expression.Identifier { Ident: var ident } => Member(ident.Value),
            Expression.CompoundIdentifier { Idents: var idents } => string.Join('.', idents.Select(ident => Member(ident.Value))),
            _ => throw new FormatException($"{Describe(expression)} not supported where a field is expected"),
        };
        return DocumentField.TryParse(path, out var field)
            ? field
            : throw new FormatException("fields must be _id, _rev or dotted member names of letters, digits and _");
    }

    // Each identifier names one member: joining a quoted "a.b" with the others would read it as the nested path a.b, not
    // the member literally named a.b, which the subset cannot address.
    private static string Member(string name) =>
        name.Contains('.', StringComparison.Ordinal)
            ? throw new FormatException("a quoted name cannot contain a dot: nested members are written a.b or \"a\".\"b\"")
            : name;

    // _rev is the opaque version the data API reports, so it can be selected but not compared or sorted.
    private static DocumentField ComparableField(Expression expression) =>
        Field(expression) is var field && field == DocumentField.Version
            ? throw new FormatException("_rev can only be selected: versions are opaque")
            : field;

    private static object Literal(Expression expression) => expression switch
    {
        Expression.LiteralValue { Value: Value.SingleQuotedString { Value: var text } } => text,
        Expression.LiteralValue { Value: Value.Boolean { Value: var flag } } => flag,
        Expression.LiteralValue { Value: Value.Number { Value: var text } } => Number(text),
        Expression.UnaryOp { Op: UnaryOperator.Minus, Expression: Expression.LiteralValue { Value: Value.Number { Value: var text } } } => Number("-" + text),
        Expression.LiteralValue { Value: Value.Null } => throw new FormatException("compare with NULL using IS NULL or IS NOT NULL"),
        Expression.LiteralValue { Value: Value.Placeholder } => throw new FormatException("parameters are not supported"),
        _ => throw new FormatException("a comparison takes a field on the left and a single-quoted string, number, TRUE or FALSE on the right"),
    };

    private static object Number(string text)
    {
        if (text.AsSpan().IndexOfAny(".eE") < 0)
        {
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                ? integer
                : throw new FormatException("integers must fit in 64 bits");
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw new FormatException("decimals must be finite");
    }

    // Names the construct by its syntax-tree node, never by the client's text.
    private static string Describe(Expression expression) => expression switch
    {
        Expression.Function => "functions are",
        Expression.Subquery or Expression.InSubquery or Expression.Exists => "subqueries are",
        Expression.ILike => "ILIKE is",
        Expression.Between => "BETWEEN is",
        Expression.Case => "CASE is",
        Expression.Cast => "CAST is",
        Expression.BinaryOp { Op: var op } => $"the {op} operator is",
        Expression.LiteralValue or Expression.UnaryOp => "a literal is",
        _ => $"the {expression.GetType().Name} expression is",
    };

    private static (int Conditions, int Values) Count(SqlFilter filter) => filter switch
    {
        SqlCompareFilter compare => (1, compare.Values.Count),
        SqlNotFilter not => Count(not.Filter),
        SqlAndFilter and => Sum(and.Filters),
        SqlOrFilter or => Sum(or.Filters),
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private static (int Conditions, int Values) Sum(IReadOnlyList<SqlFilter> filters) =>
        filters.Select(Count).Aggregate((0, 0), (total, next) => (total.Item1 + next.Conditions, total.Item2 + next.Values));
}
