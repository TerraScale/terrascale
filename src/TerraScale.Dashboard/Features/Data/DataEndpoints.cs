using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.Features.Data
{
    using NotFoundEndpoints = TerraScale.Dashboard.Features.NotFound.NotFoundEndpoints;
    public static class DataEndpoints
    {
        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved, string action)
        {
            if (action != "saved-queries/delete" && http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sqlBodyLimit)
            {
                sqlBodyLimit.MaxRequestBodySize = 3 * (16 * 1024 + 4 * 1024) + 4096;
            }
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "sql"), TypedResults.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            try
            {
                if (!action.StartsWith("saved-queries", StringComparison.Ordinal) || id.Length > 0)
                {
                    DatabaseService.Database(context, id);
                }

                if (action == "sql/check")
                {
                    return TypedResults.Json(SqlValidation.Diagnostics(form["sql"].ToString()));
                }
                if (action is "sql" or "saved-queries")
                {
                    var problem = SqlValidation.Diagnostics(form["sql"].ToString()).FirstOrDefault(item => item.Severity == "error");
                    if (problem is not null)
                    {
                        return await Refused(StatusCodes.Status422UnprocessableEntity, problem.Message);
                    }

                    if (action == "sql")
                    {
                        if (DatabaseResults.WantsHtml(http) || http.Request.IsDatastarRequest)
                        {
                            return await RunAsync(http, context, id, form, data, saved);
                        }

                        var result = await data.SqlAsync(context, id, form["sql"].ToString(), true, http.RequestAborted);
                        return TypedResults.Json(new { columns = result.Columns, rows = result.Rows, rowsAffected = result.RowsAffected, lastInsertRowId = result.LastInsertRowId });
                    }
                    var stored = await saved.SaveAsync(context, id, form["name"].ToString(), form["sql"].ToString(), http.RequestAborted);
                    return stored switch
                    {
                        SavedQuery when http.Request.IsDatastarRequest => await PageAsync(http, context, id, data, saved),
                        SavedQuery savedQuery => DatabaseEndpoints.WantsHtml(http)
                            ? HttpRedirects.SeeOther(DatabaseService.Path(id, "sql") + "?savedQueryId=" + Uri.EscapeDataString(savedQuery.Id))
                            : TypedResults.Json(PublicSavedQuery(savedQuery), statusCode: 201),
                        DashboardForbidden failure => await Refused(StatusCodes.Status403Forbidden, failure.Message),
                        DashboardValidation failure => await Refused(StatusCodes.Status422UnprocessableEntity, failure.Message),
                        DashboardConflict failure => await Refused(StatusCodes.Status409Conflict, failure.Message),
                    };
                }
                if (action == "saved-queries/delete")
                {
                    var deleted = await saved.DeleteAsync(context, id, form["id"].ToString(), http.RequestAborted);
                    IResult? refusal = deleted switch
                    {
                        Done => null,
                        DashboardNotFound failure => DatabaseResults.WantsHtml(http)
                            ? NotFoundEndpoints.Page(http, context)
                            : TypedResults.Problem(failure.Message, statusCode: StatusCodes.Status404NotFound),
                        DashboardForbidden failure => await Refused(StatusCodes.Status403Forbidden, failure.Message),
                    };
                    if (refusal is not null)
                    {
                        return refusal;
                    }

                    if (http.Request.IsDatastarRequest)
                    {
                        return await PageAsync(http, context, id, data, saved);
                    }
                }
                return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "sql")) : TypedResults.NoContent();
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                if ((!DatabaseResults.WantsHtml(http) && !http.Request.IsDatastarRequest) || DatabaseResults.Status(error) == 404)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }

                return await PageAsync(http, context, id, data, saved, DatabaseEndpoints.Message(error), action == "saved-queries" ? "save-query" : action == "saved-queries/delete" ? "delete-query-" + form["id"] : "",
                    form["sql"].ToString(), queryName: form["name"].ToString(), status: DatabaseEndpoints.Status(error));
            }

            async Task<IResult> Refused(int status, string message)
            {
                if (!DatabaseResults.WantsHtml(http) && !http.Request.IsDatastarRequest)
                {
                    return TypedResults.Problem(message, statusCode: status);
                }

                return await PageAsync(http, context, id, data, saved, message, action == "saved-queries" ? "save-query" : action == "saved-queries/delete" ? "delete-query-" + form["id"] : "",
                    form["sql"].ToString(), queryName: form["name"].ToString(), status: status);
            }
        }

        private static async Task<IResult> RunAsync(HttpContext http, AccountContext context, string id, IFormCollection form, DataService data, SavedQueryService saved)
        {
            var sql = form["sql"].ToString();
            DatabaseSqlResult? result = null;
            Exception? failure = null;
            try { result = await data.SqlAsync(context, id, sql, true, http.RequestAborted); }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                if (DatabaseResults.Status(error) == 404)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }

                failure = error;
            }
            var message = failure is null ? "" : DatabaseEndpoints.Message(failure);
            var status = failure is null ? 200 : DatabaseEndpoints.Status(failure);
            try
            {
                var receipt = await http.RequestServices.GetRequiredService<SqlRuns>().SaveAsync(context, id, sql,
                    SqlHistory.Run(SqlHistory.Read(form["history"].ToString()), sql), result, message, status, http.RequestAborted);
                var url = DatabaseService.Path(id, "sql") + "?result=" + Uri.EscapeDataString(receipt.Id);
                return http.Request.IsDatastarRequest
                    ? await PageAsync(http, context, id, data, saved, receipt.Error, sql: sql, sqlResult: receipt.Result, sqlNotice: RunNotice(receipt), runUrl: url)
                    : HttpRedirects.SeeOther(url);
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !http.RequestAborted.IsCancellationRequested)
            {
                return await PageAsync(http, context, id, data, saved, message, sql: sql, sqlResult: result, status: status,
                    sqlNotice: http.Request.IsDatastarRequest
                        ? "This outcome could not be saved for later viewing. The statement was not run again."
                        : "This outcome could not be saved for later viewing. Refreshing this page would send the statement again.",
                    runUrl: DatabaseService.Path(id, "sql"));
            }
        }

        internal static async Task<IResult> PageAsync(HttpContext http, AccountContext context, string id, DataService data, SavedQueryService saved,
            string formError = "", string dialog = "", string sql = "", DatabaseSqlResult? sqlResult = null, string queryName = "", int status = 200, string sqlNotice = "", string runUrl = "")
        {
            DatabaseRecord database;
            try { database = DatabaseService.Database(context, id); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var errorMessage = "";
            IReadOnlyList<SavedQuery> queries = [];
            SqlCatalog? catalog = null;
            var catalogError = "";
            IReadOnlyList<string> history = [];
            var submitted = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
            if (submitted is not null)
            {
                history = SqlHistory.Read(submitted["history"].ToString());
                if (sql.Length > 0 && http.Request.Path == "/api/v1/dashboard/data/sql")
                {
                    history = SqlHistory.Run(history, sql);
                }
            }

            try
            {
                queries = await saved.ListAsync(context, id, http.RequestAborted);
                if (submitted is not null)
                {
                    history = SqlHistory.Read(submitted["history"].ToString());
                    if (sql.Length > 0 && http.Request.Path == "/api/v1/dashboard/data/sql")
                    {
                        history = SqlHistory.Run(history, sql);
                    }
                }
                else
                {
                    try
                    {
                        if (await http.RequestServices.GetRequiredService<SqlRuns>().ReadWorkspaceAsync(context, id, http.RequestAborted) is { } workspace)
                        {
                            (sql, history) = (workspace.Draft, workspace.History);
                        }
                    }
                    catch (SqlWorkspaceUnavailableException)
                    {
                        history = [];
                    }
                }
                if (submitted is null && http.Request.Query["result"].ToString() is { Length: > 0 } receiptId)
                {
                    try
                    {
                        if (await http.RequestServices.GetRequiredService<SqlRuns>().ReadAsync(context, id, receiptId, http.RequestAborted) is { Receipt: { } run } workspace)
                        {
                            (sql, history, sqlResult, formError, status) = (workspace.Draft, workspace.History, run.Result, run.Error, run.Status);
                            sqlNotice = RunNotice(run);
                        }
                        else
                        {
                            sqlNotice = "This query result expired or was replaced by a newer run. The statement was not run again.";
                        }
                    }
                    catch (Exception error) when (SqlWorkspace.Unavailable(error) && !http.RequestAborted.IsCancellationRequested)
                    { sqlNotice = "This query result is unavailable right now. The statement was not run again; reload to retry."; }
                }
                if (submitted is null && http.Request.Query["savedQueryId"].ToString() is { Length: > 0 } savedQueryId
                    && queries.FirstOrDefault(query => query.Id == savedQueryId) is { } chosen)
                {
                    sql = chosen.Sql;
                }

                try { catalog = await data.CatalogAsync(context, id, http.RequestAborted); }
                catch (Exception error) when (!http.RequestAborted.IsCancellationRequested &&
                    (DatabaseResults.KnownFailure(error) || error is HttpRequestException or OperationCanceledException or JsonException))
                { catalogError = "SQL table suggestions are unavailable. You can still run queries. Retry to reload the catalog."; }
            }
            catch (Exception error) when (error is HttpRequestException || DatabaseResults.KnownFailure(error))
            {
                errorMessage = DatabaseResults.KnownFailure(error) ? DatabaseResults.Message(error) : "The live data plane is unavailable. Try again shortly.";
                if (DatabaseResults.KnownFailure(error) && DatabaseResults.Status(error) == 404)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }
            }
            if (http.Request.IsDatastarRequest && runUrl.Length > 0)
            {
                var scope = DatabaseService.Scope(context);
                runUrl = QueryHelpers.AddQueryString(QueryHelpers.AddQueryString(runUrl, "organizationId", scope.OrganizationId), "projectId", scope.ProjectId);
            }
            var page = new DataPage(ShellModel.For(http, context, database.Name + " · " + DatabaseFrameModel.Title("sql")), database, errorMessage, formError, dialog,
                sql, sqlResult, queries, queryName, catalog, history, CatalogError: catalogError, SqlNotice: sqlNotice, RunUrl: runUrl);
            if (http.Request.IsDatastarRequest)
            {
                if (http.Request.Path.StartsWithSegments("/api/v1/dashboard/data/saved-queries"))
                {
                    http.Response.Headers["datastar-mode"] = "replace";
                    if (dialog == "save-query")
                    {
                        await DatastarEvents.PatchElementsAsync(http, SaveQueryDialog.Create(page));
                    }
                    else if (dialog.StartsWith("delete-query-", StringComparison.Ordinal) && queries.FirstOrDefault(query => dialog == "delete-query-" + query.Id) is { } query)
                    {
                        await DatastarEvents.PatchElementsAsync(http, QueryDeleteDialog.Create(new(page, query)));
                    }
                    else
                    {
                        await DatastarEvents.PatchElementsAsync(http, SavedQueriesView.Create(page));
                    }
                }
                else
                {
                    await DatastarEvents.PatchElementsAsync(http, SqlRunPatch.Create(page));
                }
                return TypedResults.Empty;
            }
            var result = DataView.Create(page);
            result.StatusCode = status;
            return result;
        }

        private static string RunNotice(SqlRunReceipt run) => run.Result is { } kept && kept.Rows.Count < run.ReturnedRows
            ? $"The statement returned {run.ReturnedRows:N0} rows; this page keeps the first {kept.Rows.Count:N0}. Add a LIMIT or narrower columns to see the rest."
            : "";

        internal static McpSavedQuery PublicSavedQuery(SavedQuery query)
        {
            return new(query.Id, query.Name, query.Sql, query.CreatedAt);
        }
    }
}
