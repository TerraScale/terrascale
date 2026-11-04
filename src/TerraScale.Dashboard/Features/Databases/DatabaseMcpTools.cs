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
    public sealed class DatabaseMcpTools(IHttpContextAccessor http, DatabaseService databases, IDatabaseBackend backend, BackupService backups)
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

        [McpServerTool(Name = "get_database", Title = "Get database", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Show one database's status, regions, storage totals, and backup state.")]
        public async Task<McpDatabaseDetail> GetDatabase(string databaseId, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            await databases.SyncAsync(context, Cancellation);
            var db = DatabaseService.Database(context, databaseId);
            var output = await DatabaseOutput(db);
            var placement = await databases.ConfiguredPlacementAsync(output.Placement, Cancellation);
            IReadOnlyList<CollectionStats>? contents = null;
            string? unavailable = null;
            try
            {
                var reported = await backend.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, db, Cancellation), db.Id, Cancellation);
                DatabaseService.ValidateContents(reported);
                contents = reported;
            }
            catch (Exception error) when ((DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException or JsonException or OverflowException or OperationCanceledException) &&
                (error is not OperationCanceledException || !Cancellation.IsCancellationRequested))
            { unavailable = "Database storage measurements are unavailable."; }
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
                [.. placement.Select(region => region.Code.ToUpperInvariant() switch { "SFO" => "SFO · San Francisco", "GRU" => "GRU · São Paulo", "" => "Not configured", var code => code })], contents?.Count, contents?.Sum(item => item.Documents), contents?.Sum(item => item.Bytes),
                unavailable, backupsEnabled, context.CanManage, db.Status == "ready" && context.CanManage && context.Organization!.CanProvision(context.UserId), db.ExpiresAt);
        }

        [McpServerTool(Name = "create_database", Title = "Create database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create a database in its home region with an optional TTL in hours (one minute to 365 days).")]
        public async Task<McpDatabase> CreateDatabase(string name, string? region = null, string? organizationId = null, string? projectId = null, decimal? ttlHours = null)
        {
            var context = Scope(organizationId, projectId);
            DatabaseService.Scope(context);
            return await DatabaseOutput(await databases.CreateAsync(context, name, string.IsNullOrEmpty(region) ? context.Project!.Region : region, Cancellation, ttlHours));
        }

        [McpServerTool(Name = "fork_database", Title = "Fork database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Fork a database now or at an ISO 8601 point in time, with an optional TTL in hours.")]
        public async Task<McpDatabase> ForkDatabase(string databaseId, string name, string? organizationId = null, string? projectId = null, string? pointInTime = null, decimal? ttlHours = null)
        {
            return await DatabaseOutput(await databases.BranchAsync(Scope(organizationId, projectId), databaseId, name, Cancellation, ttlHours, pointInTime));
        }

        [McpServerTool(Name = "set_database_ttl", Title = "Set database TTL", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Set expiry from now using ttlHours (one minute to 365 days), or clear expiry with clear=true.")]
        public async Task<McpDatabase> SetDatabaseTtl(string databaseId, decimal? ttlHours = null, bool clear = false, string? organizationId = null, string? projectId = null)
        {
            if (clear == (ttlHours is not null))
            {
                throw new AccountAccessException("Supply ttlHours or clear=true, exclusively.", 422);
            }
            return await DatabaseOutput(await databases.SetTtlAsync(Scope(organizationId, projectId), databaseId, ttlHours, Cancellation));
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

        private async Task<McpDatabase> DatabaseOutput(DatabaseRecord db)
        {
            IReadOnlyList<DatabaseRegion> catalog = [];
            // Expansion is optional: discovery must not turn a committed mutation or a readable list into an error.
            if (db.AllRegions)
            {
                try { catalog = await backend.RegionsAsync(Cancellation); }
                catch (Exception error) when (!Cancellation.IsCancellationRequested &&
                    (error is HttpRequestException or JsonException or OperationCanceledException || DatabaseEndpoints.KnownFailure(error)))
                { }
            }

            return new(db.Id, db.Name, db.Status, db.Region, DatabaseService.ServingRegions(db, catalog), db.CreatedAt, db.AllRegions, db.ExpiresAt);
        }
    }

    public record McpDatabase(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt, bool AllRegions, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ExpiresAt);
    public sealed record McpDatabaseList(IReadOnlyList<McpDatabase> Databases, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
    public sealed record McpDatabaseDetail(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt, bool AllRegions,
        string Endpoint, IReadOnlyList<string> Regions,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Collections,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Documents,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unavailable, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BackupsEnabled, bool CanManage, bool CanBranch, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ExpiresAt);
}
