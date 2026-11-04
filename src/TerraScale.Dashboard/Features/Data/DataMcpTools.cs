using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.Json;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Data
{
    [McpServerToolType]
    public sealed class DataMcpTools(IHttpContextAccessor http, DataService data, SavedQueryService saved)
    {
        [McpServerTool(Name = "execute_sql", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Execute one read-only SQL statement against the database's tables. SQL is limited to 16 KiB; bound parameters are unsupported.")]
        public Task<DatabaseSqlResult> Sql(string databaseId, string sql, string? organizationId = null, string? projectId = null)
        {
            return data.SqlAsync(Scope(organizationId, projectId), databaseId, sql, false, Cancellation);
        }

        [McpServerTool(Name = "check_sql", Title = "Check SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Check SQL input limits without running it: blank input, size, statement boundaries, parameters and transaction commands. Does not validate SQL syntax, tables or permissions.")]
        public SqlAnalysis CheckSql(string databaseId, string sql, string? organizationId = null, string? projectId = null)
        {
            DatabaseService.Database(Scope(organizationId, projectId), databaseId);
            return SqlValidation.Analyze(sql);
        }

        [McpServerTool(Name = "run_sql", Title = "Run SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Run one read-only SQL query against the database's tables. SQL is limited to 16 KiB; bound parameters are unsupported.")]
        public async Task<McpSqlResponse> RunSql(string databaseId, string sql, string? organizationId = null, string? projectId = null)
        {
            var result = await data.SqlAsync(Scope(organizationId, projectId), databaseId, sql, false, Cancellation);
            return new(new(result.Columns, result.Rows));
        }

        [McpServerTool(Name = "list_saved_queries", Title = "List saved queries", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the SQL queries saved in a project.")]
        public async Task<McpSavedQueries> SavedQueries(string? organizationId = null, string? projectId = null,
            [Description(CursorPaging.Description)] int limit = 100, string after = "")
        {
            var context = Scope(organizationId, projectId);
            var page = CursorPaging.Page((await saved.ListAsync(context, Cancellation)).Select(SavedQueryOutput), query => query.Id,
                CursorPaging.Scope(context, "saved-queries"), limit, after);
            return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
        }

        [McpServerTool(Name = "save_query", Title = "Save query", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Save a read-only SQL query for everyone in the project. Requires write access.")]
        public async Task<McpSavedQuery> SaveQuery(string name, string sql, string? organizationId = null, string? projectId = null)
        {
            return SavedQueryOutput(await saved.SaveAsync(Scope(organizationId, projectId), "", name, sql, Cancellation));
        }

        [McpServerTool(Name = "delete_saved_query", Title = "Delete saved query", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Delete a saved query from the project. Requires write access.")]
        public async Task<McpDeleted> DeleteSavedQuery(string queryId, string? organizationId = null, string? projectId = null)
        {
            await saved.DeleteAsync(Scope(organizationId, projectId), "", queryId, Cancellation);
            return new(true);
        }

        private static McpSavedQuery SavedQueryOutput(SavedQuery query)
        {
            return new(query.Id, query.Name, query.Sql, query.CreatedAt);
        }

        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    }

    public sealed record McpSqlRows(IReadOnlyList<DatabaseSqlColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows);
    public sealed record McpSqlResponse(McpSqlRows Result);
    public sealed record McpSavedQuery(string Id, string Name, string Sql, DateTimeOffset CreatedAt);
    public sealed record McpSavedQueries(IReadOnlyList<McpSavedQuery> Queries, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
}
