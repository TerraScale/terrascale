using TerraBase.Errors;

namespace TerraBase.Features.Management.Queries;

/// <summary>Registers the SQL and saved-query part of the Management slice.</summary>
internal static class QueriesExtensions
{
    internal const string SqlDescription =
        "sql (at most 16 KiB): SELECT * | field, … FROM collection [WHERE …] [ORDER BY field [ASC|DESC], … (at most 4)] [LIMIT 1 to 1000, default 100]. " +
        "WHERE combines field op literal (=, !=, <>, <, <=, >, >=), field IN (…), field IS [NOT] NULL with AND, OR, NOT and parentheses. " +
        "Literals: single-quoted strings, numbers, TRUE, FALSE. Fields: _id, _rev or dotted member paths.";

    /// <summary>Registers <see cref="IQueryService"/> and the shared <see cref="IQueryShapeTracker"/>.</summary>
    public static IServiceCollection AddTerraBaseManagementQueries(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<QueryFieldRecorder>()
            .AddSingleton<QueryShapeRecorder>()
            .AddSingleton<IQueryShapeTracker>(provider => provider.GetRequiredService<QueryShapeRecorder>())
            .AddSingleton<IQueryService, QueryService>();
    }

    /// <summary>A saved query with its name trimmed, or the rule it breaks: a display name and SQL of the subset.</summary>
    internal static Valid<SavedQueryRequest> Validate(SavedQueryRequest request) =>
        (ManagementRoute.CheckDisplayName(new NameRequest(request.Name)), SqlSubset.Parse(request.Sql)) switch
        {
            (Validation error, _) => error,
            (_, Validation error) => error,
            (string name, SqlStatement) => request with { Name = name },
        };
}
