using System.Globalization;
using System.Reflection;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace TerraBase.Features.Sql;

/// <summary>
/// Applies column affinity to the constant side of comparisons, as SQLite does for a column with a declared type: a
/// table column reads through the engine's table CTE as an expression without affinity, so <c>int_col = '2'</c> would
/// match nothing. Each comparison is resolved in its own SELECT's scope (aliases, joins, derived tables, CTEs, outer
/// queries for correlated subqueries) and coerced locally: a literal is rewritten in place, a parameter occurrence gets
/// its own converted copy as one more bound parameter, so <c>n = :x AND s = :x</c> compares <c>n</c> with the integer
/// and <c>s</c> with the text. A column whose affinity cannot be told (ambiguous name, expression, VALUES) keeps the
/// constant as it is.
/// </summary>
internal static class SqlCoerce
{
    // Derived tables and CTEs resolve through each other; deeper chains are left unconverted.
    private const int MaxDepth = 32;

    private static readonly PropertyInfo Left = typeof(Expression.BinaryOp).GetProperty(nameof(Expression.BinaryOp.Left))!;
    private static readonly PropertyInfo Right = typeof(Expression.BinaryOp).GetProperty(nameof(Expression.BinaryOp.Right))!;

    /// <summary>Rewrites <paramref name="query"/>'s comparisons; parameter copies are appended to <paramref name="parameters"/>.</summary>
    public static void Apply(Query query, IReadOnlyDictionary<string, SqlTableDefinition> tables, List<object?> parameters)
    {
        var top = new Scope(null, [], [with(StringComparer.OrdinalIgnoreCase)], tables);
        var walker = new Walker(parameters, parameters.Count);
        foreach (var cte in query.With?.CteTables ?? [])
        {
            // A CTE sees the CTEs before it (WITH RECURSIVE is refused by validation).
            walker.Walk(cte.Query, top);
            top.Ctes[cte.Alias.Name.Value] = new Cte(cte.Query, [.. cte.Alias.Columns?.Select(column => column.Value) ?? []]);
        }

        walker.Walk(query.Body, top);
        foreach (var order in query.OrderBy?.Expressions ?? [])
        {
            walker.Walk(order, top);
        }
    }

    /// <summary>A bound parameter converted by <paramref name="affinity"/>; values of other types are returned as they are.</summary>
    public static object? Bound(SqlAffinity affinity, object? value)
    {
        var converted = SqlWrite.Apply(affinity, value switch
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
            _ => value,
        };
    }

    private sealed record Cte(Query Query, IReadOnlyList<string> Columns);

    // A FROM item: a SQL table, or a query (derived table or CTE) resolved in the scope it is defined in; neither for a
    // catalog relation, whose columns have no affinity.
    private sealed record Source(string Name, SqlTableDefinition? Table, Query? Query, IReadOnlyList<string> Columns, Scope? Defined);

    private sealed record Scope(Scope? Parent, List<Source> Sources, Dictionary<string, Cte> Ctes, IReadOnlyDictionary<string, SqlTableDefinition> Tables)
    {
        // The statement's scope, where CTEs are defined.
        private Scope Root => Parent?.Root ?? this;

        // The scope of a SELECT: its FROM items, inside the scope that encloses it.
        public Scope Of(Select select)
        {
            var sources = new List<Source>();
            foreach (var factor in Items(select.From))
            {
                switch (factor)
                {
                    case TableFactor.Table { Name.Values: [var name] } table when Ctes.TryGetValue(name.Value, out var cte):
                        var columns = table.Alias?.Columns is { Count: > 0 } renamed ? [.. renamed.Select(column => column.Value)] : cte.Columns;
                        sources.Add(new Source(table.Alias?.Name.Value ?? name.Value, null, cte.Query, columns, Root));
                        break;
                    case TableFactor.Table { Name.Values: [var name] } table:
                        sources.Add(new Source(table.Alias?.Name.Value ?? name.Value, Tables.GetValueOrDefault(name.Value.ToLowerInvariant()), null, [], null));
                        break;
                    case TableFactor.Derived { SubQuery: var query, Alias: var alias }:
                        sources.Add(new Source(alias?.Name.Value ?? "", null, query, [.. alias?.Columns?.Select(column => column.Value) ?? []], this));
                        break;
                }
            }

            return this with { Parent = this, Sources = sources };
        }

        // The affinity of a column reference: the innermost scope naming it decides; null when unknown or ambiguous.
        public SqlAffinity? Resolve(Expression expression, int depth)
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
            {
                switch (expression)
                {
                    case Expression.Identifier { Ident.Value: var name }:
                        var found = scope.Sources.Select(source => Lookup(source, name, depth)).Where(match => match.Found).Select(match => match.Affinity).Distinct().ToList();
                        if (found.Count > 0)
                        {
                            return found is [var only] ? only : null;
                        }

                        break;
                    case Expression.CompoundIdentifier { Idents: [var qualifier, var column] }:
                        if (scope.Sources.FirstOrDefault(source => source.Name.Equals(qualifier.Value, StringComparison.OrdinalIgnoreCase)) is { } named)
                        {
                            return Lookup(named, column.Value, depth).Affinity;
                        }

                        break;
                    case Expression.Nested { Expression: var inner }:
                        return Resolve(inner, depth);
                    default:
                        return null;
                }
            }

            return null;
        }
    }

    // Whether a FROM item has a column of that name, and its affinity.
    private static (bool Found, SqlAffinity? Affinity) Lookup(Source source, string name, int depth)
    {
        if (source.Table is { } table)
        {
            return table.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } column ? (true, column.Affinity) : (false, null);
        }

        if (source is not { Query: { } query, Defined: { } defined })
        {
            return (false, null);
        }

        if (depth >= MaxDepth)
        {
            return (true, null);
        }

        // A compound takes its names from its first SELECT.
        var body = query.Body;
        while (body is SetExpression.SetOperation { Left: var first })
        {
            body = first;
        }

        var position = source.Columns.Count > 0 ? IndexOf(source.Columns, name) : -1;
        if (source.Columns.Count > 0 && position < 0)
        {
            return (false, null);
        }

        if (body is not SetExpression.SelectExpression { Select: var select })
        {
            // VALUES (column1, …) and other bodies have no affinity.
            return (position >= 0 || name.StartsWith("column", StringComparison.OrdinalIgnoreCase), null);
        }

        var scope = defined.Of(select);
        for (var i = 0; i < select.Projection.Count; i++)
        {
            switch (select.Projection[i])
            {
                // Positions after a * are not known without expanding it.
                case SelectItem.Wildcard or SelectItem.QualifiedWildcard when position >= 0:
                    return (true, null);
                case SelectItem.Wildcard:
                    foreach (var inner in scope.Sources)
                    {
                        if (Lookup(inner, name, depth + 1) is { Found: true } match)
                        {
                            return match;
                        }
                    }

                    break;
                case SelectItem.QualifiedWildcard { Name.Values: [var qualifier] }:
                    if (scope.Sources.FirstOrDefault(inner => inner.Name.Equals(qualifier.Value, StringComparison.OrdinalIgnoreCase)) is { } named
                        && Lookup(named, name, depth + 1) is { Found: true } qualified)
                    {
                        return qualified;
                    }

                    break;
                case SelectItem.ExpressionWithAlias { Expression: var expression, Alias.Value: var alias } when position == i || (position < 0 && alias.Equals(name, StringComparison.OrdinalIgnoreCase)):
                    return (true, scope.Resolve(expression, depth + 1));
                case SelectItem.UnnamedExpression { Expression: var expression } when position == i || (position < 0 && Named(expression, name)):
                    return (true, scope.Resolve(expression, depth + 1));
            }
        }

        return (false, null);
    }

    private static bool Named(Expression expression, string name) => expression switch
    {
        Expression.Identifier { Ident.Value: var ident } => ident.Equals(name, StringComparison.OrdinalIgnoreCase),
        Expression.CompoundIdentifier { Idents: [.., var ident] } => ident.Value.Equals(name, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static IEnumerable<TableFactor> Items(Sequence<TableWithJoins>? from)
    {
        foreach (var join in from ?? [])
        {
            foreach (var factor in (join.Joins ?? []).Select(item => item.Relation).Prepend(join.Relation))
            {
                if (factor is TableFactor.NestedJoin { TableWithJoins: { } nested })
                {
                    foreach (var inner in Items([nested]))
                    {
                        yield return inner;
                    }
                }
                else if (factor is not null)
                {
                    yield return factor;
                }
            }
        }
    }

    // Visits every node with the scope of the SELECT it is in, coercing the constants of each comparison with a column.
    private sealed class Walker(List<object?> parameters, int count)
    {
        private readonly HashSet<object> seen = [with(ReferenceEqualityComparer.Instance)];

        public void Walk(object node, Scope scope)
        {
            if (!seen.Add(node))
            {
                return;
            }

            switch (node)
            {
                case Select select:
                    scope = scope.Of(select);
                    break;
                case Expression.BinaryOp { Op: BinaryOperator.Eq or BinaryOperator.NotEq or BinaryOperator.Lt or BinaryOperator.LtEq or BinaryOperator.Gt or BinaryOperator.GtEq } compare:
                    if (compare.Left is Expression.Identifier or Expression.CompoundIdentifier)
                    {
                        Coerce(scope.Resolve(compare.Left, 0), compare.Right, value => Right.SetValue(compare, value));
                    }
                    else if (compare.Right is Expression.Identifier or Expression.CompoundIdentifier)
                    {
                        Coerce(scope.Resolve(compare.Right, 0), compare.Left, value => Left.SetValue(compare, value));
                    }

                    break;
                case Expression.InList { Expression: Expression.Identifier or Expression.CompoundIdentifier, List: var list } inList:
                    var affinity = scope.Resolve(inList.Expression, 0);
                    for (var i = 0; i < list.Count; i++)
                    {
                        var index = i;
                        Coerce(affinity, list[i], value => list[index] = value);
                    }

                    break;
            }

            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType || property.PropertyType == typeof(string))
                {
                    continue;
                }

                switch (property.GetValue(node))
                {
                    case System.Collections.IList list:
                        WalkList(list, scope);
                        break;
                    case { } child when child.GetType().Namespace?.StartsWith("SqlParser", StringComparison.Ordinal) == true:
                        Walk(child, scope);
                        break;
                }
            }
        }

        private void WalkList(System.Collections.IList list, Scope scope)
        {
            foreach (var item in list)
            {
                if (item is System.Collections.IList nested)
                {
                    WalkList(nested, scope);
                }
                else if (item is not null && item.GetType().Namespace?.StartsWith("SqlParser", StringComparison.Ordinal) == true)
                {
                    Walk(item, scope);
                }
            }
        }

        private void Coerce(SqlAffinity? affinity, Expression constant, Action<Expression> set)
        {
            if (affinity is not { } known || known == SqlAffinity.Blob)
            {
                return;
            }

            switch (constant)
            {
                // Each occurrence gets its own copy: the same parameter may meet columns of other affinities.
                case Expression.LiteralValue { Value: Value.Placeholder { Value: ['$', 'p', .. var digits] } }
                    when int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= count:
                    var value = Bound(known, parameters[number - 1]);
                    if (!Equals(value, parameters[number - 1]))
                    {
                        parameters.Add(value);
                        var placeholder = new Expression.LiteralValue(new Value.Placeholder("$p" + parameters.Count.ToString(CultureInfo.InvariantCulture)));
                        seen.Add(placeholder);
                        set(placeholder);
                    }

                    break;
                case Expression.LiteralValue { Value: Value.SingleQuotedString or Value.Number } literal when Literal(literal) is { } text:
                    var replaced = Constant(SqlWrite.Apply(known, text));
                    seen.Add(replaced);
                    set(replaced);
                    break;
            }
        }
    }

    private static SqlValue? Literal(Expression.LiteralValue literal) => literal.Value switch
    {
        Value.SingleQuotedString { Value: var text } => SqlValue.Of(text),
        Value.Number { Value: var text } when long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer) => SqlValue.Of(integer),
        Value.Number { Value: var text } when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real) => SqlValue.Of(real),
        _ => null,
    };

    private static Expression Constant(SqlValue value)
    {
        var text = value.Kind switch
        {
            SqlValueKind.Integer => value.Integer.ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.Real.ToString("R", CultureInfo.InvariantCulture),
            _ => "'" + value.Text!.Replace("'", "''", StringComparison.Ordinal) + "'",
        };
        return new Parser().ParseSql("SELECT " + text, new SQLiteDialect())
            is [Statement.Select { Query.Body: SetExpression.SelectExpression { Select.Projection: [SelectItem.UnnamedExpression { Expression: var constant }] } }]
            ? constant
            : throw new FormatException("a constant cannot be rendered");
    }
}
