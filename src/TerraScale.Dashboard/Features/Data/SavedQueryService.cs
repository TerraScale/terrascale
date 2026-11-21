using System.Text;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;
    public sealed record SavedQuery(string Id, string Name, string Sql, string DatabaseId, DateTimeOffset CreatedAt);
    public readonly union SavedQueryResult(SavedQuery, Validation, Conflict, Forbidden);
    public readonly union SavedQueryDeleted(Done, NotFound, Forbidden);

    public sealed class SavedQueryService(IDbContextFactory<DashboardDbContext> storage, TimeProvider time, AccountService accounts)
    {
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
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var rows = await db.SavedQueries.AsNoTracking()
                .Where(row => row.OrganizationId == scope.OrganizationId && row.ProjectId == scope.ProjectId)
                .ToListAsync(cancellationToken);
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
                return new Forbidden("Write access is required.");
            }

            name = name.Trim();
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 128)
            {
                return new Validation("Saved query names use 1–128 characters.");
            }

            sql = sql.Trim();
            if (SqlValidation.Diagnostics(sql).FirstOrDefault(item => item.Severity == "error") is { } problem)
            {
                return new Validation(problem.Message);
            }

            if (!SqlValidation.StartsWithCommand(sql))
            {
                return new Validation("Start the saved query with a SQL command such as SELECT or WITH.");
            }

            var query = new SavedQuery(AccountService.NewId(), name, sql, databaseId, time.GetUtcNow());
            var scope = DatabaseService.Scope(context);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            db.SavedQueries.Add(new SavedQueryRow
            {
                Id = query.Id,
                OrganizationId = scope.OrganizationId,
                ProjectId = scope.ProjectId,
                DatabaseId = databaseId,
                Name = name,
                Sql = sql,
                CreatedAt = query.CreatedAt.UtcDateTime,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException error) when (error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation })
            {
                return new Conflict("A saved query with this name already exists. Choose another name or delete the existing query.");
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
                return new Forbidden("Write access is required.");
            }

            var scope = DatabaseService.Scope(context);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            return await db.SavedQueries.Where(row => row.Id == id && row.OrganizationId == scope.OrganizationId && row.ProjectId == scope.ProjectId).ExecuteDeleteAsync(cancellationToken) == 0
                ? new NotFound("Saved query is unavailable.")
                : new Done();
        }
    }
}
