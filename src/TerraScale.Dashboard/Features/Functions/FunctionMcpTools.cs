using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using TerraScale.TerraBase.Contracts;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Functions
{
    [McpServerToolType]
    public sealed class FunctionMcpTools(IHttpContextAccessor http, FunctionBackend functions)
    {
        [McpServerTool(Name = "list_functions", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a database's deployed functions. Use deploy_function to create or replace and invoke_function to run. Listing requires SQL read and write access.")]
        public async Task<McpFunctionList> List(string databaseId, string? organizationId = null, string? projectId = null,
            [Description(CursorPaging.Description)] int limit = 100, string after = "")
        {
            var context = Scope(organizationId, projectId);
            var items = await functions.ListAsync(context, DatabaseService.Database(context, databaseId), Cancellation);
            var page = CursorPaging.Page(items, item => item.Name, CursorPaging.Scope(context, "functions", databaseId), limit, after);
            return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
        }

        [McpServerTool(Name = "get_function", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read a function's source, schedule and HTTPS invoke URL. Use deploy_function to replace it and invoke_function to run it. Requires SQL read and write access.")]
        public async Task<McpFunctionDetail> Get(string databaseId, string name, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            var database = DatabaseService.Database(context, databaseId);
            var function = await functions.ReadAsync(context, database, name, Cancellation)
                ?? throw new AccountAccessException("This function does not exist.", 404);
            return new(function, FunctionBackend.InvokeUrl(context, database, name));
        }

        [McpServerTool(Name = "list_function_logs", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read a function's latest 25 invocation records, newest first, with console output and errors. invoke_function returns the newest log alongside its response when SQL write access is available. Requires SQL read and write access.")]
        public async Task<McpFunctionLogs> Logs(string databaseId, string name, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            return new(await functions.LogsAsync(context, DatabaseService.Database(context, databaseId), name, Cancellation));
        }

        [McpServerTool(Name = "get_functions_reference", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read the complete JavaScript Functions runtime contract, limits and working examples before deploying or invoking. Requires no data permission.")]
        public McpFunctionReference Reference() => new(FunctionReference.Text);

        [McpServerTool(Name = "deploy_function", ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Call get_functions_reference first. Create or replace a JavaScript handler(event) in this database, with an optional five-field UTC cron. Omit cron to remove the schedule. Returns name, cron, updated_at and invoke_url, or a structured validation error retaining the runner message and available line/column. Requires SQL write access.")]
        public async Task<CallToolResult> Deploy(string databaseId, string name, string source, string? cron = null, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            var database = DatabaseService.Database(context, databaseId);
            try
            {
                var function = await functions.DeployAsync(context, database, name, source, cron, Cancellation);
                return FunctionMcpOutput.Deployed(function.Name, function.Cron, function.UpdatedAt, FunctionBackend.InvokeUrl(context, database, name));
            }
            catch (TerraBaseValidationException error)
            {
                return FunctionMcpOutput.Invalid(error.ErrorCode, error.Message);
            }
            catch (AccountAccessException error) when (error.StatusCode == 400)
            {
                return FunctionMcpOutput.Invalid("validation", error.Message);
            }
        }

        [McpServerTool(Name = "invoke_function", ReadOnly = false, Destructive = false, OpenWorld = false, Idempotent = false, UseStructuredContent = true), Description("Call get_functions_reference first. Run a deployed function once with body as a JSON value (default null). Returns status, content_type, parsed JSON or text body, and the newest invocation log if you have SQL write access; otherwise log is null with log_note. Concurrent calls or cron may produce a newer log. Requires SQL read only; the function itself may write SQL. event.query is empty through MCP and the dashboard test run; use the HTTPS invoke URL for query parameters.")]
        public async Task<McpFunctionRun> Invoke(string databaseId, string name, JsonElement? body = null, string? organizationId = null, string? projectId = null)
        {
            var context = Scope(organizationId, projectId);
            return await functions.InvokeForMcpAsync(context, DatabaseService.Database(context, databaseId), name, body?.GetRawText() ?? "null", Cancellation);
        }

        [McpServerTool(Name = "delete_function", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Return step_up_required with the resource and confirmationUrl. MCP leaves the resource intact; finish deletion in the signed-in dashboard, which applies its authorization and confirmation requirements.")]
        public McpStepUpRequired Delete(string databaseId, string name, string confirmName, string? organizationId = null, string? projectId = null)
        {
            var scope = DatabaseService.Scope(Scope(organizationId, projectId));
            return McpDeletion.Required("delete_function", name, scope, databaseId);
        }

        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    }

    public sealed record McpFunctionList(IReadOnlyList<FunctionRecord> Functions, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
    public sealed record McpFunctionDetail(FunctionRecord Function, string InvokeUrl);
    public sealed record McpFunctionLogs(IReadOnlyList<FunctionInvocation> Logs);
}
