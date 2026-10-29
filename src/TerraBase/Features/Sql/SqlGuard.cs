using System.Reflection;
using SqlParser;
using SqlParser.Ast;
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
    // Guards and affinity are applied only to an execution copy of the validated tree.
    private static readonly PropertyInfo Selection = typeof(Select).GetProperty(nameof(Select.Selection))!;

    /// <summary>Normalizes parser rendering before the validated tree is cached or copied.</summary>
    public static void Normalize(Query query) => Concat(query, [with(ReferenceEqualityComparer.Instance)]);

    public static void Apply(Query query, IReadOnlyDictionary<string, SqlTableDefinition> tables)
    {
        var ctes = new Dictionary<string, string?>(SqlIdentifierComparer.Instance);
        foreach (var cte in query.With?.CteTables ?? [])
        {
            ctes[cte.Alias.Name.Value] = cte.Alias.Columns is [var first, ..] ? first.Value : null;
        }

        foreach (var cte in query.With?.CteTables ?? [])
        {
            ctes[cte.Alias.Name.Value] ??= FirstColumn(cte.Query, tables, ctes);
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

        // Turso names a parenthesized direct projection "(n)" instead of SQLite's "n".
        if (node is Select select)
        {
            for (var i = 0; i < select.Projection.Count; i++)
            {
                if (select.Projection[i] is SelectItem.UnnamedExpression { Expression: Expression.Nested nested }
                    && Unwrap(nested) is var column
                    && column is Expression.Identifier or Expression.CompoundIdentifier)
                {
                    var name = column is Expression.Identifier ident ? ident.Ident : ((Expression.CompoundIdentifier)column).Idents.Last();
                    select.Projection[i] = new SelectItem.ExpressionWithAlias(nested, new Ident(name.Value, '"'));
                }
            }
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

    /// <summary>Parentheses preserve column affinity; unary operators and computed expressions do not.</summary>
    public static Expression Unwrap(Expression expression)
    {
        while (expression is Expression.Nested nested)
        {
            expression = nested.Expression;
        }

        return expression;
    }

    /// <summary>
    /// Applies column affinity to each comparison independently. Extra bound slots name the original parameter and
    /// the conversion for this occurrence; projections and other uses retain the original slots.
    /// </summary>
    public static List<(int Parameter, SqlAffinity Affinity)> Affinity(Query query, IReadOnlyDictionary<string, SqlTableDefinition> tables, int parameterCount, string? keyed)
    {
        var parameters = new List<(int Parameter, SqlAffinity Affinity)>();
        var resolver = new SqlAffinityResolver(query, tables, keyed);
        var finder = new Comparisons();
        ((IElement)query).Visit(finder);
        foreach (var comparison in finder.Found)
        {
            var left = resolver.Resolve(comparison.Left);
            var right = comparison.InList ? null : resolver.Resolve(comparison.Right, comparison.Context);
            Convert(comparison.Right, Conversion(left, right), comparison.SetRight);
            Convert(comparison.Left, Conversion(right, left), comparison.SetLeft);
        }

        return parameters;

        void Convert(Expression expression, SqlAffinity? affinity, Action<Expression>? set)
        {
            if (affinity is null || Unwrap(expression) is Expression.LiteralValue { Value: Value.Null })
            {
                return;
            }

            if (resolver.ConversionIsIdentity(expression, affinity.Value))
            {
                return;
            }

            if (set is null)
            {
                throw Unsupported();
            }

            var literal = Literal(expression);
            var placeholder = Unwrap(expression) is Expression.LiteralValue { Value: Value.Placeholder { Value: ['$', 'p', ..] } };
            if (affinity == SqlAffinity.Text && (placeholder || literal is not null))
            {
                // API parameters have no BLOB storage class. Let the engine format numeric TEXT affinity exactly;
                // .NET floating-point formatting differs for negative zero, exponent notation and significant digits.
                set(Call("CAST(" + expression.ToSql() + " AS TEXT)") ?? throw Unsupported());
            }
            else if (Unwrap(expression) is Expression.LiteralValue { Value: Value.Placeholder { Value: ['$', 'p', .. var digits] } }
                && int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                parameters.Add((number, affinity.Value));
                set(new Expression.LiteralValue(new Value.Placeholder("$p" + (parameterCount + parameters.Count).ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
            else if (literal is { } value)
            {
                set(Constant(ComparisonValue(affinity.Value, value)));
            }
            else
            {
                throw Unsupported();
            }
        }
    }

    // A guaranteed runtime storage class is separate from declared affinity: n+0 still has no affinity.
    public static SqlAffinity? Storage(Expression expression) => Unwrap(expression) switch
    {
        Expression.BinaryOp binary when binary.Op is BinaryOperator.Plus or BinaryOperator.Minus or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => SqlAffinity.Numeric,
        Expression.UnaryOp { Op: UnaryOperator.Minus } => SqlAffinity.Numeric,
        Expression.Function { Name.Values: [{ Value: SqlProgram.Concat }] } => SqlAffinity.Text,
        Expression.LiteralValue { Value: Value.SingleQuotedString } => SqlAffinity.Text,
        Expression.LiteralValue { Value: Value.Number or Value.Boolean } => SqlAffinity.Numeric,
        _ => null,
    };

    public static SqlValue ComparisonValue(SqlAffinity affinity, SqlValue value) =>
        value.Kind == SqlValueKind.Text && value.Text!.Any(c => char.IsWhiteSpace(c) && c is not (' ' or '\t' or '\n' or '\r' or '\v' or '\f'))
            ? value : SqlWrite.Apply(affinity, value);

    public static SqlAffinity? Conversion(SqlAffinity? source, SqlAffinity? target) => source switch
    {
        SqlAffinity.Integer or SqlAffinity.Real or SqlAffinity.Numeric when target is not (SqlAffinity.Integer or SqlAffinity.Real or SqlAffinity.Numeric) => SqlAffinity.Numeric,
        SqlAffinity.Text when target is null => SqlAffinity.Text,
        _ => null,
    };

    public static FormatException Unsupported() => new("comparison requires unsupported row-dependent affinity conversion");

    private static SqlValue? Literal(Expression expression)
    {
        var negative = false;
        expression = Unwrap(expression);
        var signed = expression is Expression.UnaryOp;
        while (expression is Expression.UnaryOp { Op: UnaryOperator.Plus or UnaryOperator.Minus } unary)
        {
            negative ^= unary.Op == UnaryOperator.Minus;
            expression = Unwrap(unary.Expression);
        }

        return expression switch
        {
            Expression.LiteralValue { Value: Value.SingleQuotedString { Value: var text } } when !signed => SqlValue.Of(text),
            Expression.LiteralValue { Value: Value.Boolean { Value: var flag } } when !signed => SqlValue.Of(flag ? 1L : 0L),
            Expression.LiteralValue { Value: Value.Number { Value: var text } } => Number((negative ? "-" : "") + text),
            _ => null,
        };
    }

    private static SqlValue? Number(string text) =>
        long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var integer) ? SqlValue.Of(integer)
        : double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real) && double.IsFinite(real) ? SqlValue.Of(real)
        : null;

    private static Expression Constant(SqlValue value)
    {
        var text = value.Kind switch
        {
            SqlValueKind.Integer => value.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.Real.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => "'" + value.Text!.Replace("'", "''", StringComparison.Ordinal) + "'",
        };
        return new Parser().ParseSql("SELECT " + text, new SqlDialect())
            is [Statement.Select { Query.Body: SetExpression.SelectExpression { Select.Projection: [SelectItem.UnnamedExpression { Expression: var constant }] } }]
            ? constant
            : throw new FormatException("a constant cannot be rendered");
    }

    private sealed record Comparison(Expression Left, Expression Right, Action<Expression>? SetLeft,
        Action<Expression>? SetRight, bool InList = false, Expression? Context = null);

    // Every comparison is inspected, including computed operands. Only independent literal/parameter conversions
    // are rewritten; conversions of row-dependent values fail explicitly rather than changing SQL semantics.
    private sealed class Comparisons : Visitor
    {
        public List<Comparison> Found { get; } = [];

        private static Action<Expression> Setter(Expression owner, string property) =>
            value => owner.GetType().GetProperty(property)!.SetValue(owner, value);

        public override ControlFlow PreVisitTableFactor(TableFactor relation)
        {
            if (relation is TableFactor.Derived { SubQuery: var query })
            {
                ((IElement)query).Visit(this);
            }

            return ControlFlow.Continue;
        }

        public override ControlFlow PreVisitExpression(Expression expression)
        {
            switch (expression)
            {
                case Expression.BinaryOp { Op: BinaryOperator.Eq or BinaryOperator.NotEq or BinaryOperator.Lt or BinaryOperator.LtEq or BinaryOperator.Gt or BinaryOperator.GtEq } compare:
                    Found.Add(new(compare.Left, compare.Right, Setter(compare, nameof(compare.Left)), Setter(compare, nameof(compare.Right))));
                    break;
                case Expression.IsDistinctFrom compare:
                    Found.Add(new(compare.Expression1, compare.Expression2, Setter(compare, nameof(compare.Expression1)), Setter(compare, nameof(compare.Expression2))));
                    break;
                case Expression.IsNotDistinctFrom compare:
                    Found.Add(new(compare.Expression1, compare.Expression2, Setter(compare, nameof(compare.Expression1)), Setter(compare, nameof(compare.Expression2))));
                    break;
                case Expression.Case { Operand: { } caseOperand, Conditions: var conditions } simpleCase:
                    for (var i = 0; i < conditions.Count; i++)
                    {
                        var index = i;
                        Found.Add(new(caseOperand, conditions[i], null, value => simpleCase.Conditions[index] = value));
                    }

                    break;
                case Expression.InList { List: var list } inList:
                    for (var i = 0; i < list.Count; i++)
                    {
                        var index = i;
                        Found.Add(new(inList.Expression, list[i], null, value => list[index] = value, InList: true));
                    }

                    break;
                case Expression.InSubquery { Expression: { } operand } inQuery:
                    Found.Add(new(operand, new Expression.Subquery(inQuery.SubQuery), Setter(inQuery, nameof(inQuery.Expression)), null, Context: inQuery));
                    break;
                case Expression.Between between:
                    Found.Add(new(between.Expression, between.Low, null, Setter(between, nameof(between.Low))));
                    Found.Add(new(between.Expression, between.High, null, Setter(between, nameof(between.High))));
                    break;
            }

            return ControlFlow.Continue;
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
        new Parser().ParseSql($"SELECT {sql}", new SqlDialect())
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
        var guard = new Parser().ParseSql($"SELECT 1 WHERE {SqlProgram.RowGuard}({argument})", new SqlDialect())
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
            TableFactor.Table { Name.Values: [var name] } when ctes.TryGetValue(name.Value, out var first) =>
                (item.Alias?.Name.Value ?? name.Value, first),
            TableFactor.Table { Name.Values: [var name] } when tables.TryGetValue(SqlIdentifierComparer.LowerAscii(name.Value), out var table) =>
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
