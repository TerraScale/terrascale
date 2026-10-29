using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

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
                var current = await state.GetAsync(SqlWorkspace.Collection, key, cancellationToken);
                try
                {
                    await state.PutAsync(SqlWorkspace.Collection, key, JsonSerializer.SerializeToElement(record),
                        current is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(current.Version), cancellationToken);
                    return receipt.Id;
                }
                catch (TerraBasePreconditionFailedException)
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
            if (await state.GetAsync(SqlWorkspace.Collection, Key(context, databaseId), cancellationToken) is not { } document)
            {
                return null;
            }

            var record = AccountService.Read<SqlWorkspaceRecord>(document);
            var scope = DatabaseService.Scope(context);
            return record.Receipt is { } receipt && receipt.Id == receiptId && time.GetUtcNow() - receipt.CreatedAt < Lifetime &&
                record.OrganizationId == scope.OrganizationId && record.ProjectId == scope.ProjectId && record.DatabaseId == databaseId && record.UserId == context.UserId
                ? record : null;
        }

        private static string Key(AccountContext context, string databaseId)
        {
            var scope = DatabaseService.Scope(context);
            return scope.OrganizationId + "/" + scope.ProjectId + "/" + databaseId + "/" + context.UserId;
        }
    }
}
