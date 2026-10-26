using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Dashboard.Platform.Mcp.McpScope;

namespace TerraScale.Dashboard.Features.Databases;

[McpServerToolType]
public sealed class DatabaseMcpTools(IHttpContextAccessor http, AccountService accounts, DatabaseService databases, IDatabaseBackend backend, BackupService backups)
{
    [McpServerTool(Name = "list_databases", Title = "List databases", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the databases in a project with their status and region.")]
    public async Task<McpDatabaseList> ListDatabases(string? organizationId = null, string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "")
    {
        var context = Scope(organizationId, projectId);
        DatabaseService.Scope(context);
        await databases.SyncAsync(context, Cancellation);
        var page = CursorPaging.Page(context.Project!.Databases.Values.Select(DatabaseOutput), db => db.Id,
            CursorPaging.Scope(context, "databases"), limit, after);
        return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
    }

    [McpServerTool(Name = "get_database", Title = "Get database", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Show one database's status, regions, storage totals, and backup state.")]
    public async Task<McpDatabaseDetail> GetDatabase(string databaseId, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        var db = DatabaseService.Database(context, databaseId);
        var placement = await databases.PlacementAsync(DatabaseService.Regions(db), Cancellation, DatabaseService.Regions(db));
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
            if (listing.Policy is { } policy) backupsEnabled = !policy.Disabled && policy.FullBackupCron.Length > 0;
            else unavailable = unavailable is null ? "Backup scheduling measurements are unavailable." : unavailable + " Backup scheduling measurements are unavailable.";
        }
        catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
            (error is not OperationCanceledException || !Cancellation.IsCancellationRequested))
        { unavailable = unavailable is null ? "Backup measurements are unavailable." : unavailable + " Backup measurements are unavailable."; }
        return new(db.Id, db.Name, db.Status, db.Region, DatabaseService.Regions(db), db.CreatedAt, db.AllRegions, db.Endpoint,
            // MCP labels are an existing response contract, independent of page display casing.
            placement.Select(region => region.Code.ToUpperInvariant() switch { "SFO" => "SFO · San Francisco", "GRU" => "GRU · São Paulo", "" => "Not configured", var code => code }).ToArray(), contents?.Count, contents?.Sum(item => item.Documents), contents?.Sum(item => item.Bytes),
            unavailable, backupsEnabled, context.CanManage, db.Status == "ready" && context.CanManage && context.Organization!.CanProvision(context.UserId));
    }

    [McpServerTool(Name = "create_database", Title = "Create database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Provision a new database in a project. Requires an owner or admin role and a plan with database capacity. Without replica regions, the API uses every currently serving region; explicit placement needs at least two regions including home. allRegions also follows future regions; it excludes replica regions, so pass at most the home region with it.")]
    public async Task<McpDatabase> CreateDatabase(string name, string? region = null, string[]? regions = null, bool allRegions = false, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        DatabaseService.Scope(context);
        if (regions is not null && !allRegions) DatabaseService.RequireReplicas(regions);
        var placement = regions is { Length: > 0 } ? regions : [string.IsNullOrEmpty(region) ? context.Project!.Region : region];
        return DatabaseOutput(await databases.CreateAsync(context, name, placement, allRegions, Cancellation));
    }

    [McpServerTool(Name = "branch_database", Title = "Branch database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create a new database that copies a ready database's documents, schemas, and indexes.")]
    public async Task<McpDatabase> BranchDatabase(string databaseId, string name = "", string? organizationId = null, string? projectId = null) =>
        DatabaseOutput(await databases.BranchAsync(Scope(organizationId, projectId), databaseId, name, Cancellation));

    [McpServerTool(Name = "set_database_regions", Title = "Set database regions", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Choose at least two regions including its home, or allRegions to copy it to every serving region, including regions that join later. Selecting every current region alone does not follow future regions. allRegions excludes replica regions; choosing regions turns it off. The home region never changes. Requires an owner or admin role.")]
    public async Task<McpDatabase> SetRegions(string databaseId, string[]? regions = null, bool allRegions = false, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        await databases.SetRegionsAsync(context, databaseId, regions ?? [], allRegions, Cancellation);
        var fresh = await accounts.GetContextAsync(context.Session, Cancellation);
        var db = fresh.Organizations.First(org => org.Id == context.Organization!.Id).Projects[context.Project!.Id].Databases[databaseId];
        return DatabaseOutput(db);
    }

    [McpServerTool(Name = "delete_database", Title = "Delete database", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Permanently delete a database and all of its data. Requires confirmName to equal the database name.")]
    public async Task<McpDeleted> DeleteDatabase(string databaseId, string confirmName = "", string? organizationId = null, string? projectId = null, string? confirmation = null)
    {
        await databases.DeleteAsync(Scope(organizationId, projectId), databaseId, Alias(confirmName, confirmation, "confirmName"), Cancellation);
        return new(true);
    }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

    private static McpDatabase DatabaseOutput(DatabaseRecord db) => new(db.Id, db.Name, db.Status, db.Region, DatabaseService.Regions(db), db.CreatedAt, db.AllRegions);
}

public record McpDatabase(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt, bool AllRegions);
public sealed record McpDatabaseList(IReadOnlyList<McpDatabase> Databases, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
public sealed record McpDatabaseDetail(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt, bool AllRegions,
    string Endpoint, IReadOnlyList<string> Regions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Collections,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Documents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unavailable, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BackupsEnabled, bool CanManage, bool CanBranch);
