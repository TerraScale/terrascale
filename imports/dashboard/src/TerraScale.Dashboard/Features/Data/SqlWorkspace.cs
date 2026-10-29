using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public sealed record SqlWorkspaceRecord(string OrganizationId, string ProjectId, string DatabaseId, string UserId,
    string Draft, IReadOnlyList<string> History, string HistoryEpoch = "", SqlRunReceipt? Receipt = null);
public sealed record SqlWorkspaceSnapshot(SqlWorkspaceRecord Record, string Version);

// Ownership-only erasure; SqlRuns keeps each user's latest run receipt here.
public sealed class SqlWorkspace
{
    public const string Collection = "dashboard_sql_workspaces";
    public const int SqlBytes = 16 * 1024;
    public const int HistoryBytes = 128 * 1024;
    public const int HistoryCount = 20;
    public static bool Unavailable(Exception error) => error is IOException or HttpRequestException or JsonException or OperationCanceledException or TimeoutException || DatabaseResults.KnownFailure(error);

    // Project only identity: erasure never downloads SQL history or drafts.
    public static async Task EraseAsync(IDashboardState state, IReadOnlyDictionary<string, string> scope, CancellationToken cancellationToken)
    {
        if (scope.Count == 0) throw new IOException("SQL workspace erasure requires an owner scope.");
        await foreach (var document in DashboardStatePages.ReadAsync(state, Collection, scope,
            ["/OrganizationId", "/ProjectId", "/DatabaseId", "/UserId"], cancellationToken))
        {
            var owner = AccountService.Read<WorkspaceOwner>(document);
            var ids = new[] { owner.OrganizationId, owner.ProjectId, owner.DatabaseId, owner.UserId };
            var key = string.Join("/", ids);
            if (ids.Any(id => !TerraScaleIds.IsValid(id)) || document.Id != key && document.Id != TerraBaseDashboardState.DocumentId(Collection, key))
                throw new IOException("SQL workspace deletion identity is invalid.");
            await state.DeleteListedAsync(Collection, document, cancellationToken);
        }
    }
    private sealed record WorkspaceOwner(string OrganizationId, string ProjectId, string DatabaseId, string UserId);
}
