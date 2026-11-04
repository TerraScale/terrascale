using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record DatabaseWorkspaceDeletion(string OrganizationId, string ProjectId, string DatabaseId);

    // One immutable, bounded terminal marker per database; never embedded in organization metadata.
    public static class DatabaseWorkspaceCleanup
    {
        public const string Table = "dashboard_sql_database_deletions";
        public static string Key(DatabaseWorkspaceDeletion record)
        {
            if (new[] { record.OrganizationId, record.ProjectId, record.DatabaseId }.Any(id => !TerraScaleIds.IsValid(id)))
            {
                throw new IOException("Database cleanup marker scope is invalid.");
            }

            return record.OrganizationId + "/" + record.ProjectId + "/" + record.DatabaseId;
        }
        public static DatabaseWorkspaceDeletion Read(StateRecord row)
        {
            var record = AccountService.Read<DatabaseWorkspaceDeletion>(row);
            if (new[] { record.OrganizationId, record.ProjectId, record.DatabaseId }.Any(id => !TerraScaleIds.IsValid(id)) ||
                row.Id != Key(record))
            {
                throw new IOException("Database cleanup marker identity is invalid.");
            }

            return record;
        }

        public static async Task RememberAsync(IDashboardState state, DatabaseWorkspaceDeletion record, CancellationToken cancellationToken)
        {
            var key = Key(record);
            if (await IsDeletedAsync(state, record, cancellationToken))
            {
                return;
            }

            try { await state.PutAsync(Table, key, JsonSerializer.SerializeToElement(record), StatePrecondition.Absent, cancellationToken); }
            catch (StateConflictException)
            {
                if (!await IsDeletedAsync(state, record, cancellationToken))
                {
                    throw;
                }
            }
        }
        public static async Task<bool> IsDeletedAsync(IDashboardState state, DatabaseWorkspaceDeletion record, CancellationToken cancellationToken)
        {
            if (await state.GetAsync(Table, Key(record), cancellationToken) is not { } row)
            {
                return false;
            }

            if (Read(row) != record)
            {
                throw new IOException("Database cleanup marker scope is invalid.");
            }

            return true;
        }

        public static async Task ReplayAsync(IDashboardState state, CancellationToken cancellationToken)
        {
            await foreach (var row in DashboardStatePages.ReadAsync(state, Table, new Dictionary<string, string>(), null, cancellationToken))
            {
                var record = Read(row);
                await SqlWorkspace.EraseAsync(state, Scope(record), cancellationToken);
            }
        }

        public static async Task EraseMarkersAsync(IDashboardState state, string organizationId, string? projectId, CancellationToken cancellationToken)
        {
            var scope = new Dictionary<string, string> { ["OrganizationId"] = organizationId };
            if (projectId is not null)
            {
                scope["ProjectId"] = projectId;
            }

            await foreach (var row in DashboardStatePages.ReadAsync(state, Table, scope, null, cancellationToken))
            {
                Read(row);
                await state.DeleteAsync(Table, row.Id, row.Version, cancellationToken);
            }
        }

        public static async Task TryEraseAsync(IDashboardState state, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken)
        {
            try { await SqlWorkspace.EraseAsync(state, Scope(new(organizationId, projectId, databaseId)), cancellationToken); }
            // The terminal marker is durable; cleanup failure must not invite a repeat remote delete.
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !cancellationToken.IsCancellationRequested) { }
        }

        public static async Task DemandActiveAsync(IDashboardState state, SqlWorkspaceRecord record, CancellationToken cancellationToken)
        {
            if (await IsDeletedAsync(state, new(record.OrganizationId, record.ProjectId, record.DatabaseId), cancellationToken))
            {
                throw Unavailable();
            }

            var row = await state.GetAsync(AccountService.OrganizationsTable, record.OrganizationId, cancellationToken);
            if (row is null || row.Id != record.OrganizationId || AccountService.IsDeletionFence(row))
            {
                throw Unavailable();
            }

            var organization = AccountService.Read<OrganizationRecord>(row);
            if (organization.Id != record.OrganizationId || organization.DeletedProjectIds.Contains(record.ProjectId) ||
                !organization.Projects.TryGetValue(record.ProjectId, out var project) || project.Id != record.ProjectId ||
                !project.Databases.ContainsKey(record.DatabaseId))
            {
                throw Unavailable();
            }
        }
        private static Dictionary<string, string> Scope(DatabaseWorkspaceDeletion record)
        {
            return new()
            { ["OrganizationId"] = record.OrganizationId, ["ProjectId"] = record.ProjectId, ["DatabaseId"] = record.DatabaseId };
        }

        private static AccountAccessException Unavailable()
        {
            return new("The SQL workspace database is unavailable.", 404);
        }
    }
}
