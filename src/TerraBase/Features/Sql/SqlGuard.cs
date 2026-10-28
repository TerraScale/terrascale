using System.Reflection;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using TerraBase.Engine;

namespace TerraBase.Features.Sql;

/// <summary>
/// Charges every row combination a statement evaluates to the work budget: each SELECT of the statement (top level,
/// subqueries, CTE bodies, both sides of a compound) gets <c>WHERE tb_sql_row(coalesce(x.c, y.d, …)) AND (…)</c>, naming a
/// column of each FROM item, so the term is evaluated in the innermost join loop (Turso evaluates a function of constants
/// once). Joins, cross products and aggregates over them stop at the budget like scans do.
/// </summary>
internal static class SqlGuard
{
    // The syntax tree is immutable to callers; the guard is set on the validated tree once, before it is rendered.
    private static readonly PropertyInfo Selection = typeof(Select).GetProperty(nameof(Select.Selection))!;

    public static void Apply(Query query, IReadOnlyDictionary<string, SqlTableDefinition> tables)
    {
        Concat(query, [with(ReferenceEqualityComparer.Instance)]);
        var ctes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var cte in query.With?.CteTables ?? [])
        {
            ctes[cte.Alias.Name.Value.ToLowerInvariant()] = cte.Alias.Columns is [var first, ..] ? first.Value : null;
        }

        foreach (var cte in query.With?.CteTables ?? [])
        {
            ctes[cte.Alias.Name.Value.ToLowerInvariant()] ??= FirstColumn(cte.Query, tables, ctes);
        }

        var queries = new Queries { Found = { query } };
        ((IElement)query).Visit(queries);
        var selects = new HashSet<Select>(ReferenceEqualityComparer.Instance);
        foreach (var found in queries.Found)
        {
            Collect(found.Body, selects);
            foreach (var cte in found.With?.CteTables ?? [])
            {
                Collect(cte.Query.Body, selects);
            }
        }

        foreach (var select in selects)
        {
            Guard(select, tables, ctes);
        }
    }

    // Every a || b becomes tb_sql_concat(a, b) (SqlProgram.Concat), which bounds the string it builds: nested subqueries
    // doubling a string reached gigabytes. Post-order, so operands are rewritten first; the node is replaced in its parent.
    private static void Concat(object node, HashSet<object> seen)
    {
        if (!seen.Add(node))
        {
            return;
        }

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType || property.PropertyType == typeof(string))
            {
                continue;
            }

            switch (property.GetValue(node))
            {
                case Expression expression:
                    Concat(expression, seen);
                    if (Rewritten(expression) is { } call && property.CanWrite)
                    {
                        property.SetValue(node, call);
                    }

                    break;
                case System.Collections.IList list:
                    ConcatList(list, seen);
                    break;
                case { } child when child.GetType().Namespace?.StartsWith("SqlParser", StringComparison.Ordinal) == true:
                    Concat(child, seen);
                    break;
            }
        }
    }

    // A list's items, and the items of nested lists (the rows of VALUES).
    private static void ConcatList(System.Collections.IList list, HashSet<object> seen)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is System.Collections.IList nested)
            {
                ConcatList(nested, seen);
            }
            else if (list[i] is { } item && item.GetType().Namespace?.StartsWith("SqlParser", StringComparison.Ordinal) == true)
            {
                Concat(item, seen);
                if (item is Expression element && Rewritten(element) is { } replaced)
                {
                    list[i] = replaced;
                }
            }
        }
    }

    // Also a '…' literal holding two adjacent quotes: SqlParserCS escapes a quote unless the next one is a quote too, so it
    // renders the value it''s as 'it''s' and SQLite reads it's. Its quote-free pieces are joined with char(39) instead.
    private static Expression? Rewritten(Expression expression) => expression switch
    {
        Expression.BinaryOp { Op: BinaryOperator.StringConcat, Left: var left, Right: var right } => Call($"{SqlProgram.Concat}({left.ToSql()}, {right.ToSql()})"),
        // SqlRead.Number reads a GLOB b as a SIMILAR TO b.
        Expression.SimilarTo { Expression: var value, Pattern: var pattern, Negated: var negated } => Call($"({(negated ? "NOT " : "")}glob({pattern.ToSql()}, {value.ToSql()}))"),
        Expression.LiteralValue { Value: Value.SingleQuotedString { Value: var text } } when text.Contains("''", StringComparison.Ordinal) =>
            Call(text.Split('\'').Skip(1).Aggregate("'" + text.Split('\'')[0] + "'", (joined, piece) => $"{SqlProgram.Concat}({SqlProgram.Concat}({joined}, char(39)), '{piece}')")),
        _ => null,
    };

    private static Expression? Call(string sql) =>
        new Parser().ParseSql($"SELECT {sql}", new SQLiteDialect())
            is [Statement.Select { Query.Body: SetExpression.SelectExpression { Select.Projection: [SelectItem.UnnamedExpression { Expression: var call }] } }]
            ? call
            : null;

    private static void Collect(SetExpression? body, HashSet<Select> selects)
    {
        switch (body)
        {
            case SetExpression.SelectExpression { Select: var select }:
                selects.Add(select);
                break;
            case SetExpression.SetOperation { Left: var left, Right: var right }:
                Collect(left, selects);
                Collect(right, selects);
                break;
            case SetExpression.QueryExpression { Query.Body: var inner }:
                Collect(inner, selects);
                break;
        }
    }

    private static void Guard(Select select, IReadOnlyDictionary<string, SqlTableDefinition> tables, Dictionary<string, string?> ctes)
    {
        if (select.Selection is Expression.BinaryOp { Left: Expression.Function { Name.Values: [{ Value: SqlProgram.RowGuard }] } })
        {
            return;
        }

        var items = Items(select.From).ToList();
        var columns = new List<string>();
        foreach (var item in items)
        {
            if (Reference(item, tables, ctes) is var (qualifier, column))
            {
                columns.Add($"{Quote(qualifier)}.{Quote(column)}");
            }
            else if (items.Count > 1)
            {
                throw new FormatException("a joined subquery or CTE needs an alias and a named first column (AS name)");
            }
        }

        var argument = columns.Count == 0 ? "0" : $"coalesce({string.Join(", ", columns)}, 0)";
        var guard = new Parser().ParseSql($"SELECT 1 WHERE {SqlProgram.RowGuard}({argument})", new SQLiteDialect())
            is [Statement.Select { Query.Body: SetExpression.SelectExpression { Select.Selection: { } parsed } }]
            ? parsed
            : throw new FormatException("the statement cannot be guarded");
        Selection.SetValue(select, select.Selection is { } where ? new Expression.BinaryOp(guard, BinaryOperator.And, new Expression.Nested(where)) : guard);
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

    // A column of a FROM item, qualified by its alias or name, or null when its columns are unknown.
    private static (string Qualifier, string Column)? Reference(TableFactor item, IReadOnlyDictionary<string, SqlTableDefinition> tables, Dictionary<string, string?> ctes)
    {
        var (qualifier, column) = item switch
        {
            TableFactor.Table { Name.Values: [var name] } when ctes.TryGetValue(name.Value.ToLowerInvariant(), out var first) =>
                (item.Alias?.Name.Value ?? name.Value, first),
            TableFactor.Table { Name.Values: [var name] } when tables.TryGetValue(name.Value.ToLowerInvariant(), out var table) =>
                (item.Alias?.Name.Value ?? name.Value, table.Columns[0].Name),
            TableFactor.Derived { SubQuery: var query, Alias: { } alias } =>
                (alias.Name.Value, alias.Columns is [var first, ..] ? first.Value : FirstColumn(query, tables, ctes)),
            _ => (null, null),
        };
        return qualifier is null || column is null ? null : (qualifier, column);
    }

    // The name SQLite gives a query's first result column, when it is a name: an alias, a column, column1 of VALUES.
    private static string? FirstColumn(Query query, IReadOnlyDictionary<string, SqlTableDefinition> tables, Dictionary<string, string?> ctes) => query.Body switch
    {
        SetExpression.ValuesExpression => "column1",
        SetExpression.SetOperation { Left: SetExpression.SelectExpression or SetExpression.ValuesExpression } operation =>
            FirstColumn(new Query(operation.Left), tables, ctes),
        SetExpression.SelectExpression { Select.Projection: [var first, ..] } select => first switch
        {
            SelectItem.ExpressionWithAlias { Alias: var alias } => alias.Value,
            SelectItem.UnnamedExpression { Expression: Expression.Identifier { Ident: var ident } } => ident.Value,
            SelectItem.UnnamedExpression { Expression: Expression.CompoundIdentifier { Idents: [.., var ident] } } => ident.Value,
            SelectItem.Wildcard when Items(select.Select.From).FirstOrDefault() is { } source =>
                Reference(source, tables, ctes)?.Column,
            _ => null,
        },
        _ => null,
    };

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class Queries : Visitor
    {
        public List<Query> Found { get; } = [];

        public override ControlFlow PreVisitTableFactor(TableFactor relation)
        {
            // The visitor does not enter a FROM subquery: its own subqueries are found here.
            if (relation is TableFactor.Derived { SubQuery: { } query } && !Found.Contains(query))
            {
                Found.Add(query);
                ((IElement)query).Visit(this);
            }

            return ControlFlow.Continue;
        }

        public override ControlFlow PreVisitExpression(Expression expression)
        {
            switch (expression)
            {
                case Expression.Subquery { Query: var query }:
                    Found.Add(query);
                    break;
                case Expression.Exists { SubQuery: var query }:
                    Found.Add(query);
                    break;
                case Expression.InSubquery { SubQuery: var query }:
                    Found.Add(query);
                    break;
            }

            return ControlFlow.Continue;
        }
    }
}
