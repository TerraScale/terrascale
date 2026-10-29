using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public sealed record DatabaseWorkspaceDeletion(string OrganizationId, string ProjectId, string DatabaseId);

// One immutable, bounded terminal marker per database; never embedded in organization metadata.
public static class DatabaseWorkspaceCleanup
{
    public const string Collection = "dashboard_sql_database_deletions";
    public static string Key(DatabaseWorkspaceDeletion record)
    {
        if (new[] { record.OrganizationId, record.ProjectId, record.DatabaseId }.Any(id => !TerraScaleIds.IsValid(id)))
            throw new IOException("Database cleanup marker scope is invalid.");
        return record.OrganizationId + "/" + record.ProjectId + "/" + record.DatabaseId;
    }
    public static DatabaseWorkspaceDeletion Read(TerraBaseDocument document)
    {
        var record = AccountService.Read<DatabaseWorkspaceDeletion>(document);
        if (new[] { record.OrganizationId, record.ProjectId, record.DatabaseId }.Any(id => !TerraScaleIds.IsValid(id)) ||
            document.Id != Key(record) && document.Id != TerraBaseDashboardState.DocumentId(Collection, Key(record)))
            throw new IOException("Database cleanup marker identity is invalid.");
        return record;
    }

    public static async Task RememberAsync(IDashboardState state, DatabaseWorkspaceDeletion record, CancellationToken cancellationToken)
    {
        var key = Key(record);
        if (await IsDeletedAsync(state, record, cancellationToken)) return;
        try { await state.PutAsync(Collection, key, JsonSerializer.SerializeToElement(record), TerraBasePrecondition.Absent, cancellationToken); }
        catch (TerraBasePreconditionFailedException)
        {
            if (!await IsDeletedAsync(state, record, cancellationToken)) throw;
        }
    }
    public static async Task<bool> IsDeletedAsync(IDashboardState state, DatabaseWorkspaceDeletion record, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(Collection, Key(record), cancellationToken) is not { } document) return false;
        if (Read(document) != record) throw new IOException("Database cleanup marker scope is invalid.");
        return true;
    }

    public static async Task ReplayAsync(IDashboardState state, CancellationToken cancellationToken)
    {
        await foreach (var document in DashboardStatePages.ReadAsync(state, Collection, new Dictionary<string, string>(), null, cancellationToken))
        {
            var record = Read(document);
            await SqlWorkspace.EraseAsync(state, Scope(record), cancellationToken);
        }
    }

    public static async Task EraseMarkersAsync(IDashboardState state, string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        var scope = new Dictionary<string, string> { ["OrganizationId"] = organizationId };
        if (projectId is not null) scope["ProjectId"] = projectId;
        await foreach (var document in DashboardStatePages.ReadAsync(state, Collection, scope, null, cancellationToken))
        {
            Read(document);
            await state.DeleteListedAsync(Collection, document, cancellationToken);
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
        if (await IsDeletedAsync(state, new(record.OrganizationId, record.ProjectId, record.DatabaseId), cancellationToken)) throw Unavailable();
        var document = await state.GetAsync(AccountService.OrganizationsCollection, record.OrganizationId, cancellationToken);
        if (document is null || document.Id != record.OrganizationId || AccountService.IsDeletionFence(document)) throw Unavailable();
        var organization = AccountService.Read<OrganizationRecord>(document);
        if (organization.Id != record.OrganizationId || organization.DeletedProjectIds.Contains(record.ProjectId) ||
            !organization.Projects.TryGetValue(record.ProjectId, out var project) || project.Id != record.ProjectId ||
            !project.Databases.ContainsKey(record.DatabaseId)) throw Unavailable();
    }
    private static Dictionary<string, string> Scope(DatabaseWorkspaceDeletion record) => new()
    { ["OrganizationId"] = record.OrganizationId, ["ProjectId"] = record.ProjectId, ["DatabaseId"] = record.DatabaseId };
    private static AccountAccessException Unavailable() => new("The SQL workspace database is unavailable.", 404);
}
