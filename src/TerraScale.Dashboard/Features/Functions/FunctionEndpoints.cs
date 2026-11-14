using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed record FunctionForm(string Name, string Cron, string Source, bool Editing, string Error);

    public sealed record FunctionsPage(ShellModel Shell, DatabaseRecord Database, IReadOnlyList<FunctionRecord> Functions, FunctionRecord? Selected,
        string? InvokeUrl, IReadOnlyList<FunctionInvocation> Logs, string Error, bool NoAccess, string DeleteName,
        string Notice = "", FunctionForm? Form = null, string RunEvent = "", string RunResult = "", string RunError = "");

    public static class FunctionEndpoints
    {
        public static IServiceCollection AddDatabaseFunctions(this IServiceCollection services)
        {
            return services.AddSingleton<FunctionBackend>();
        }

        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, FunctionBackend functions)
        {
            var databaseId = http.Request.RouteValues["databaseId"]?.ToString() ?? "";
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            DatabaseRecord database;
            try { database = DatabaseService.Database(context, databaseId); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var name = http.Request.Query["function"].ToString();
            var edit = http.Request.Query["edit"].ToString();
            var notice = http.Request.Query["deployed"].ToString() == "1" ? "Function deployed." : "";
            IReadOnlyList<FunctionRecord> items = [];
            try
            {
                items = await functions.ListAsync(context, database, http.RequestAborted);
                if (edit == "new")
                {
                    return View(http, context, database, items, null, [], notice: notice, form: new FunctionForm("", "", FunctionBackend.StarterSource, false, ""));
                }

                if (name.Length == 0)
                {
                    return View(http, context, database, items, null, [], notice: notice);
                }

                var selected = await functions.ReadAsync(context, database, name, http.RequestAborted)
                    ?? throw new AccountAccessException("This function does not exist.", 404);
                var logs = await functions.LogsAsync(context, database, name, http.RequestAborted);
                var form = edit == "1" ? new FunctionForm(selected.Name, selected.Cron ?? "", selected.Source ?? "", true, "") : null;
                return View(http, context, database, items, selected, logs, notice: notice, form: form);
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                var missing = error is AccountAccessException { StatusCode: 404 };
                return View(http, context, database, items, null, [], missing ? error.Message : Message(error), missing ? 404 : Status(error) == 403 ? 200 : Status(error), noAccess: Status(error) == 403);
            }
        }

        internal static async Task<IResult> DeleteAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, FunctionBackend functions)
        {
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "functions"), Results.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            DatabaseRecord database;
            try { database = DatabaseService.Database(context, id); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var name = form["name"].ToString();
            IReadOnlyList<FunctionRecord> items = [];
            FunctionRecord? selected = null;
            IReadOnlyList<FunctionInvocation> logs = [];
            try
            {
                items = await functions.ListAsync(context, database, http.RequestAborted);
                selected = await functions.ReadAsync(context, database, name, http.RequestAborted);
                logs = selected is null ? [] : await functions.LogsAsync(context, database, name, http.RequestAborted);
                if (!await functions.DeleteAsync(context, database, name, http.RequestAborted))
                {
                    throw new AccountAccessException("This function no longer exists.", 404);
                }

                return HttpRedirects.SeeOther(DatabaseService.Path(id, "functions"));
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                return View(http, context, database, items, selected, logs, "The function was not deleted. " + Message(error), Status(error),
                    noAccess: Status(error) == 403, deleteName: name);
            }
        }

        internal static async Task<IResult> DeployAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, FunctionBackend functions)
        {
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "functions"), Results.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            DatabaseRecord database;
            try { database = DatabaseService.Database(context, id); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var name = form["name"].ToString().Trim();
            var cron = form["cron"].ToString().Trim();
            var source = form["source"].ToString();
            var editing = form["editing"].ToString() == "1";
            IReadOnlyList<FunctionRecord> items = [];
            try
            {
                items = await functions.ListAsync(context, database, http.RequestAborted);
                var saved = await functions.DeployAsync(context, database, name, source, cron.Length == 0 ? null : cron, http.RequestAborted);
                return HttpRedirects.SeeOther(DatabaseService.Path(id, "functions") + "?function=" + Uri.EscapeDataString(saved.Name) + "&deployed=1");
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                return View(http, context, database, items, null, [], status: Status(error), form: new FunctionForm(name, cron, source, editing, FailureText(error)));
            }
        }

        internal static async Task<IResult> InvokeAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, FunctionBackend functions)
        {
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "functions"), Results.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            DatabaseRecord database;
            try { database = DatabaseService.Database(context, id); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var name = form["name"].ToString();
            var raw = form["event"].ToString();
            IReadOnlyList<FunctionRecord> items = [];
            FunctionRecord? selected = null;
            IReadOnlyList<FunctionInvocation> logs = [];
            try
            {
                items = await functions.ListAsync(context, database, http.RequestAborted);
                selected = await functions.ReadAsync(context, database, name, http.RequestAborted)
                    ?? throw new AccountAccessException("This function does not exist.", 404);
                logs = await functions.LogsAsync(context, database, name, http.RequestAborted);
                if (!JsonEvent(raw))
                {
                    return View(http, context, database, items, selected, logs, status: 400, runEvent: raw, runError: "Event must be JSON.");
                }

                var body = raw.Trim().Length == 0 ? "{}" : raw;
                string result = "";
                TerraBaseException? fault = null;
                try
                {
                    result = await functions.InvokeAsync(context, database, name, body, http.RequestAborted);
                }
                catch (TerraBaseException error)
                {
                    fault = error;
                }

                logs = await functions.LogsAsync(context, database, name, http.RequestAborted);
                return fault is null
                    ? View(http, context, database, items, selected, logs, runEvent: body, runResult: result)
                    : View(http, context, database, items, selected, logs, status: fault.Status, runEvent: body, runError: fault.Message);
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                return View(http, context, database, items, selected, logs, selected is null ? FailureText(error) : "", Status(error), runEvent: raw, runError: selected is null ? "" : FailureText(error));
            }
        }

        private static bool JsonEvent(string raw)
        {
            if (raw.Trim().Length == 0)
            {
                return true;
            }

            try
            {
                using (JsonDocument.Parse(raw))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string FailureText(Exception error)
        {
            return error is TerraBaseValidationException or TerraBaseConflictException or AccountAccessException ? error.Message : Message(error);
        }

        private static bool KnownFailure(Exception error, HttpContext http)
        {
            return error is HttpRequestException or JsonException or IOException or NotSupportedException || DatabaseResults.KnownFailure(error) ||
            error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
        }

        private static int Status(Exception error)
        {
            return error is TerraBaseException { Status: var rpcStatus } ? rpcStatus :
            error is AccountAccessException { StatusCode: var code } ? code :
            error is HttpRequestException { StatusCode: { } status } && (int)status is 401 or 403 ? (int)status : 503;
        }

        private static string Message(Exception error)
        {
            return Status(error) switch
            {
                401 or 403 => "Functions need a role that can read and write data in this project, which an owner or admin can grant from the organization Members tab.",
                _ when error is AccountAccessException => error.Message,
                _ => "Functions are unavailable. Retry to load this database’s functions."
            };
        }

        private static RazorSlices.RazorSlice<FunctionsPage> View(HttpContext http, AccountContext context, DatabaseRecord database, IReadOnlyList<FunctionRecord> items, FunctionRecord? selected,
            IReadOnlyList<FunctionInvocation> logs, string error = "", int status = 200, bool noAccess = false, string deleteName = "", string notice = "", FunctionForm? form = null,
            string runEvent = "", string runResult = "", string runError = "")
        {
            var page = FunctionsView.Create(new(ShellModel.For(http, context, database.Name + " · Functions"), database, items, selected,
                selected is null ? null : FunctionBackend.InvokeUrl(context, database, selected.Name), logs, error, noAccess, deleteName, notice, form, runEvent, runResult, runError));
            page.StatusCode = status;
            return page;
        }
    }
}
