using System.Reflection;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Tokens;
using Column = TerraBase.Features.Sql.SqlAffinityColumn;
using Scope = TerraBase.Features.Sql.SqlAffinityScope;

namespace TerraBase.Features.Sql;

/// <summary>Bounded, execution-local column lineage and SQL name visibility.</summary>
internal sealed class SqlAffinityResolver
{
    public const int MaxCtes = 128;
    public const long ReservationBytes = SqlAffinityBudget.ReservationBytes;
    private readonly SqlAffinityBudget _budget = new();
    private readonly Func<Query, List<Scope>, List<Column>?> _output;
    private readonly IReadOnlyDictionary<string, SqlTableDefinition> _tables;
    private readonly string? _keyed;
    private readonly Dictionary<string, CommonTableExpression> _ctes = [with(SqlIdentifierComparer.Instance)];
    private readonly Dictionary<string, List<Column>> _tableColumns = [with(SqlIdentifierComparer.Instance)];
    private readonly Dictionary<Query, List<Column>?> _outputs = [with(ReferenceEqualityComparer.Instance)];
    private readonly Dictionary<SetExpression, List<Column>?> _bodies = [with(ReferenceEqualityComparer.Instance)];
    private readonly Dictionary<Select, Scope> _selects = [with(ReferenceEqualityComparer.Instance)];
    private readonly Dictionary<Select, Scope> _aliasScopes = [with(ReferenceEqualityComparer.Instance)];
    private readonly Dictionary<Expression, List<Scope>> _scopes = [with(ReferenceEqualityComparer.Instance)];
    private readonly HashSet<object> _visited = [with(ReferenceEqualityComparer.Instance)];
    private readonly HashSet<string> _names = [with(SqlIdentifierComparer.Instance)];
    private int _groups;
    private int _aliasNumber;

    public SqlAffinityResolver(Query query, IReadOnlyDictionary<string, SqlTableDefinition> tables, string? keyed)
    {
        _tables = tables;
        _keyed = keyed;
        _output = Output;
        foreach (var word in new Tokenizer().Tokenize(query.ToSql(), new SqlDialect()).OfType<Word>())
        {
            _budget.Charge(1, word.Value.Length);
            _names.Add(word.Value);
        }
        foreach (var cte in query.With?.CteTables ?? [])
        {
            if (_ctes.Count >= MaxCtes)
            {
                throw SqlAffinityBudget.Limit();
            }

            _budget.Charge(1, cte.Alias.Name.Value.Length);
            _ctes.TryAdd(cte.Alias.Name.Value, cte);
        }

        Walk(query, [], 0);
        // Turso does not synthesize SQLite's :N suffix for duplicate CTE output names.
        // Bind the engine to the same bounded, deduplicated shape used by this resolver.
        foreach (var cte in query.With?.CteTables ?? [])
        {
            if (Rename(Output(cte.Query, []), cte.Alias.Columns) is { } columns)
            {
                cte.Alias.GetType().GetProperty(nameof(cte.Alias.Columns))!.SetValue(cte.Alias,
                    new Sequence<Ident>(columns.Select(column => new Ident(column.Name, '"'))));
            }
        }
    }

    public SqlAffinity? Resolve(Expression column, Expression? context = null)
    {
        var found = _scopes.TryGetValue(context ?? column, out var scopes) ? Scope.Lookup(column, scopes, _output) : null;
        return found is { Ambiguous: true }
            ? throw new FormatException("compound query output affinity is ambiguous")
            : found?.Affinity;
    }

    public bool ConversionIsIdentity(Expression expression, SqlAffinity affinity)
    {
        var storage = _scopes.TryGetValue(expression, out var scopes)
            ? Scope.Lookup(expression, scopes, _output)?.Storage : null;
        storage ??= SqlGuard.Storage(expression);
        return affinity == SqlAffinity.Text ? storage == SqlAffinity.Text
            : storage is SqlAffinity.Numeric;
    }

    private Expression Qualify(Expression expression)
    {
        if (expression is not (Expression.Identifier or Expression.CompoundIdentifier)
            || !_scopes.TryGetValue(expression, out var scopes)
            || Scope.Lookup(expression, scopes, _output, strict: false) is not { Qualifier: { } qualifier } column)
        {
            return expression;
        }

        var qualified = new Expression.CompoundIdentifier([new Ident(qualifier, '"'), new Ident(column.Name, '"')]);
        _budget.Charge(1);
        _scopes[qualified] = scopes;
        return qualified;
    }

    // Reflection is required: the parser's visitor has no SELECT callback and skips derived tables.
    private void Walk(object node, List<Scope> scopes, int depth)
    {
        if (depth > 256 || _visited.Count >= 4096)
        {
            throw SqlAffinityBudget.Limit();
        }

        if (!_visited.Add(node))
        {
            return;
        }

        if (node is Select select)
        {
            scopes = [SelectScope(select, scopes), .. scopes];
        }

        if (node is Expression)
        {
            _budget.Charge(1);
            _scopes[(Expression)node] = scopes;
        }

        if (node is System.Collections.IList list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is { } item)
                {
                    Walk(item, scopes, depth + 1);
                    if (item is Expression expression)
                    {
                        list[i] = Qualify(expression);
                    }
                }
            }

            return;
        }

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.GetValue(node) is { } child
                && (child is System.Collections.IList || child.GetType().Namespace?.StartsWith("SqlParser", StringComparison.Ordinal) == true))
            {
                var context = node is Query { Body: SetExpression.SelectExpression { Select: var body } } && property.Name == nameof(Query.OrderBy)
                    ? (List<Scope>)[AliasScope(body, scopes), .. scopes] : scopes;
                if (node is Select owner && property.Name is nameof(Select.Selection) or nameof(Select.Having) or nameof(Select.GroupBy) or nameof(Select.From))
                {
                    context = [AliasScope(owner, [.. scopes.Skip(1)]), .. scopes.Skip(1)];
                }

                if (node is OrderByExpression order && SqlGuard.Unwrap(order.Expression) is Expression.Identifier && context.Count > 0)
                {
                    context = [context[0] with { PreferAliases = true }, .. context.Skip(1)];
                }

                Walk(child, context, depth + 1);
                if (child is Expression expression && property.CanWrite)
                {
                    property.SetValue(node, Qualify(expression));
                }
            }
        }

        if (node is SelectItem.QualifiedWildcard { Name.Values: [var name] } wildcard)
        {
            var (_, columns) = scopes.SelectMany(scope => scope.Sources).FirstOrDefault(source => Same(source.Name, name.Value));
            if (columns is [var first, ..] && first.Qualifier is { } qualifier)
            {
                wildcard.GetType().GetProperty(nameof(wildcard.Name))!.SetValue(wildcard, new ObjectName([new Ident(qualifier, '"')]));
            }
        }
    }

    private Scope SelectScope(Select select, List<Scope> outer)
    {
        if (_selects.TryGetValue(select, out var cached))
        {
            return cached;
        }

        var scope = (select.From ?? []).Aggregate(new Scope([], []), (left, from) => Merge(left, From(from, outer), null));
        _selects.Add(select, scope);
        return scope;
    }

    // Expressions search real columns first; a bare ORDER BY alias refers to the projection. Projection expressions
    // themselves cannot refer to sibling aliases, and unary/computed expressions retain no column affinity.
    private Scope AliasScope(Select select, List<Scope> outer)
    {
        if (_aliasScopes.TryGetValue(select, out var cached))
        {
            return cached;
        }

        var source = SelectScope(select, outer);
        var aliases = new List<Column>();
        foreach (var item in select.Projection.OfType<SelectItem.ExpressionWithAlias>())
        {
            _budget.Add(aliases, [Project(item.Alias.Value, item.Expression, [source, .. outer])]);
        }

        var result = source with { Aliases = aliases };
        _aliasScopes.Add(select, result);
        return result;
    }

    private Scope From(TableWithJoins from, List<Scope> outer)
    {
        if (++_groups > 256)
        {
            throw SqlAffinityBudget.Limit();
        }

        return (from.Joins ?? []).Aggregate(Factor(from.Relation!, outer), (left, join) => Merge(left, Factor(join.Relation!, outer), join.JoinOperator));
    }

    private Scope Factor(TableFactor factor, List<Scope> outer)
    {
        if (factor is TableFactor.NestedJoin { TableWithJoins: { } nested })
        {
            if (factor.Alias is not null)
            {
                throw new FormatException("affinity for aliased parenthesized joins is not supported");
            }

            return From(nested, outer);
        }

        List<Column>? columns = null;
        var name = factor.Alias?.Name.Value ?? "";
        if (factor is TableFactor.Table { Name.Values: [var table] })
        {
            name = factor.Alias?.Name.Value ?? table.Value;
            if (_ctes.TryGetValue(table.Value, out var cte))
            {
                columns = Rename(Output(cte.Query, []), cte.Alias.Columns);
            }
            else if (_tables.TryGetValue(SqlIdentifierComparer.LowerAscii(table.Value), out var definition))
            {
                if (!_tableColumns.TryGetValue(table.Value, out columns))
                {
                    columns = [];
                    _budget.Add(columns, definition.Columns.Select(column => new Column(column.Name, column.Affinity == SqlAffinity.Blob ? null : column.Affinity)));
                    // Internal UPDATE/DELETE reads expose the document key alongside declared columns.
                    if (Same(table.Value, _keyed ?? ""))
                    {
                        _budget.Add(columns, [new Column("_id", SqlAffinity.Text)]);
                    }
                    _tableColumns.Add(table.Value, columns);
                }
            }
        }
        else if (factor is TableFactor.Derived derived)
        {
            columns = Rename(Output(derived.SubQuery, outer), factor.Alias?.Columns);
        }

        var rendered = name;
        if (outer.Any(scope => scope.Sources.Any(source => Same(source.Name, name))))
        {
            do
            {
                rendered = "__tb_affinity" + (++_aliasNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _budget.Charge(1, rendered.Length);
            }
            while (!_names.Add(rendered));
            var alias = new TableAlias(new Ident(rendered, '"'), factor.Alias?.Columns);
            factor.GetType().GetProperty(nameof(factor.Alias))!.SetValue(factor, alias);
        }

        if (columns is not null)
        {
            var qualified = new List<Column>();
            _budget.Add(qualified, columns.Select(column => column with { Qualifier = rendered.Length > 0 ? rendered : null }));
            columns = qualified;
        }

        _budget.Charge(1);
        return new Scope([(name, columns)], columns);
    }

    private Scope Merge(Scope left, Scope right, JoinOperator? operation)
    {
        _budget.Charge(left.Sources.Count + right.Sources.Count);
        var sources = left.Sources.Concat(right.Sources).ToList();
        if (left.Visible is null || right.Visible is null)
        {
            return new Scope(sources, null);
        }

        var constraint = (operation as JoinOperator.ConstrainedJoinOperator)?.JoinConstraint;
        var merged = constraint switch
        {
            JoinConstraint.Using { Idents: var names } => names.Select(name => name.Value).ToHashSet(SqlIdentifierComparer.Instance),
            JoinConstraint.Natural => left.Visible.Select(column => column.Name).Intersect(right.Visible.Select(column => column.Name), SqlIdentifierComparer.Instance).ToHashSet(SqlIdentifierComparer.Instance),
            _ => [],
        };
        foreach (var name in merged)
        {
            var lhs = left.Visible.FirstOrDefault(column => Same(column.Name, name));
            var rhs = right.Visible.FirstOrDefault(column => Same(column.Name, name));
            if (lhs.Ambiguous || rhs.Ambiguous || SqlGuard.Conversion(lhs.Affinity, rhs.Affinity) is not null
                || SqlGuard.Conversion(rhs.Affinity, lhs.Affinity) is not null)
            {
                throw SqlGuard.Unsupported();
            }
        }

        var visible = new List<Column>();
        _budget.Add(visible, left.Visible.Select(column => merged.Contains(column.Name) ? column with
        {
            Qualifier = operation is JoinOperator.FullOuter ? null : operation is JoinOperator.RightOuter
                ? right.Visible.FirstOrDefault(other => Same(other.Name, column.Name)).Qualifier : column.Qualifier,
            Affinity = operation is JoinOperator.FullOuter ? null : operation is JoinOperator.RightOuter
                ? right.Visible.FirstOrDefault(other => Same(other.Name, column.Name)).Affinity : column.Affinity,
            Ambiguous = operation is not JoinOperator.FullOuter && (operation is JoinOperator.RightOuter
                ? right.Visible.FirstOrDefault(other => Same(other.Name, column.Name)).Ambiguous : column.Ambiguous),
        } : column));
        _budget.Add(visible, right.Visible.Where(column => !merged.Contains(column.Name)));
        return new Scope(sources, visible);
    }

    private List<Column>? Output(Query query, List<Scope> outer)
    {
        if (_outputs.TryGetValue(query, out var cached))
        {
            return cached;
        }

        if (_outputs.Count >= 128)
        {
            throw SqlAffinityBudget.Limit();
        }

        _outputs.Add(query, null); // Memoize before descending: repeated references share a shape; cycles stop here.
        var result = BodyOutput(query.Body, outer);
        _outputs[query] = result;
        return result;
    }

    private List<Column>? BodyOutput(SetExpression body, List<Scope> outer)
    {
        if (_bodies.TryGetValue(body, out var cached))
        {
            return cached;
        }

        if (_bodies.Count >= 128)
        {
            throw SqlAffinityBudget.Limit();
        }

        _budget.Charge(1);
        _bodies.Add(body, null);
        List<Column>? result;
        switch (body)
        {
            case SetExpression.SelectExpression { Select: var select }:
                result = SelectOutput(select, outer);
                break;
            case SetExpression.QueryExpression { Query: var inner }:
                result = Output(inner, outer);
                break;
            case SetExpression.ValuesExpression { Values.Rows: [var first, ..] }:
                result = [];
                _budget.Add(result, Enumerable.Range(1, first.Count).Select(i => new Column("column" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), null)));
                break;
            case SetExpression.SetOperation operation:
                var left = BodyOutput(operation.Left, outer);
                var right = BodyOutput(operation.Right, outer);
                if (left is null || right is null || left.Count != right.Count)
                {
                    throw new FormatException("compound query output shape cannot be inferred");
                }

                // Differing branch affinities have no stable SQLite choice. Keep ordinary result reads working,
                // but refuse comparisons that would require guessing a branch's affinity.
                result = [];
                _budget.Add(result, left.Select((column, i) => column with
                {
                    Ambiguous = column.Ambiguous || right[i].Ambiguous || column.Affinity != right[i].Affinity,
                    Storage = column.Storage == right[i].Storage ? column.Storage : null,
                }));
                break;
            default:
                result = null;
                break;
        }

        _bodies[body] = result;
        return result;
    }

    private List<Column>? SelectOutput(Select select, List<Scope> outer)
    {
        var scope = SelectScope(select, outer);
        var result = new List<Column>();
        foreach (var item in select.Projection)
        {
            var columns = item switch
            {
                SelectItem.Wildcard => scope.Visible,
                SelectItem.QualifiedWildcard { Name.Values: [var qualifier] } => scope.Sources.FirstOrDefault(source => Same(source.Name, qualifier.Value) || source.Columns?.Any(column => Same(column.Qualifier ?? "", qualifier.Value)) == true).Columns,
                SelectItem.ExpressionWithAlias { Expression: var expression, Alias: var alias } => [Project(alias.Value, expression, [scope, .. outer])],
                SelectItem.UnnamedExpression { Expression: var expression } => [Project(SqlGuard.Unwrap(expression) switch
                {
                    Expression.Identifier { Ident: var ident } => ident.Value,
                    Expression.CompoundIdentifier { Idents: [.., var ident] } => ident.Value,
                    _ => expression.ToSql(),
                }, expression, [scope, .. outer])],
                _ => null,
            };
            if (columns is null)
            {
                return null;
            }

            _budget.Add(result, columns);
        }

        UniqueNames(result);
        return result;
    }

    private Column Project(string name, Expression expression, List<Scope> scopes)
    {
        var source = Scope.Lookup(expression, scopes, _output);
        return new Column(name, source?.Affinity, source?.Ambiguous ?? false, Storage: source?.Storage ?? SqlGuard.Storage(expression));
    }

    private void UniqueNames(List<Column> result)
    {
        var names = new HashSet<string>(SqlIdentifierComparer.Instance);
        for (var i = 0; i < result.Count; i++)
        {
            var column = result[i];
            var name = column.Name;
            if (!names.Add(name))
            {
                _budget.Charge(0, name.Length);
                var suffix = name.LastIndexOf(':');
                var stem = suffix >= 0 && int.TryParse(name.AsSpan(suffix + 1), out _) ? name[..suffix] : name;
                for (var number = 1; ; number++)
                {
                    _budget.Charge(0, stem.Length + 12);
                    name = stem + ":" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (names.Add(name))
                    {
                        break;
                    }
                }

                result[i] = column with { Name = name };
            }
        }
    }

    private List<Column>? Rename(List<Column>? columns, Sequence<Ident>? aliases)
    {
        if (columns is null || aliases is null || aliases.Count == 0 || aliases.Count != columns.Count)
        {
            return columns;
        }

        _budget.Charge(columns.Count);
        var renamed = columns.Select((column, i) => column with { Name = aliases[i].Value }).ToList();
        UniqueNames(renamed);
        return renamed;
    }

    private static bool Same(string left, string right) => SqlIdentifierComparer.Instance.Equals(left, right);
}
