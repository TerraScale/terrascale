using SqlParser;
using SqlParser.Ast;
using Column = TerraBase.Features.Sql.SqlAffinityColumn;

namespace TerraBase.Features.Sql;

internal readonly record struct SqlAffinityColumn(string Name, SqlAffinity? Affinity, bool Ambiguous = false);
internal sealed record SqlAffinityScope(List<(string Name, List<Column>? Columns)> Sources, List<Column>? Visible,
        List<Column>? Aliases = null, bool PreferAliases = false)
{
    public static Column? Lookup(Expression expression, List<SqlAffinityScope> scopes,
        Func<Query, List<SqlAffinityScope>, List<Column>?> output)
    {
        expression = SqlGuard.Unwrap(expression);
        while (expression is Expression.Collate collate)
        {
            expression = SqlGuard.Unwrap(collate.Expression);
        }

        switch (expression)
        {
            case Expression.Cast cast:
                var affinity = SqlTable.AffinityOf(cast.DataType.ToSql());
                return new Column("", affinity == SqlAffinity.Blob ? null : affinity);
            case Expression.Subquery subquery:
                return output(subquery.Query, scopes)?.FirstOrDefault()
                    ?? throw new FormatException("SQL source column affinity cannot be inferred");
            case Expression.Tuple:
                throw new FormatException("row-value comparison affinity is not supported");
        }

        var (qualifier, name) = expression switch
        {
            Expression.Identifier { Ident: var ident } => ((string?)null, ident.Value),
            Expression.CompoundIdentifier { Idents: [var table, var ident] } => (table.Value, ident.Value),
            _ => ((string?)null, (string?)null),
        };
        if (name is null)
        {
            return null;
        }

        foreach (var scope in scopes)
        {
            var alias = qualifier is null ? scope.Aliases?.FirstOrDefault(column => Same(column.Name, name)) : null;
            if (scope.PreferAliases && alias is { Name: not null } preferred)
            {
                return preferred;
            }

            var sources = qualifier is null ? [("", scope.Visible)] : scope.Sources.Where(source => Same(source.Name, qualifier)).ToList();
            if (sources.Any(source => source.Item2 is null))
            {
                throw new FormatException("SQL source column affinity cannot be inferred");
            }

            var matches = sources.SelectMany(source => source.Item2!).Where(column => Same(column.Name, name)).ToList();
            if (matches.Count > 0)
            {
                return matches.Count == 1 ? matches[0] : throw new FormatException("SQL column reference is ambiguous");
            }

            if (alias is { Name: not null } fallback)
            {
                return fallback;
            }
        }

        throw new FormatException("SQL source column affinity cannot be inferred");
    }

    private static bool Same(string left, string right) => SqlIdentifierComparer.Instance.Equals(left, right);
}
