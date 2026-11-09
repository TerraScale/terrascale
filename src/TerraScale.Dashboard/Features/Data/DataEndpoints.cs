using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Databases;

namespace TerraScale.Dashboard.Features.Data
{
    public static class DataEndpoints
    {
        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved, string action)
        {
            if (action != "saved-queries/delete" && http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sqlBodyLimit)
            {
                // URL encoding can expand each SQL/history byte to three bytes; validation limits decoded SQL.
                sqlBodyLimit.MaxRequestBodySize = 3 * (16 * 1024 + 4 * 1024) + 4096;
            }
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            var id = form["databaseId"].ToString();
            try
            {
                if (!action.StartsWith("saved-queries", StringComparison.Ordinal) || id.Length > 0)
                {
                    DatabaseService.Database(context, id);
                }

                if (action == "sql/catalog")
                {
                    return await PageAsync(http, context, id, data, saved, sql: form["sql"].ToString());
                }

                if (action == "sql/check")
                {
                    var diagnostics = SqlValidation.Diagnostics(form["sql"].ToString());
                    var invalid = diagnostics.Any(item => item.Severity == "error");
                    // An empty editor shows no analysis, so a submitted check of it answers with the reason.
                    return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, id, data, saved,
                        formError: form["sql"].ToString().Trim().Length == 0 ? SqlValidation.Error("") : "",
                        sql: form["sql"].ToString(), sqlChecked: !invalid, status: invalid ? 422 : 200) : Results.Json(diagnostics);
                }
                if (action is "sql" or "saved-queries")
                {
                    var problem = SqlValidation.Diagnostics(form["sql"].ToString()).FirstOrDefault(item => item.Severity == "error");
                    if (problem is not null)
                    {
                        throw new AccountAccessException(problem.Message, 422);
                    }

                    if (action == "sql")
                    {
                        if (DatabaseResults.WantsHtml(http))
                        {
                            return await RunAsync(http, context, id, form, data, saved);
                        }

                        var result = await data.SqlAsync(context, id, form["sql"].ToString(), true, http.RequestAborted);
                        return Results.Json(new { columns = result.Columns, rows = result.Rows, rowsAffected = result.RowsAffected, lastInsertRowId = result.LastInsertRowId });
                    }
                    var query = await saved.SaveAsync(context, id, form["name"].ToString(), form["sql"].ToString(), http.RequestAborted);
                    return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "sql") + "?savedQueryId=" + Uri.EscapeDataString(query.Id)) : Results.Json(PublicSavedQuery(query), statusCode: 201);
                }
                if (action == "saved-queries/delete")
                {
                    await saved.DeleteAsync(context, id, form["id"].ToString(), http.RequestAborted);
                }
                return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "sql")) : Results.NoContent();
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }

                return await PageAsync(http, context, id, data, saved, DatabaseEndpoints.Message(error), action == "saved-queries" ? "save-query" : "",
                    form["sql"].ToString(), queryName: form["name"].ToString(), status: DatabaseEndpoints.Status(error));
            }
        }

        // A browser run answers 303 to a GET of its stored receipt, never a page rendered from the POST: refresh and back
        // cannot resend the statement, including a write whose outcome is unknown. Failures keep the draft and their reason.
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
                return HttpRedirects.SeeOther(DatabaseService.Path(id, "sql") + "?result=" + Uri.EscapeDataString(receipt));
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !http.RequestAborted.IsCancellationRequested)
            {
                // Without a stored receipt the outcome renders once from the POST; say so rather than invite a resend.
                return await PageAsync(http, context, id, data, saved, message, sql: sql, sqlResult: result, status: status,
                    sqlNotice: "This outcome could not be saved for later viewing. Refreshing this page would send the statement again.");
            }
        }

        internal static async Task<IResult> PageAsync(HttpContext http, AccountContext context, string id, DataService data, SavedQueryService saved,
            string formError = "", string dialog = "", string sql = "", DatabaseSqlResult? sqlResult = null, string queryName = "", int status = 200, bool sqlChecked = false, string sqlNotice = "")
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
                            if (run.Result is { } kept && kept.Rows.Count < run.ReturnedRows)
                            {
                                sqlNotice = $"The statement returned {run.ReturnedRows:N0} rows; this page keeps the first {kept.Rows.Count:N0}. Add a LIMIT or narrower columns to see the rest.";
                            }
                        }
                        else
                        {
                            sqlNotice = "This query result expired or was replaced by a newer run. The statement was not run again.";
                        }
                    }
                    catch (Exception error) when (SqlWorkspace.Unavailable(error) && !http.RequestAborted.IsCancellationRequested)
                    { sqlNotice = "This query result is unavailable right now. The statement was not run again; reload to retry."; }
                }
                if (sql.Length == 0 && submitted is null)
                {
                    sql = queries.FirstOrDefault(query => query.Id == http.Request.Query["savedQueryId"])?.Sql ?? "";
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
            var result = DataView.Create(new(ShellModel.For(http, context, database.Name + " · " + DatabaseFrameModel.Title("sql")), database, errorMessage, formError, dialog,
                sql, sqlResult, queries, queryName, catalog, history, sqlChecked, catalogError, sqlNotice));
            result.StatusCode = status;
            return result;
        }

        internal static McpSavedQuery PublicSavedQuery(SavedQuery query)
        {
            return new(query.Id, query.Name, query.Sql, query.CreatedAt);
        }
    }
}
