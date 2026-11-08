using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Data
{
    // The outcome of the latest console run. Rows keep at most ResultBytes; ReturnedRows counts what TerraBase returned.
    public sealed record SqlRunReceipt(string Id, DatabaseSqlResult? Result, int ReturnedRows, string Error, int Status, DateTimeOffset CreatedAt);

    // A run answers 303 to a GET of its stored receipt, so refresh and back never resend a statement. One receipt per user
    // and database lives in the user's SQL workspace (erased with it); a newer run replaces it and it expires after an hour.
    public sealed class SqlRuns(IDashboardState state, TimeProvider time)
    {
        public const int ResultBytes = 512 * 1024;
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

        public async Task<string> SaveAsync(AccountContext context, string databaseId, string sql, IReadOnlyList<string> history,
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
            // Bounds hold for any input: SQL past 16 KiB was refused before running and history is capped at 4 KiB.
            var record = new SqlWorkspaceRecord(scope.OrganizationId, scope.ProjectId, databaseId, context.UserId,
                Encoding.UTF8.GetByteCount(sql) <= SqlWorkspace.SqlBytes ? sql : "", history, Receipt: receipt);
            var key = Key(context, databaseId);
            var attempt = 0;
            while (true)
            {
                var current = await state.GetAsync(SqlWorkspace.Table, key, cancellationToken);
                try
                {
                    await state.PutAsync(SqlWorkspace.Table, key, JsonSerializer.SerializeToElement(record),
                        current is null ? StatePrecondition.Absent : StatePrecondition.Matches(current.Version), cancellationToken);
                    return receipt.Id;
                }
                catch (StateConflictException)
                {
                    if (attempt >= 3)
                    {
                        throw;
                    }
                    attempt++;
                }
            }
        }

        // Null when the receipt expired, was replaced by a newer run or belongs to another user, project or database.
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
            StateRecord? row;
            try
            {
                row = await state.GetAsync(SqlWorkspace.Table, Key(context, databaseId), cancellationToken);
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !cancellationToken.IsCancellationRequested)
            {
                throw new SqlWorkspaceUnavailable(error);
            }

            if (row is not { } stored)
            {
                return null;
            }

            var record = AccountService.Read<SqlWorkspaceRecord>(stored);
            var scope = DatabaseService.Scope(context);
            return record.OrganizationId == scope.OrganizationId && record.ProjectId == scope.ProjectId && record.DatabaseId == databaseId && record.UserId == context.UserId
                ? record : null;
        }

        private static string Key(AccountContext context, string databaseId)
        {
            var scope = DatabaseService.Scope(context);
            return scope.OrganizationId + "/" + scope.ProjectId + "/" + databaseId + "/" + context.UserId;
        }
    }
}
