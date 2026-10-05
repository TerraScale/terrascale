namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary>Registers the SQL and saved-query part of the Management slice.</summary>
public static class QueriesExtensions
{
    internal const string SqlDescription =
        "sql (at most 16 KiB): SELECT * | field, … FROM collection [WHERE …] [ORDER BY field [ASC|DESC], … (at most 4)] [LIMIT 1 to 1000, default 100]. " +
        "WHERE combines field op literal (=, !=, <>, <, <=, >, >=), field IN (…), field IS [NOT] NULL with AND, OR, NOT and parentheses. " +
        "Literals: single-quoted strings, numbers, TRUE, FALSE. Fields: _id, _rev or dotted member paths.";

    /// <summary>Registers <see cref="IQueryService"/>.</summary>
    public static IServiceCollection AddTerraBaseManagementQueries(this IServiceCollection services) =>
        services.AddSingleton<QueryFieldRecorder>().AddSingleton<IQueryService, QueryService>();

    /// <summary>Validates a saved query: a display name and SQL of the subset.</summary>
    internal static (string Name, string Sql) Validate(SavedQueryRequest request)
    {
        var name = ManagementRoute.DisplayName(new NameRequest(request.Name));
        var parsed = SqlSubset.Parse(request.Sql);
        return parsed.Error is null ? (name, request.Sql!) : throw new BadHttpRequestException(parsed.Error);
    }
}
