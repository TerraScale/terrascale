using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record SqlWorkspaceRecord(string OrganizationId, string ProjectId, string DatabaseId, string UserId,
        string Draft, IReadOnlyList<string> History, string HistoryEpoch = "", SqlRunReceipt? Receipt = null);

    public sealed class SqlWorkspaceUnavailableException : Exception
    {
        public SqlWorkspaceUnavailableException(Exception error) : base("The SQL workspace is unavailable.", error) { }
    }

    public sealed class SqlWorkspace
    {
        public const int SqlBytes = 16 * 1024;
        public const int HistoryBytes = 128 * 1024;
        public const int HistoryCount = 20;
        public static bool Unavailable(Exception error)
        {
            return error is IOException or HttpRequestException or JsonException or OperationCanceledException or TimeoutException || DatabaseResults.KnownFailure(error);
        }

        public static async Task EraseAsync(IDbContextFactory<DashboardDbContext> storage, Expression<Func<SqlWorkspaceRow, bool>> owned, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            await db.SqlWorkspaces.Where(owned).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
