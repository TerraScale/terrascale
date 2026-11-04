namespace TerraScale.Dashboard.Features.Data
{
    public sealed class DataService(DatabaseService databases, IDatabaseBackend backend)
    {
        // One statement through TerraBase's relational SQL API, as its own transaction. Reads need read access; writes and DDL
        // need project write access, and callers that promise read-only (MCP) refuse them outright.
        public async Task<DatabaseSqlResult> SqlAsync(AccountContext context, string databaseId, string statement, bool allowWrites, CancellationToken cancellationToken)
        {
            context = await databases.CurrentAsync(context, cancellationToken);
            var database = DatabaseService.Database(context, databaseId);
            statement = statement.Trim();
            if (SqlValidation.Diagnostics(statement).FirstOrDefault(item => item.Severity == "error") is { } problem)
            {
                throw new AccountAccessException(problem.Message, 422);
            }

            var write = !SqlValidation.IsRead(statement);
            if (write && !allowWrites)
            {
                throw new AccountAccessException("Only read-only statements (SELECT, WITH, EXPLAIN, VALUES, introspection PRAGMA) run here.", 422);
            }

            if (write && !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Write access is required to change data or schema. Your role can run read-only statements.");
            }

            return await backend.SqlAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, statement, write, cancellationToken);
        }

        public async Task<SqlCatalog> CatalogAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
        {
            var database = DatabaseService.Database(context, databaseId);
            var tables = await backend.SqlTablesAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, cancellationToken);
            return new(SqlCatalog.Empty.Keywords, [.. tables.Tables.Select(table => new SqlCatalogTable(table.Name,
                [.. table.Columns.Select(column => new SqlCatalogField(column.Name, string.IsNullOrEmpty(column.Decltype) ? "No declared type" : column.Decltype))]))], tables.Truncated);
        }
    }

    public sealed record SqlCatalog(IReadOnlyList<string> Keywords, IReadOnlyList<SqlCatalogTable> Tables, bool Truncated = false)
    {
        public static SqlCatalog Empty { get; } = new(["SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "ORDER BY", "ASC", "DESC", "LIMIT", "TRUE", "FALSE"], []);
    }
    public sealed record SqlCatalogTable(string Name, IReadOnlyList<SqlCatalogField> Fields);
    public sealed record SqlCatalogField(string Path, string Type);
}
