using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Functions;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed record FunctionForm(string Name, string Cron, string Source, bool Editing, string Error);

    public sealed record FunctionTriggerChips(string Cron);

    public sealed record FunctionSide(FunctionsPage Page, string ChipCron);

    public sealed record FunctionsPage(ShellModel Shell, DatabaseRecord Database, IReadOnlyList<FunctionRecord> Functions, FunctionRecord? Selected,
        string? InvokeUrl, IReadOnlyList<FunctionInvocation> Logs, string Error, bool NoAccess, string DeleteName,
        string Notice = "", FunctionForm? Form = null, string RunEvent = "", string RunResult = "", string RunError = "",
        string LogsTrigger = "", string LogsStatus = "", string DeployError = "", TerraBaseFunctionStats? Stats = null,
        IReadOnlyList<TerraBaseFunctionSecret>? Secrets = null, string SecretName = "", string SecretError = "")
    {
        public string Window => Stats?.Window ?? "24h";
    }

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
            var trigger = http.Request.Query["trigger"].ToString() is "http" or "cron" ? http.Request.Query["trigger"].ToString() : "";
            var status = http.Request.Query["status"].ToString() is "ok" or "error" ? http.Request.Query["status"].ToString() : "";
            var notice = http.Request.Query["deployed"].ToString() == "1" ? "Function deployed." : http.Request.Query["secret"].ToString() switch
            {
                "saved" => "Secret saved.",
                "deleted" => "Secret deleted.",
                _ => "",
            };
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
                var stats = await ReadStatsAsync(http, functions, context, database, name);
                var logs = Filter(await functions.LogsAsync(context, database, name, http.RequestAborted), trigger, status, stats);
                var secrets = await ReadSecretsAsync(http, functions, context, database, name);
                return View(http, context, database, items, selected, logs, notice: notice, logsTrigger: trigger, logsStatus: status, stats: stats, secrets: secrets);
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
            var intent = form["intent"].ToString();
            var trigger = form["trigger"].ToString() is "http" or "cron" ? form["trigger"].ToString() : "";
            var status = form["status"].ToString() is "ok" or "error" ? form["status"].ToString() : "";
            var inPlace = intent is "trigger" or "code" && http.Request.IsDatastarRequest;
            FunctionRecord? before = null;
            try
            {
                if (inPlace)
                {
                    before = await functions.ReadAsync(context, database, name, http.RequestAborted)
                        ?? throw new AccountAccessException("This function does not exist.", 404);
                    if (intent == "trigger")
                    {
                        source = before.Source ?? "";
                    }
                }

                var saved = await functions.DeployAsync(context, database, name, source, cron.Length == 0 ? null : cron, http.RequestAborted);
                if (inPlace)
                {
                    var logs = Filter(await functions.LogsAsync(context, database, name, http.RequestAborted), trigger, status);
                    await PatchDeployAsync(http, functions, context, database, saved, logs, "Function deployed.", "", trigger, status);
                    return Results.Empty;
                }

                return HttpRedirects.SeeOther(DatabaseService.Path(id, "functions") + "?function=" + Uri.EscapeDataString(saved.Name) + "&deployed=1");
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                if (inPlace)
                {
                    var logs = before is null ? [] : Filter(await functions.LogsAsync(context, database, name, http.RequestAborted), trigger, status);
                    await PatchDeployAsync(http, functions, context, database, before, logs, "", FailureText(error), trigger, status);
                    return Results.Empty;
                }

                var editing = form["editing"].ToString() == "1";
                return View(http, context, database, [], null, [], status: Status(error), form: new FunctionForm(name, cron, source, editing, FailureText(error)));
            }
        }

        internal static async Task<IResult> SecretAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, FunctionBackend functions, bool delete)
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
            var secret = form["secret"].ToString().Trim();
            FunctionRecord? selected = null;
            try
            {
                selected = await functions.ReadAsync(context, database, name, http.RequestAborted)
                    ?? throw new AccountAccessException("This function does not exist.", 404);
                if (delete)
                {
                    if (!await functions.DeleteSecretAsync(context, database, name, secret, http.RequestAborted))
                    {
                        throw new AccountAccessException("This secret no longer exists.", 404);
                    }
                }
                else
                {
                    await functions.SetSecretAsync(context, database, name, secret, form["value"].ToString(), http.RequestAborted);
                }

                return HttpRedirects.SeeOther(DatabaseService.Path(id, "functions") + "?function=" + Uri.EscapeDataString(name) + "&secret=" + (delete ? "deleted" : "saved"));
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                IReadOnlyList<FunctionInvocation> logs = selected is null ? [] : await OptionalPanel.LoadAsync<IReadOnlyList<FunctionInvocation>>(http, "function-logs",
                    async () => await functions.LogsAsync(context, database, name, http.RequestAborted), () => []);
                var secrets = selected is null ? null : await ReadSecretsAsync(http, functions, context, database, name);
                var stats = selected is null ? null : await ReadStatsAsync(http, functions, context, database, name);
                return View(http, context, database, [], selected, Filter(logs, "", "", stats), status: Status(error), noAccess: Status(error) == 403, stats: stats, secrets: secrets,
                    secretName: delete ? "" : secret, secretError: FailureText(error));
            }
        }

        private static async Task PatchDeployAsync(HttpContext http, FunctionBackend functions, AccountContext context, DatabaseRecord database, FunctionRecord? saved,
            IReadOnlyList<FunctionInvocation> logs, string notice, string error, string trigger, string status)
        {
            var stats = saved is null ? null : await ReadStatsAsync(http, functions, context, database, saved.Name);
            var page = new FunctionsPage(ShellModel.For(http, context, database.Name + " · Functions"), database, [], saved,
                saved is null ? null : FunctionBackend.InvokeUrl(context, database, saved.Name), logs, "", false, "", notice, null, "", "", "", LogsTrigger: trigger, LogsStatus: status, DeployError: error, Stats: stats);
            await DatastarEvents.PatchElementsAsync(http, FunctionsDeployView.Create(page));
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
            var trigger = form["trigger"].ToString() is "http" or "cron" ? form["trigger"].ToString() : "";
            var status = form["status"].ToString() is "ok" or "error" ? form["status"].ToString() : "";
            FunctionRecord? selected = null;
            IReadOnlyList<FunctionInvocation> logs = [];
            try
            {
                selected = await functions.ReadAsync(context, database, name, http.RequestAborted)
                    ?? throw new AccountAccessException("This function does not exist.", 404);
                logs = Filter(await functions.LogsAsync(context, database, name, http.RequestAborted), trigger, status);
                if (!JsonEvent(raw))
                {
                    return await RunView(http, functions, context, database, selected, logs, raw, "", "Event must be JSON.", trigger, status);
                }

                var body = raw.Trim().Length == 0 ? "{}" : raw;
                string result = "";
                TerraBaseException? fault = null;
                try
                {
                    var call = await functions.InvokeAsync(context, database, name, body, http.RequestAborted);
                    result = string.Concat(call.Status.ToString(CultureInfo.InvariantCulture), "\n", call.ContentType, "\n", call.Body);
                }
                catch (TerraBaseException error)
                {
                    fault = error;
                }

                logs = Filter(await functions.LogsAsync(context, database, name, http.RequestAborted), trigger, status);
                return fault is null
                    ? await RunView(http, functions, context, database, selected, logs, body, result, "", trigger, status)
                    : await RunView(http, functions, context, database, selected, logs, body, "", fault.Message, trigger, status);
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                return await RunView(http, functions, context, database, selected, logs, raw, "", FailureText(error), trigger, status);
            }
        }

        private static IReadOnlyList<FunctionInvocation> Filter(IReadOnlyList<FunctionInvocation> logs, string trigger, string status, TerraBaseFunctionStats? stats = null)
        {
            return [.. logs.Where(log => (stats is null || log.At >= stats.Buckets[0].At) && (trigger.Length == 0 || log.Trigger == trigger) && (status.Length == 0 || log.Status == status))];
        }

        private static Task<IReadOnlyList<TerraBaseFunctionSecret>?> ReadSecretsAsync(HttpContext http, FunctionBackend functions, AccountContext context, DatabaseRecord database, string name)
        {
            return OptionalPanel.LoadAsync<IReadOnlyList<TerraBaseFunctionSecret>?>(http, "function-secrets",
                async () => await functions.SecretsAsync(context, database, name, http.RequestAborted), () => null);
        }

        private static Task<TerraBaseFunctionStats?> ReadStatsAsync(HttpContext http, FunctionBackend functions, AccountContext context, DatabaseRecord database, string name)
        {
            return OptionalPanel.LoadAsync<TerraBaseFunctionStats?>(http, "function-metrics",
                async () => await functions.StatsAsync(context, database, name, Window(http), http.RequestAborted), () => null);
        }

        private static string Window(HttpContext http)
        {
            var window = HttpMethods.IsPost(http.Request.Method) ? http.Request.Form["window"].ToString() : http.Request.Query["window"].ToString();

            return window is "1h" or "7d" ? window : "24h";
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
            string runEvent = "", string runResult = "", string runError = "", string logsTrigger = "", string logsStatus = "", TerraBaseFunctionStats? stats = null,
            IReadOnlyList<TerraBaseFunctionSecret>? secrets = null, string secretName = "", string secretError = "")
        {
            var page = FunctionsView.Create(new(ShellModel.For(http, context, database.Name + " · Functions"), database, items, selected,
                selected is null ? null : FunctionBackend.InvokeUrl(context, database, selected.Name), logs, error, noAccess, deleteName, notice, form, runEvent, runResult, runError, logsTrigger, logsStatus, Stats: stats,
                Secrets: secrets, SecretName: secretName, SecretError: secretError));
            page.StatusCode = status;
            return page;
        }

        private static async Task<IResult> RunView(HttpContext http, FunctionBackend functions, AccountContext context, DatabaseRecord database, FunctionRecord? selected, IReadOnlyList<FunctionInvocation> logs,
            string runEvent, string runResult, string runError, string trigger = "", string status = "")
        {
            var stats = selected is null ? null : await ReadStatsAsync(http, functions, context, database, selected.Name);
            logs = Filter(logs, trigger, status, stats);
            if (!http.Request.IsDatastarRequest)
            {
                return View(http, context, database, [], selected, logs, runEvent: runEvent, runResult: runResult, runError: runError, logsTrigger: trigger, logsStatus: status, stats: stats);
            }

            var model = new FunctionsPage(ShellModel.For(http, context, database.Name + " · Functions"), database, [], selected,
                selected is null ? null : FunctionBackend.InvokeUrl(context, database, selected.Name), logs, "", false, "", "", null, runEvent, runResult, runError,
                trigger, status, Stats: stats);
            await DatastarEvents.PatchElementsAsync(http, FunctionsRunView.Create(model));
            return Results.Empty;
        }
    }
}
