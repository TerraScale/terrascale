using System.Diagnostics;
using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.TerraBase.Contracts.Logs;

namespace TerraScale.Dashboard.Features.Logs
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;

    public sealed record DatabaseLogPage(ShellModel Shell, DatabaseRecord Database, string Status, DatabaseLogBatch? Logs, string Error, LogDrain? Drain = null, string DrainError = "")
    {
        public bool Live => Status.Length == 0;
        public bool CanManageDrain => Shell.Context.Role == "owner";
    }

    public static class DatabaseLogEndpoints
    {
        public static IServiceCollection AddDatabaseLogs(this IServiceCollection services)
        {
            return services.AddSingleton<DatabaseLogBackend>()
            .AddSingleton<ILogDrainSource, LogDrainSource>().AddSingleton<LogDrains>().AddHostedService<LogDrainWorker>();
        }

        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases,
            DatabaseLogBackend backend, LogDrains drains)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            return await PageAsync(http, context, http.Request.RouteValues["databaseId"]?.ToString() ?? "", http.Request.Query["status"].ToString(), databases, backend, drains);
        }

        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases,
            DatabaseLogBackend backend, LogDrains drains, bool delete)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            IFormCollection form;
            switch (await FormRequests.ReadAsync(http, http.RequestAborted))
            {
                case Validation invalid:
                    return FormRequests.Refused(http, invalid);
                case UnsupportedMediaType unsupported:
                    return FormRequests.Refused(http, unsupported);
                case IFormCollection parsed:
                    form = parsed;
                    break;
                case null:
                    throw new UnreachableException();
            }
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "logs"), Results.BadRequest("Invalid form token."));
            }

            var databaseId = form["databaseId"].ToString();
            if (delete)
            {
                var deleted = await drains.DeleteAsync(context, http.RequestAborted);
                if (deleted is not Done)
                {
                    return deleted switch
                    {
                        NotFound error => await PageAsync(http, context, databaseId, "", databases, backend, drains, error.Message!, 404),
                        Forbidden error => await PageAsync(http, context, databaseId, "", databases, backend, drains, error.Message, 403),
                        Done => throw new UnreachableException(),
                    };
                }
            }
            else
            {
                var saved = await drains.SaveAsync(context, form["url"], form["headerName"], form["headerValue"], http.RequestAborted);
                if (saved is not Done)
                {
                    return saved switch
                    {
                        NotFound error => await PageAsync(http, context, databaseId, "", databases, backend, drains, error.Message!, 404),
                        Forbidden error => await PageAsync(http, context, databaseId, "", databases, backend, drains, error.Message, 403),
                        Validation error => await PageAsync(http, context, databaseId, "", databases, backend, drains, error.Message, 422),
                        Done => throw new UnreachableException(),
                    };
                }
            }

            return HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "logs") + "#log-drain");
        }

        private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, string databaseId, string status, DatabaseService databases,
            DatabaseLogBackend backend, LogDrains drains, string drainError = "", int drainStatus = 200)
        {
            http.Response.Headers.CacheControl = "no-store";
            DatabaseRecord database;
            try { database = DatabaseService.Database(context, databaseId); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            DatabaseLogBatch? logs = null;
            var message = "";
            var code = 200;
            LogDrain? drain = null;
            try
            {
                if (http.Request.Query["status"].Count > 1 || status is not ("" or "2xx" or "3xx" or "4xx" or "5xx"))
                {
                    throw new AccountAccessException("Choose a valid status filter.", 400);
                }

                drain = await drains.ReadAsync(database.ProjectId, http.RequestAborted);
                var home = await databases.HomeAsync(context, database, http.RequestAborted);
                logs = await backend.ReadAsync(DatabaseService.Scope(context), home, databaseId, status, http.RequestAborted);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException or AccountAccessException || DatabaseResults.KnownFailure(error) ||
                error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            {
                code = error is AccountAccessException { StatusCode: 400 } ? 400 : 503;
                message = code == 400 ? "Choose a valid status filter." : "Request logs for " + database.Name + " are unavailable.";
            }
            var model = new DatabaseLogPage(ShellModel.For(http, context, database.Name + " · Logs"), database, status, logs, message, drain, drainError);
            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, DatabaseLogSection.Create(model));
                return Results.Empty;
            }
            var page = DatabaseLogView.Create(model);
            page.StatusCode = drainStatus != 200 ? drainStatus : code;
            return (IResult)page;
        }
    }
}
