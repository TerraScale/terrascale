using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record SqlRunReceipt(string Id, DatabaseSqlResult? Result, int ReturnedRows, string Error, int Status, DateTimeOffset CreatedAt);

    public sealed class SqlRuns(IDbContextFactory<DashboardDbContext> storage, TimeProvider time)
    {
        public const int ResultBytes = 512 * 1024;
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

        public async Task<SqlRunReceipt> SaveAsync(AccountContext context, string databaseId, string sql, IReadOnlyList<string> history,
            DatabaseSqlResult? result, string error, int status, CancellationToken cancellationToken)
        {
            var scope = DatabaseService.Scope(context);
            var kept = result;
            if (result is not null)
            {
                var rows = new List<IReadOnlyList<JsonElement>>();
                var bytes = 0L;
                foreach (var row in result.Rows)
                {
                    bytes += JsonSerializer.SerializeToUtf8Bytes(row).Length;
                    if (bytes > ResultBytes)
                    {
                        break;
                    }

                    rows.Add(row);
                }
                kept = result with { Rows = rows };
            }
            var receipt = new SqlRunReceipt(AccountService.NewId(), kept, result?.Rows.Count ?? 0, error, status, time.GetUtcNow());
            var draft = Encoding.UTF8.GetByteCount(sql) <= SqlWorkspace.SqlBytes ? sql : "";
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                db.ChangeTracker.Clear();
                var row = await db.SqlWorkspaces.SingleOrDefaultAsync(workspace => workspace.OrganizationId == scope.OrganizationId && workspace.ProjectId == scope.ProjectId &&
                    workspace.DatabaseId == databaseId && workspace.UserId == context.UserId, cancellationToken);
                if (row is null)
                {
                    db.SqlWorkspaces.Add(new SqlWorkspaceRow
                    {
                        Id = AccountService.NewId(),
                        OrganizationId = scope.OrganizationId,
                        ProjectId = scope.ProjectId,
                        DatabaseId = databaseId,
                        UserId = context.UserId,
                        Draft = draft,
                        History = [.. history],
                        HistoryEpoch = "",
                        Receipt = receipt,
                        Version = AccountService.NewId(),
                    });
                }
                else
                {
                    row.Draft = draft;
                    row.History = [.. history];
                    row.HistoryEpoch = "";
                    row.Receipt = receipt;
                    row.Version = AccountService.NewId();
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return receipt;
                }
                catch (DbUpdateException) when (attempt < 3)
                {
                }
            }
        }

        public async Task<SqlWorkspaceRecord?> ReadAsync(AccountContext context, string databaseId, string receiptId, CancellationToken cancellationToken)
        {
            var record = await ReadWorkspaceAsync(context, databaseId, cancellationToken);
            if (record is null)
            {
                return null;
            }

            var receipt = record.Receipt;
            return receipt is { } run && run.Id == receiptId && time.GetUtcNow() - run.CreatedAt < Lifetime ? record : null;
        }

        public async Task<SqlWorkspaceRecord?> ReadWorkspaceAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
        {
            var scope = DatabaseService.Scope(context);
            try
            {
                await using var db = await storage.CreateDbContextAsync(cancellationToken);
                var row = await db.SqlWorkspaces.AsNoTracking().SingleOrDefaultAsync(workspace => workspace.OrganizationId == scope.OrganizationId &&
                    workspace.ProjectId == scope.ProjectId && workspace.DatabaseId == databaseId && workspace.UserId == context.UserId, cancellationToken);
                return row is null ? null : new(row.OrganizationId, row.ProjectId, row.DatabaseId, row.UserId, row.Draft, row.History, row.HistoryEpoch, row.Receipt);
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !cancellationToken.IsCancellationRequested)
            {
                throw new SqlWorkspaceUnavailableException(error);
            }
        }
    }
}
