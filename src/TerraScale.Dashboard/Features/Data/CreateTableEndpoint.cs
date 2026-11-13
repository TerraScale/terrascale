using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed class CreateTableEndpoint(DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/data/sql/tables");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, cancellationToken);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "sql"), Results.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            try
            {
                DatabaseService.Database(context, id);
                if (!context.CanWriteProjectData)
                {
                    throw new AccountAccessException("Write access is required to change data or schema. Your role can run read-only statements.");
                }

                var parsed = CreateTableSql.Parse(form);
                if (!parsed.Accepted)
                {
                    return await RejectAsync(http, context, id, parsed, form["sql"].ToString(), StatusCodes.Status422UnprocessableEntity);
                }

                return form["intent"].ToString() == "editor"
                    ? await OpenAsync(http, context, id, form, parsed, cancellationToken)
                    : await CreateAsync(http, context, id, form, parsed, cancellationToken);
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == StatusCodes.Status404NotFound)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }

                var parsed = CreateTableSql.Parse(form);
                var denied = DatabaseResults.Status(error) == StatusCodes.Status403Forbidden;
                return await DataEndpoints.PageAsync(http, context, id, data, saved, DatabaseResults.Message(error), denied ? "" : "create-table",
                    form["sql"].ToString(), status: DatabaseResults.Status(error), tableDraft: denied ? null : parsed.Draft);
            }
        }

        private async Task<IResult> RejectAsync(HttpContext http, AccountContext context, string id, CreateTableResult parsed, string sql, int status)
        {
            return DatabaseResults.WantsHtml(http)
                ? await DataEndpoints.PageAsync(http, context, id, data, saved, parsed.Error, "create-table", sql, status: status, tableDraft: parsed.Draft)
                : Results.Problem(parsed.Error, statusCode: status);
        }

        private async Task<IResult> OpenAsync(HttpContext http, AccountContext context, string id, IFormCollection form, CreateTableResult parsed, CancellationToken cancellationToken)
        {
            var sql = CreateTableSql.Statement(parsed.Draft);
            var runs = http.RequestServices.GetRequiredService<SqlRuns>();
            var history = await HistoryAsync(http, context, id, form, cancellationToken);
            try
            {
                await runs.SaveAsync(context, id, sql, history, null, "", StatusCodes.Status200OK, cancellationToken);
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !cancellationToken.IsCancellationRequested)
            {
                return await DataEndpoints.PageAsync(http, context, id, data, saved, sql: sql,
                    sqlNotice: "The statement is in the editor, but it could not be saved. Refreshing loads the previous draft.");
            }

            return DatabaseResults.WantsHtml(http)
                ? HttpRedirects.SeeOther(DatabaseService.Path(id, "sql"))
                : Results.Json(new { sql });
        }

        private async Task<IResult> CreateAsync(HttpContext http, AccountContext context, string id, IFormCollection form, CreateTableResult parsed, CancellationToken cancellationToken)
        {
            var sql = CreateTableSql.Statement(parsed.Draft);
            DatabaseSqlResult result;
            try
            {
                result = await data.SqlAsync(context, id, sql, true, cancellationToken);
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                if (DatabaseResults.Status(error) == StatusCodes.Status404NotFound)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }

                return await RejectAsync(http, context, id, parsed with { Error = DatabaseResults.Message(error) }, form["sql"].ToString(), DatabaseResults.Status(error));
            }

            var history = SqlHistory.Run(await HistoryAsync(http, context, id, form, cancellationToken), sql);
            try
            {
                var receipt = await http.RequestServices.GetRequiredService<SqlRuns>().SaveAsync(context, id, sql, history, result, "", StatusCodes.Status200OK, cancellationToken);
                var location = DatabaseService.Path(id, "sql") + "?result=" + Uri.EscapeDataString(receipt) + "&created=" + Uri.EscapeDataString(parsed.Draft.Name);
                return DatabaseResults.WantsHtml(http)
                    ? HttpRedirects.SeeOther(location)
                    : Results.Json(new { columns = result.Columns, rows = result.Rows, rowsAffected = result.RowsAffected, lastInsertRowId = result.LastInsertRowId });
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !cancellationToken.IsCancellationRequested)
            {
                return await DataEndpoints.PageAsync(http, context, id, data, saved, sql: sql, sqlResult: result, createdTable: parsed.Draft.Name,
                    sqlNotice: "This outcome could not be saved for later viewing. Refreshing this page would send the statement again.");
            }
        }

        private static async Task<IReadOnlyList<string>> HistoryAsync(HttpContext http, AccountContext context, string id, IFormCollection form, CancellationToken cancellationToken)
        {
            var history = SqlHistory.Read(form["history"].ToString());
            if (history.Count > 0)
            {
                return history;
            }

            try
            {
                if (await http.RequestServices.GetRequiredService<SqlRuns>().ReadWorkspaceAsync(context, id, cancellationToken) is { } workspace)
                {
                    return workspace.History;
                }
            }
            catch (SqlWorkspaceUnavailableException)
            {
                return history;
            }

            return history;
        }
    }
}
