using Dapper;
using System.Text;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record SavedQuery(string Id, string Name, string Sql, string DatabaseId, DateTimeOffset CreatedAt);
    public readonly union SavedQueryResult(SavedQuery, DashboardValidation, DashboardConflict, DashboardForbidden);
    public readonly union SavedQueryDeleted(Done, DashboardNotFound, DashboardForbidden);

    public sealed class SavedQueryService(DashboardDb storage, TimeProvider time, AccountService accounts)
    {
        private const string Saved = DashboardSql.SavedQueries.Select + " WHERE organization_id = @OrganizationId AND project_id = @ProjectId";
        private const string Remove = "DELETE FROM saved_queries WHERE id = @Id AND organization_id = @OrganizationId AND project_id = @ProjectId";

        public async Task<IReadOnlyList<SavedQuery>> ListAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (databaseId.Length > 0)
            {
                DatabaseService.Database(context, databaseId);
            }

            var queries = await ReadAsync(context, cancellationToken);
            return databaseId.Length == 0 ? queries : [.. queries.Where(query => query.DatabaseId.Length == 0 || query.DatabaseId == databaseId)];
        }

        public async Task<IReadOnlyList<SavedQuery>> ListAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            return await ReadAsync(context, cancellationToken);
        }

        private async Task<IReadOnlyList<SavedQuery>> ReadAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var scope = DatabaseService.Scope(context);
            await using var connection = await storage.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<SavedQueryRow>(Saved, new { scope.OrganizationId, scope.ProjectId });
            return [.. rows.Select(row => new SavedQuery(row.Id, row.Name, row.Sql, row.DatabaseId, new DateTimeOffset(row.CreatedAt))).OrderBy(query => query.Name, StringComparer.Ordinal)];
        }

        public async Task<SavedQueryResult> SaveAsync(AccountContext context, string databaseId, string name, string sql, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (databaseId.Length > 0)
            {
                DatabaseService.Database(context, databaseId);
            }

            if (!context.CanWriteProjectData)
            {
                return new DashboardForbidden("Write access is required.");
            }

            name = name.Trim();
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 128)
            {
                return new DashboardValidation("Saved query names use 1–128 characters.");
            }

            sql = sql.Trim();
            if (SqlValidation.Diagnostics(sql).FirstOrDefault(item => item.Severity == "error") is { } problem)
            {
                return new DashboardValidation(problem.Message);
            }

            if (!SqlValidation.StartsWithCommand(sql))
            {
                return new DashboardValidation("Start the saved query with a SQL command such as SELECT or WITH.");
            }

            var query = new SavedQuery(AccountService.NewId(), name, sql, databaseId, time.GetUtcNow());
            var scope = DatabaseService.Scope(context);
            var row = new SavedQueryRow
            {
                Id = query.Id,
                OrganizationId = scope.OrganizationId,
                ProjectId = scope.ProjectId,
                DatabaseId = databaseId,
                Name = name,
                Sql = sql,
                CreatedAt = query.CreatedAt.UtcDateTime,
            };
            try
            {
                await using var connection = await storage.OpenAsync(cancellationToken);
                await connection.ExecuteAsync(DashboardSql.SavedQueries.Insert, row);
            }
            catch (Exception error) when (DashboardDb.IsDuplicate(error))
            {
                return new DashboardConflict("A saved query with this name already exists. Choose another name or delete the existing query.");
            }

            return query;
        }

        public async Task<SavedQueryDeleted> DeleteAsync(AccountContext context, string databaseId, string id, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (databaseId.Length > 0)
            {
                DatabaseService.Database(context, databaseId);
            }

            if (!context.CanWriteProjectData)
            {
                return new DashboardForbidden("Write access is required.");
            }

            var scope = DatabaseService.Scope(context);
            await using var connection = await storage.OpenAsync(cancellationToken);
            return await connection.ExecuteAsync(Remove, new { Id = id, scope.OrganizationId, scope.ProjectId }) == 0
                ? new DashboardNotFound("Saved query is unavailable.")
                : new Done();
        }
    }
}
