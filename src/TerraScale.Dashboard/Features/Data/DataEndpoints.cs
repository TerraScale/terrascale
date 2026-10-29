using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data
{
    public static class DataEndpoints
    {
        // A 1 MiB document plus a 1 MiB cursor, with worst-case URL encoding and form framing.
        private const int DocumentFormBytes = 3 * 2 * 1024 * 1024 + 16 * 1024;
        private const int BrowseFormBytes = 3 * 1024 * 1024 + 16 * 1024;
        public static IEndpointRouteBuilder MapDataFeatures(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapDocumentChanges();
            endpoints.MapSchemaPreview();
            endpoints.MapDeclaredSchema();
            Features.Webhooks.WebhookEndpoints.MapChangeWebhooks(endpoints);
            foreach (var section in new[] { "documents", "sql", "indexes", "history" })
            {
                var tab = section;
                endpoints.MapGet("/databases/{databaseId}/" + section, async (HttpContext http, string databaseId, DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) =>
                {
                    var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                    return context is null ? AccountRequest.LoginRedirect(http) : await PageAsync(http, context, databaseId, tab, data, saved);
                });
            }
            endpoints.MapMethods("/databases/{databaseId}/documents/{collection}", ["GET", "POST"], async (HttpContext http, string databaseId, string collection, DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) =>
            {
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                {
                    bodyLimit.MaxRequestBodySize = DocumentFormBytes;
                }

                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is not null && HttpMethods.IsPost(http.Request.Method) && (!http.Request.HasFormContentType || !Csrf.Validate(http, await http.Request.ReadFormAsync(http.RequestAborted))))
                {
                    return Results.BadRequest("Invalid form token.");
                }

                return context is null ? AccountRequest.LoginRedirect(http) : await PageAsync(http, context, databaseId, "documents", data, saved, collection);
            });
            // The documented NDJSON export: one {"collection","id","document"} object per line. Failures before the first line
            // answer normally; a failure mid-stream aborts the connection, so a cut download is never mistaken for a complete one.
            endpoints.MapGet("/databases/{databaseId}/export", async (HttpContext http, string databaseId, DashboardSessions sessions, AccountService accounts, DataService data) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                string name;
                IAsyncEnumerator<TerraBaseExportLine>? lines = null;
                bool more;
                try
                {
                    (name, var all) = await data.ExportAsync(context, databaseId, http.RequestAborted);
                    lines = all.GetAsyncEnumerator(http.RequestAborted);
                    more = await lines.MoveNextAsync();
                }
                catch (Exception error) when (DatabaseResults.KnownFailure(error))
                {
                    if (lines is not null)
                    {
                        await lines.DisposeAsync();
                    }

                    return DatabaseResults.PageFailure(http, context, error);
                }
                await using (lines)
                {
                    http.Response.ContentType = "application/x-ndjson";
                    http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}.ndjson\"";
                    http.Response.Headers.CacheControl = "no-store";
                    var newline = "\n"u8.ToArray();
                    while (more)
                    {
                        var line = lines.Current;
                        await JsonSerializer.SerializeAsync(http.Response.Body, new { collection = line.Collection, id = line.Id, document = line.Document }, cancellationToken: http.RequestAborted);
                        await http.Response.Body.WriteAsync(newline, http.RequestAborted);
                        more = await lines.MoveNextAsync();
                    }
                }
                return Results.Empty;
            });
            endpoints.MapGet("/api/v1/dashboard/data/documents", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DataService data) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var query = http.Request.Query;
                    var limit = 100;
                    if (query.TryGetValue("limit", out var requestedLimit) &&
                        (requestedLimit.Count != 1 || !int.TryParse(requestedLimit[0], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
                    {
                        throw new AccountAccessException("Document limits use 1–100 rows.", 422);
                    }

                    if (query.ContainsKey("after") && query.ContainsKey("cursor"))
                    {
                        throw new AccountAccessException("Use either after or cursor for document continuation.", 422);
                    }

                    var continuation = query.ContainsKey("after") ? query["after"] : query["cursor"];
                    if (continuation.Count > 1 || continuation.Count == 1 &&
                        (string.IsNullOrWhiteSpace(continuation[0]) || continuation[0]!.Any(char.IsControl)))
                    {
                        throw new AccountAccessException("Enter a valid document continuation cursor.", 422);
                    }

                    var result = await data.ListPageAsync(context, query["databaseId"].ToString(), query["collection"].ToString(),
                        query["search"].ToString(), limit, continuation.ToString(), http.RequestAborted);
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Json(new
                    {
                        documents = result.Items.Select(document => new { id = document.Id, value = document.Document }),
                        nextCursor = result.NextCursor,
                        hasMore = !string.IsNullOrEmpty(result.NextCursor),
                        limit
                    });
                }
                catch (Exception error) when (DatabaseResults.KnownFailure(error)) { return DatabaseResults.Failure(error); }
            });
            endpoints.MapGet("/api/v1/dashboard/schema", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DataService data) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try { return Results.Json(await data.SchemaAsync(context, http.Request.Query["databaseId"].ToString(), http.Request.Query["collection"].ToString(), http.RequestAborted)); }
                catch (Exception error) when (DatabaseResults.KnownFailure(error)) { return DatabaseResults.Failure(error); }
            });
            endpoints.MapGet("/api/v1/dashboard/data/saved-queries", async (HttpContext http, DashboardSessions sessions, AccountService accounts, SavedQueryService saved) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var (limit, after) = CursorPaging.Read(http.Request);
                    return Results.Json(CursorPaging.Page((await saved.ListAsync(context, http.RequestAborted)).Select(PublicSavedQuery),
                        query => query.Id, CursorPaging.Scope(context, "saved-queries"), limit, after));
                }
                catch (Exception error) when (DatabaseResults.KnownFailure(error)) { return DatabaseResults.Failure(error); }
            });
            endpoints.MapGet("/api/v1/dashboard/indexes", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DataService data) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var collection = http.Request.Query["collection"].ToString().Trim();
                    if (collection.Length > 0)
                    {
                        DataService.ValidCollection(collection);
                    }

                    var indexes = await data.IndexesAsync(context, http.Request.Query["databaseId"].ToString(), http.RequestAborted);
                    return Results.Json(indexes.Where(index => collection.Length == 0 || index.Collection == collection).Select(index => new { field = string.Join(", ", index.Fields), state = index.State }));
                }
                catch (Exception error) when (DatabaseResults.KnownFailure(error)) { return DatabaseResults.Failure(error); }
            });
            foreach (var operation in new[] { "documents", "collections", "collections/delete", "sql", "sql/check", "sql/catalog", "saved-queries", "saved-queries/delete", "indexes", "indexes/delete" })
            {
                var action = operation;
                var route = operation.StartsWith("indexes", StringComparison.Ordinal) ? "/api/v1/dashboard/" + operation : "/api/v1/dashboard/data/" + operation;
                endpoints.MapPost(route, async (HttpContext http, DashboardSessions sessions, AccountService accounts, DataService data, SavedQueryService saved) =>
                {
                    if (action is "documents" or "collections" or "collections/delete" && http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                    {
                        bodyLimit.MaxRequestBodySize = action == "documents" ? DocumentFormBytes : BrowseFormBytes;
                    }

                    if ((action.StartsWith("sql", StringComparison.Ordinal) || action == "saved-queries") && http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sqlBodyLimit)
                    {
                        // URL encoding can expand each SQL/history byte to three bytes; validation limits decoded SQL.
                        sqlBodyLimit.MaxRequestBodySize = 3 * (16 * 1024 + 4 * 1024) + 4096;
                    }
                    var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                    if (context is null)
                    {
                        return Results.Unauthorized();
                    }

                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form))
                    {
                        return Results.BadRequest("Invalid form token.");
                    }

                    var id = form["databaseId"].ToString();
                    var collection = form["collection"].ToString();
                    try
                    {
                        if (!action.StartsWith("saved-queries", StringComparison.Ordinal) || id.Length > 0)
                        {
                            DatabaseService.Database(context, id);
                        }

                        if (action == "documents")
                        {
                            var result = await data.WriteAsync(context, id, collection, form["operation"].ToString(), form["id"].ToString(), form["document"].ToString(), form["version"].ToString(), http.RequestAborted);
                            if (DatabaseEndpoints.WantsHtml(http))
                            {
                                return form["cursor"].ToString().Length > 0 ? Results.Redirect(DocumentsPath(id, collection, form), preserveMethod: true) : HttpRedirects.SeeOther(DocumentsPath(id, collection, form));
                            }

                            return Results.Json(new { id = result.Id, operation = form["operation"].ToString() });
                        }
                        if (action == "collections")
                        {
                            var collectionName = form["name"].ToString().Trim();
                            await data.CreateCollectionAsync(context, id, collectionName, http.RequestAborted);
                            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DocumentsPath(id, collectionName, form, true)) : Results.Json(new { collection = collectionName, status = "created" }, statusCode: 201);
                        }
                        if (action == "collections/delete")
                        {
                            await data.DropCollectionAsync(context, id, collection, form["confirmName"].ToString(), http.RequestAborted);
                            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "documents")) : Results.NoContent();
                        }
                        if (action == "sql/catalog")
                        {
                            return await PageAsync(http, context, id, "sql", data, saved, sql: form["sql"].ToString());
                        }

                        if (action == "sql/check")
                        {
                            var diagnostics = SqlValidation.Diagnostics(form["sql"].ToString());
                            var invalid = diagnostics.Any(item => item.Severity == "error");
                            // An empty editor shows no analysis, so a submitted check of it answers with the reason.
                            return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, id, "sql", data, saved, collection,
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
                                    return await RunAsync(http, context, id, collection, form, data, saved);
                                }

                                var result = await data.SqlAsync(context, id, form["sql"].ToString(), true, http.RequestAborted);
                                return Results.Json(new { columns = result.Columns, rows = result.Rows, rowsAffected = result.RowsAffected, lastInsertRowId = result.LastInsertRowId });
                            }
                            var query = await saved.SaveAsync(context, id, form["name"].ToString(), form["sql"].ToString(), http.RequestAborted);
                            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "sql", collection) + (collection.Length == 0 ? "?" : "&") + "savedQueryId=" + Uri.EscapeDataString(query.Id)) : Results.Json(PublicSavedQuery(query), statusCode: 201);
                        }
                        if (action == "saved-queries/delete")
                        {
                            await saved.DeleteAsync(context, id, form["id"].ToString(), http.RequestAborted);
                        }
                        else if (action == "indexes")
                        {
                            await data.CreateIndexAsync(context, id, collection, form["field"].ToString(), http.RequestAborted);
                        }
                        else if (action == "indexes/delete")
                        {
                            await data.DropIndexAsync(context, id, form["name"].ToString(), http.RequestAborted);
                        }

                        return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, action.StartsWith("indexes", StringComparison.Ordinal) ? "indexes" : "sql", collection)) : Results.NoContent();
                    }
                    catch (Exception error) when (DatabaseResults.KnownFailure(error))
                    {
                        if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404)
                        {
                            return DatabaseResults.PageFailure(http, context, error);
                        }

                        var section = action.StartsWith("indexes", StringComparison.Ordinal) ? "indexes" : action.Contains("sql", StringComparison.Ordinal) || action.StartsWith("saved-queries", StringComparison.Ordinal) ? "sql" : "documents";
                        var dialog = action == "collections" ? "new-collection" : action == "collections/delete" ? "delete-collection" : action == "saved-queries" ? "save-query" : action == "documents" ? form["operation"] == "create" ? "add-document" : form["operation"] == "delete" ? "delete-document" : "edit-document" : "";
                        var submittedId = error is DocumentWriteOutcomeUnknownException unknown ? unknown.DocumentId : form["id"].ToString();
                        return await PageAsync(http, context, id, section, data, saved, collection, DatabaseEndpoints.Message(error), dialog, submittedId, form["document"].ToString(), form["version"].ToString(), form["operation"].ToString(), form["sql"].ToString(), queryName: action == "indexes" ? form["field"].ToString() : form["name"].ToString(), status: DatabaseEndpoints.Status(error));
                    }
                });
            }
            return endpoints;
        }

        // A browser run answers 303 to a GET of its stored receipt, never a page rendered from the POST: refresh and back
        // cannot resend the statement, including a write whose outcome is unknown. Failures keep the draft and their reason.
        private static async Task<IResult> RunAsync(HttpContext http, AccountContext context, string id, string collection, IFormCollection form, DataService data, SavedQueryService saved)
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
                return HttpRedirects.SeeOther(DatabaseService.Path(id, "sql", collection) + (collection.Length == 0 ? "?" : "&") + "result=" + Uri.EscapeDataString(receipt));
            }
            catch (Exception error) when (SqlWorkspace.Unavailable(error) && !http.RequestAborted.IsCancellationRequested)
            {
                // Without a stored receipt the outcome renders once from the POST; say so rather than invite a resend.
                return await PageAsync(http, context, id, "sql", data, saved, collection, message, sql: sql, sqlResult: result, status: status,
                    sqlNotice: "This outcome could not be saved for later viewing. Refreshing this page would send the statement again.");
            }
        }

        internal static async Task<IResult> PageAsync(HttpContext http, AccountContext context, string id, string section, DataService data, SavedQueryService saved,
            string collection = "", string formError = "", string dialog = "", string documentId = "", string body = "{}", string version = "", string operation = "replace", string sql = "", DatabaseSqlResult? sqlResult = null, string queryName = "", int status = 200, bool sqlChecked = false, string sqlNotice = "")
        {
            DatabaseRecord database;
            try { database = DatabaseService.Database(context, id); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var errorMessage = "";
            IReadOnlyList<TerraBaseCollection> collections = [];
            var browse = new DocumentBrowse([], 0, 1, 1, false);
            IReadOnlyList<DatabaseIndex> indexes = [];
            IReadOnlyList<SavedQuery> queries = [];
            SqlCatalog? catalog = null;
            var catalogError = "";
            IReadOnlyList<string> history = [];
            IReadOnlyDictionary<string, long> collectionCounts = new Dictionary<string, long>();
            IFormCollection? submittedForm = null;
            if (http.Request.HasFormContentType)
            {
                var submitted = await http.Request.ReadFormAsync(http.RequestAborted);
                submittedForm = submitted;
                history = SqlHistory.Read(submitted["history"].ToString());
                if (sql.Length > 0 && http.Request.Path == "/api/v1/dashboard/data/sql")
                {
                    history = SqlHistory.Run(history, sql);
                }
            }
            var schema = "";
            string BrowseValue(string key) => submittedForm is not null && submittedForm.ContainsKey(key) ? submittedForm[key].ToString() : http.Request.Query[key].ToString();
            var search = BrowseValue("q").Trim();
            var sort = BrowseValue("sort");
            var descending = BrowseValue("order") == "desc";
            var layout = BrowseValue("view") == "list" ? "list" : "grid";
            var page = Math.Clamp(int.TryParse(BrowseValue("page"), out var requestedPage) ? requestedPage : 1, 1, int.MaxValue - 1);
            var cursor = BrowseValue("cursor");
            browse = new([], 0, page, page, false, cursor);
            if (collection.Length == 0)
            {
                collection = http.Request.Query["collection"].ToString();
            }

            try
            {
                var countsRead = section == "documents" ? data.CollectionCountsAsync(context, id, http.RequestAborted) : null;
                // Listing can fail or select no collection. Observe even a later unused counts fault.
                if (countsRead is not null)
                {
                    _ = countsRead.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                if (section != "sql")
                {
                    collections = await data.CollectionsAsync(context, id, http.RequestAborted);
                    // Default to a collection that exists; a database without collections has none selected.
                    if (collection.Length == 0)
                    {
                        collection = collections.FirstOrDefault()?.Name ?? "";
                    }
                }
                if (collection.Length > 0)
                {
                    DataService.ValidCollection(collection);
                }

                if (section == "documents" && collections.Count > 0)
                {
                    // Counts are an optional producer measurement. Omit unavailable
                    // counts while retaining independently available document rows; both reads run together.
                    var browseRead = data.BrowseAsync(context, id, collection, search, sort, descending, page, http.RequestAborted, cursor);
                    try { collectionCounts = await countsRead!; }
                    catch (Exception error) when (error is HttpRequestException or JsonException || DatabaseResults.KnownFailure(error)) { }
                    browse = await browseRead;
                    if (dialog.Length == 0 && (http.Request.Query["edit"].ToString() is { Length: > 0 } edit))
                    {
                        var selected = await data.GetAsync(context, id, collection, edit, http.RequestAborted);
                        dialog = "edit-document"; documentId = selected.Id; body = JsonDraft.Format(selected.Document, 1024 * 1024); version = selected.Version;
                    }
                    if (dialog.Length == 0 && (http.Request.Query["delete"].ToString() is { Length: > 0 } delete))
                    {
                        var selected = await data.GetAsync(context, id, collection, delete, http.RequestAborted);
                        dialog = "delete-document"; documentId = selected.Id; version = selected.Version;
                    }
                }
                else if (section == "sql")
                {
                    queries = await saved.ListAsync(context, id, http.RequestAborted);
                    // SQL runs on the database's SQL tables; document collections are not visible to /sql/execute,
                    // so the editor neither prefills nor suggests collection names.
                    if (submittedForm is null && http.Request.Query["result"].ToString() is { Length: > 0 } receiptId)
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
                    if (sql.Length == 0 && submittedForm is null)
                    {
                        sql = queries.FirstOrDefault(query => query.Id == http.Request.Query["savedQueryId"])?.Sql ?? "";
                    }

                    try { catalog = await data.CatalogAsync(context, id, http.RequestAborted); }
                    catch (Exception error) when (!http.RequestAborted.IsCancellationRequested &&
                        (DatabaseResults.KnownFailure(error) || error is HttpRequestException or OperationCanceledException or JsonException))
                    { catalogError = "SQL table suggestions are unavailable. You can still run queries. Retry to reload the catalog."; }
                }
                else if (section == "indexes")
                {
                    indexes = await data.IndexesAsync(context, id, http.RequestAborted);
                }
                else if (section == "history")
                {
                    documentId = http.Request.Query["id"].ToString() is { Length: > 0 } historyId ? DataService.ValidDocumentId(historyId) : "";
                }
            }
            catch (Exception error) when (error is HttpRequestException || DatabaseResults.KnownFailure(error))
            {
                errorMessage = DatabaseResults.KnownFailure(error) ? DatabaseResults.Message(error) : "The live data plane is unavailable. Try again shortly.";
                if (DatabaseResults.KnownFailure(error) && DatabaseResults.Status(error) == 404)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }
            }
            var result = DataView.Create(new(ShellModel.For(http, context, database.Name + " · " + DatabaseFrameModel.Title(section)), database, section, collection, collections, browse, search, sort, descending, layout, errorMessage, formError, dialog, documentId, body, version, operation, sql, sqlResult, indexes, schema, queries, queryName, catalog, history, collectionCounts, sqlChecked, catalogError, sqlNotice));
            result.StatusCode = status;
            return result;
        }

        private static string DocumentsPath(string id, string collection, IFormCollection form, bool restart = false)
        {
            var values = new List<KeyValuePair<string, string?>>();
            foreach (var key in new[] { "view", "sort", "order", "q", "page" })
            {
                if (!(restart && key == "page") && form[key].ToString().Length > 0)
                {
                    values.Add(new(key, form[key].ToString()));
                }
            }

            return "/databases/" + Uri.EscapeDataString(id) + "/documents/" + Uri.EscapeDataString(collection) + QueryString.Create(values);
        }

        private static McpSavedQuery PublicSavedQuery(SavedQuery query)
        {
            return new(query.Id, query.Name, query.Sql, query.CreatedAt);
        }
    }
}
