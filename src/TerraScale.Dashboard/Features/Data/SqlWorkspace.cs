using System.Text.Json;
using Dapper;
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

        private const string EraseProject = "DELETE FROM sql_workspaces WHERE organization_id = @OrganizationId AND project_id = @ProjectId";
        private const string EraseOrganization = "DELETE FROM sql_workspaces WHERE organization_id = @OrganizationId";
        private const string EraseUser = "DELETE FROM sql_workspaces WHERE user_id = @UserId";

        public static async Task EraseProjectAsync(DashboardDb storage, string organizationId, string projectId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(storage);
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(EraseProject, new { OrganizationId = organizationId, ProjectId = projectId });
        }

        public static async Task EraseOrganizationAsync(DashboardDb storage, string organizationId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(storage);
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(EraseOrganization, new { OrganizationId = organizationId });
        }

        public static async Task EraseUserAsync(DashboardDb storage, string userId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(storage);
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(EraseUser, new { UserId = userId });
        }
    }
}
