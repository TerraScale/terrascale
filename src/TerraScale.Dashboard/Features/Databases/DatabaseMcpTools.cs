using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Platform.Mcp;
using static TerraScale.Dashboard.Platform.Mcp.McpScope;

namespace TerraScale.Dashboard.Features.Databases
{
    [McpServerToolType]
    public sealed class DatabaseMcpTools(IHttpContextAccessor http, DatabaseService databases, BackupService backups)
    {
        [McpServerTool(Name = "list_databases", Title = "List databases", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the databases in a project with their status and region.")]
        public async Task<McpDatabaseList> ListDatabases(string? organizationId = null, string? projectId = null,
            [Description(CursorPaging.Description)] int limit = 100, string after = "")
        {
            var context = Scope(organizationId, projectId);
            DatabaseService.Scope(context);
            await databases.SyncAsync(context, Cancellation);
            var page = CursorPaging.Page(context.Project!.Databases.Values, db => db.Id,
                CursorPaging.Scope(context, "databases"), limit, after);
            return new(await Task.WhenAll(page.Items.Select(DatabaseOutput)), page.NextCursor, page.HasMore, page.Limit);
        }

        [McpServerTool(Name = "get_database", Title = "Get database", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Show one database's status, regions and backup state.")]
        public async Task<McpDatabaseDetail> GetDatabase(string databaseId, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            await databases.SyncAsync(context, Cancellation);
            var db = DatabaseService.Database(context, databaseId);
            var output = await DatabaseOutput(db);
            var placement = await databases.ConfiguredPlacementAsync(output.Placement, Cancellation);
            string? unavailable = null;
            bool? backupsEnabled = null;
            try
            {
                var listing = await backups.ListAsync(context, db.Id, Cancellation);
                if (listing.Policy is { } policy)
                {
                    backupsEnabled = !policy.Disabled && policy.FullBackupCron.Length > 0;
                }
                else
                {
                    unavailable = unavailable is null ? "Backup scheduling measurements are unavailable." : unavailable + " Backup scheduling measurements are unavailable.";
                }
            }
            catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
                (error is not OperationCanceledException || !Cancellation.IsCancellationRequested))
            { unavailable = unavailable is null ? "Backup measurements are unavailable." : unavailable + " Backup measurements are unavailable."; }
            return new(db.Id, db.Name, db.Status, db.Region, output.Placement, db.CreatedAt, db.AllRegions, db.Endpoint,
                // MCP labels are an existing response contract, independent of page display casing.
                [.. placement.Select(region => region.Code.ToUpperInvariant() switch { "SFO" => "SFO · San Francisco", "GRU" => "GRU · São Paulo", "" => "Not configured", var code => code })],
                unavailable, backupsEnabled, context.CanManage, db.Status == "ready" && context.CanManage && context.Organization!.CanProvision(context.UserId));
        }

        [McpServerTool(Name = "create_database", Title = "Create database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Provision a new database in a project. It serves only in its home region: region, or the project's region when omitted. Requires an owner or admin role and a plan with database capacity.")]
        public async Task<McpDatabase> CreateDatabase(string name, string? region = null, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            DatabaseService.Scope(context);
            return await DatabaseOutput(await databases.CreateAsync(context, name, string.IsNullOrEmpty(region) ? context.Project!.Region : region, Cancellation));
        }

        [McpServerTool(Name = "branch_database", Title = "Branch database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create a new database that copies a ready database's tables and data.")]
        public async Task<McpDatabase> BranchDatabase(string databaseId, string name = "", string? organizationId = null, string? projectId = null)
        {
            return await DatabaseOutput(await databases.BranchAsync(Scope(organizationId, projectId), databaseId, name, Cancellation));
        }

        [McpServerTool(Name = "delete_database", Title = "Delete database", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Permanently delete a database and all of its data. Requires confirmName to equal the database name.")]
        public async Task<McpDeleted> DeleteDatabase(string databaseId, string confirmName = "", string? organizationId = null, string? projectId = null, string? confirmation = null)
        {
            await databases.DeleteAsync(Scope(organizationId, projectId), databaseId, Alias(confirmName, confirmation, "confirmName"), Cancellation);
            return new(true);
        }

        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

        private static Task<McpDatabase> DatabaseOutput(DatabaseRecord db)
        {
            return Task.FromResult(new McpDatabase(db.Id, db.Name, db.Status, db.Region, DatabaseService.ServingRegions(db), db.CreatedAt, db.AllRegions));
        }
    }

    public record McpDatabase(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt, bool AllRegions);
    public sealed record McpDatabaseList(IReadOnlyList<McpDatabase> Databases, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
    public sealed record McpDatabaseDetail(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt, bool AllRegions,
        string Endpoint, IReadOnlyList<string> Regions,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unavailable, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BackupsEnabled, bool CanManage, bool CanBranch);
}
