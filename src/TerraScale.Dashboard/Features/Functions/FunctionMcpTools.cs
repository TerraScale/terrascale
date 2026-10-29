using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Functions;

[McpServerToolType]
public sealed class FunctionMcpTools(IHttpContextAccessor http, FunctionBackend functions)
{
    [McpServerTool(Name = "list_functions", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a database's deployed functions. Functions require document read and write access, including reads.")]
    public async Task<McpFunctionList> List(string databaseId, string? organizationId = null, string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "")
    {
        var context = Scope(organizationId, projectId);
        var items = await functions.ListAsync(context, DatabaseService.Database(context, databaseId), Cancellation);
        var page = CursorPaging.Page(items, item => item.Name, CursorPaging.Scope(context, "functions", databaseId), limit, after);
        return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
    }

    [McpServerTool(Name = "get_function", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read a function's source, trigger, schedule and HTTPS invoke URL. Does not invoke the function. Requires document read and write access.")]
    public async Task<McpFunctionDetail> Get(string databaseId, string name, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        var database = DatabaseService.Database(context, databaseId);
        var function = await functions.ReadAsync(context, database, name, Cancellation)
            ?? throw new AccountAccessException("This function does not exist.", 404);
        return new(function, FunctionBackend.InvokeUrl(context, database, name).AbsoluteUri);
    }

    [McpServerTool(Name = "list_function_logs", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read a function's latest 25 invocation records, newest first, with console output and errors. Requires document read and write access.")]
    public async Task<McpFunctionLogs> Logs(string databaseId, string name, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        return new(await functions.LogsAsync(context, DatabaseService.Database(context, databaseId), name, Cancellation));
    }

    [McpServerTool(Name = "list_function_deliveries", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read change-trigger delivery attempts for a function, including pending retries and errors. Requires document read and write access.")]
    public async Task<McpFunctionDeliveries> Deliveries(string databaseId, string name, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        return new(await functions.DeliveriesAsync(context, DatabaseService.Database(context, databaseId), name, Cancellation));
    }

    [McpServerTool(Name = "delete_function", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Permanently delete a function with its trigger, schedule and logs. Obtain explicit user consent; confirmName must equal name. Requires project data write access.")]
    public async Task<McpFunctionDeleted> Delete(string databaseId, string name, string confirmName, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        var database = DatabaseService.Database(context, databaseId);
        if (name.Length == 0 || !string.Equals(name, confirmName, StringComparison.Ordinal))
            throw new AccountAccessException("Confirm the function name before deleting it.", 422);
        if (!await functions.DeleteAsync(context, database, name, Cancellation))
            throw new AccountAccessException("This function does not exist.", 404);
        return new(true);
    }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record McpFunctionList(IReadOnlyList<FunctionRecord> Functions, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
public sealed record McpFunctionDetail(FunctionRecord Function, string InvokeUrl);
public sealed record McpFunctionLogs(IReadOnlyList<FunctionInvocation> Logs);
public sealed record McpFunctionDeliveries(IReadOnlyList<TerraBaseChangeWebhookDelivery> Deliveries);
public sealed record McpFunctionDeleted(bool Deleted);
