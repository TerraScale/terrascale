using System.Text.Json;

using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary>A body that carries SQL of the dashboard subset.</summary>
/// <param name="Sql">The statement.</param>
public sealed record SqlRequest(string? Sql);

/// <summary>One row of a SQL result.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Value">The whole document for <c>*</c>; otherwise one member per selected field, named as written (missing fields are null).</param>
public sealed record SqlRow(string Id, JsonElement Value);

/// <summary>The rows a statement selected.</summary>
/// <param name="Matched">Rows returned.</param>
/// <param name="Truncated">Whether the limit or the 8 MiB byte bound cut the rows short.</param>
/// <param name="Rows">The rows in query order.</param>
public sealed record SqlResultBody(int Matched, bool Truncated, IReadOnlyList<SqlRow> Rows);

/// <summary>A field a query filters or sorts on without an index that leads with it.</summary>
/// <param name="Field">The field as written.</param>
/// <param name="Reason">Why an index would help.</param>
public sealed record IndexSuggestion(string Field, string Reason);

/// <summary>Whether SQL is in the subset and how the engine would run it.</summary>
/// <param name="Ok">Whether the SQL is valid.</param>
/// <param name="Error">Why it is not; <see langword="null"/> when <paramref name="Ok"/>.</param>
/// <param name="Collection">The queried collection.</param>
/// <param name="HasLimit">Whether the statement has a <c>LIMIT</c>.</param>
/// <param name="Plan">Engine plan steps.</param>
/// <param name="Indexes">Names of the indexes the plan reads.</param>
/// <param name="Suggestions">Fields that would benefit from an index.</param>
public sealed record SqlCheckBody(
    bool Ok, string? Error, string? Collection, bool HasLimit, IReadOnlyList<string> Plan, IReadOnlyList<string> Indexes, IReadOnlyList<IndexSuggestion> Suggestions);

/// <summary>How often queries of a database filtered or sorted on a field without an index; drives index recommendations.</summary>
/// <param name="Collection">Queried collection.</param>
/// <param name="Field">The field as written.</param>
/// <param name="Queries">Successful queries that used the field without an index.</param>
/// <param name="LastSeenAt">Time of the latest such query.</param>
public sealed record QueryFieldUsage(string Collection, string Field, long Queries, DateTimeOffset LastSeenAt)
{
    /// <summary>Usage records a database keeps; fields first used after that are not recorded.</summary>
    internal const int MaxRecords = 1000;

    /// <summary>The catalog collection of the usage records of a managed database.</summary>
    internal static string Records(string databaseId) => SystemCatalog.DatabaseState(databaseId) + "query_fields";

    /// <summary>The record id, also the recommendation id: <c>collection:field</c>.</summary>
    internal static string Key(string collection, string field) => $"{collection}:{field}";
}

/// <summary>A named SQL statement saved in a project.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="Name">Display name.</param>
/// <param name="Sql">The statement; valid when saved.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Time of the last update; <see langword="null"/> before the first.</param>
public sealed record SavedQuery(string Id, string Name, string Sql, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>A saved query to create or replace.</summary>
/// <param name="Name">Display name, 1 to 128 characters.</param>
/// <param name="Sql">A statement of the SQL subset.</param>
public sealed record SavedQueryRequest(string? Name, string? Sql);

/// <summary>Runs and checks dashboard SQL against managed databases, and keeps saved queries.</summary>
public interface IQueryService
{
    /// <summary>Runs a statement and records the fields it used without an index.</summary>
    ValueTask<SqlResultBody> RunAsync(TenantContext database, SqlStatement statement, CancellationToken cancellationToken);

    /// <summary>Explains a statement without running it.</summary>
    ValueTask<SqlCheckBody> CheckAsync(TenantContext database, SqlStatement statement, CancellationToken cancellationToken);

    /// <summary>Lists the saved queries of a project in creation order.</summary>
    ValueTask<ManagementPage<SavedQuery>> ListSavedAsync(Project project, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Saves a query in a project.</summary>
    ValueTask<SavedQuery?> CreateSavedAsync(string userId, Project project, string name, string sql, CancellationToken cancellationToken);

    /// <summary>Reads a saved query; <see langword="null"/> when absent.</summary>
    ValueTask<SavedQuery?> GetSavedAsync(Project project, string queryId, CancellationToken cancellationToken);

    /// <summary>Replaces the name and SQL of a saved query; <see langword="null"/> when absent.</summary>
    ValueTask<SavedQuery?> UpdateSavedAsync(string userId, Project project, string queryId, string name, string sql, CancellationToken cancellationToken);

    /// <summary>Deletes a saved query; <see langword="false"/> when absent.</summary>
    ValueTask<bool> DeleteSavedAsync(string userId, Project project, string queryId, CancellationToken cancellationToken);
}
