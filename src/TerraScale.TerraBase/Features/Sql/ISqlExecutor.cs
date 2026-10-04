using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Features.Sql;

/// <summary>A tabular result.</summary>
/// <param name="Columns">Column names.</param>
/// <param name="Rows">Rows; values are JSON-compatible scalars or <see langword="null"/>.</param>
public sealed record SqlResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>
/// Runs statements of the supported SQL subset against one database, for the PostgreSQL-wire
/// endpoint. Parsing and validation happen here, never by passing client SQL to the engine.
/// Not implemented yet.
/// </summary>
public interface ISqlExecutor
{
    /// <summary>Executes one statement with positional parameters.</summary>
    ValueTask<SqlResult> ExecuteAsync(DatabaseName database, string statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken);
}
