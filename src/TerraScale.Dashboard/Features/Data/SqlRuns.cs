using System.Data;
using Dapper;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record SqlRunReceipt(string Id, DatabaseSqlResult? Result, int ReturnedRows, string Error, int Status, DateTimeOffset CreatedAt);

    public sealed class SqlRuns(DashboardDb storage, TimeProvider time)
    {
        public const int ResultBytes = 512 * 1024;
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
        private const string Workspace = DashboardSql.SqlWorkspaces.Select
            + " WHERE organization_id = @OrganizationId AND project_id = @ProjectId AND database_id = @DatabaseId AND user_id = @UserId";

        private static string Json(string[] values) => JsonSerializer.Serialize(values, DataRowJson.Default.StringArray);

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
            await using var connection = await storage.OpenAsync(cancellationToken);
            var key = new { scope.OrganizationId, scope.ProjectId, DatabaseId = databaseId, context.UserId };
            for (var attempt = 0; ; attempt++)
            {
                var row = await connection.QuerySingleOrDefaultAsync<SqlWorkspaceRow>(Workspace, key);
                try
                {
                    if (row is null)
                    {
                        DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.SqlWorkspaces.Insert, new SqlWorkspaceRow
                        {
                            Id = AccountService.NewId(),
                            OrganizationId = scope.OrganizationId,
                            ProjectId = scope.ProjectId,
                            DatabaseId = databaseId,
                            UserId = context.UserId,
                            Draft = draft,
                            History = Json([.. history]),
                            HistoryEpoch = "",
                            RunReceipt = receipt,
                            Version = AccountService.NewId(),
                        }));
                        return receipt;
                    }

                    row.Draft = draft;
                    row.HistoryItems = [.. history];
                    row.HistoryEpoch = "";
                    row.RunReceipt = receipt;
                    row.OriginalVersion = row.Version;
                    row.Version = AccountService.NewId();
                    DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.SqlWorkspaces.Update, row));
                    return receipt;
                }
                catch (Exception failure) when (attempt < 3 && (DashboardDb.IsConflict(failure) || DashboardDb.IsDuplicate(failure)))
                {
                    await DashboardDb.ConflictBackoffAsync(attempt, time, cancellationToken);
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
                await using var connection = await storage.OpenAsync(cancellationToken);
                var row = await connection.QuerySingleOrDefaultAsync<SqlWorkspaceRow>(Workspace,
                    new { scope.OrganizationId, scope.ProjectId, DatabaseId = databaseId, context.UserId });
                return row is null ? null : new(row.OrganizationId, row.ProjectId, row.DatabaseId, row.UserId, row.Draft, row.HistoryItems, row.HistoryEpoch, row.RunReceipt);
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !cancellationToken.IsCancellationRequested)
            {
                throw new SqlWorkspaceUnavailableException(error);
            }
        }
    }
}
