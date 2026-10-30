using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed record FunctionsPage(ShellModel Shell, DatabaseRecord Database, IReadOnlyList<FunctionRecord> Functions, FunctionRecord? Selected,
        string? InvokeUrl, IReadOnlyList<FunctionInvocation> Logs, string Error);

    public static class FunctionEndpoints
    {
        public static IServiceCollection AddDatabaseFunctions(this IServiceCollection services)
        {
            return services.AddSingleton<FunctionBackend>();
        }

        public static IEndpointRouteBuilder MapDatabaseFunctions(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/databases/{databaseId}/functions", async (HttpContext http, string databaseId, DashboardSessions sessions, AccountService accounts, FunctionBackend functions) =>
            {
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
                IReadOnlyList<FunctionRecord> items = [];
                try
                {
                    items = await functions.ListAsync(context, database, http.RequestAborted);
                    if (name.Length == 0)
                    {
                        return View(http, context, database, items, null, []);
                    }

                    var selected = await functions.ReadAsync(context, database, name, http.RequestAborted)
                        ?? throw new AccountAccessException("This function does not exist.", 404);
                    var logs = await functions.LogsAsync(context, database, name, http.RequestAborted);
                    return View(http, context, database, items, selected, logs);
                }
                catch (Exception error) when (KnownFailure(error, http))
                {
                    var missing = error is AccountAccessException { StatusCode: 404 };
                    return View(http, context, database, items, null, [], missing ? error.Message : Message(error), missing ? 404 : Status(error));
                }
            });
            endpoints.MapPost("/api/v1/dashboard/functions/delete", async (HttpContext http, DashboardSessions sessions, AccountService accounts, FunctionBackend functions) =>
            {
                http.Response.Headers.CacheControl = "no-store";
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
                DatabaseRecord database;
                try { database = DatabaseService.Database(context, id); }
                catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
                try
                {
                    await functions.DeleteAsync(context, database, form["name"].ToString(), http.RequestAborted);
                    return HttpRedirects.SeeOther(DatabaseService.Path(id, "functions"));
                }
                catch (Exception error) when (KnownFailure(error, http))
                {
                    if (error is AccountAccessException { StatusCode: 403 })
                    {
                        return Results.Problem(error.Message, statusCode: 403);
                    }

                    IReadOnlyList<FunctionRecord> items = [];
                    try { items = await functions.ListAsync(context, database, http.RequestAborted); }
                    catch (Exception again) when (KnownFailure(again, http)) { }
                    return View(http, context, database, items, null, [], "The function was not deleted. " + Message(error), Status(error));
                }
            });
            return endpoints;
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
                401 or 403 => "Functions need a role that can read and write documents in this project.",
                _ when error is AccountAccessException => error.Message,
                _ => "Functions are unavailable. Retry to load this database’s functions."
            };
        }

        private static RazorSlices.RazorSlice<FunctionsPage> View(HttpContext http, AccountContext context, DatabaseRecord database, IReadOnlyList<FunctionRecord> items, FunctionRecord? selected,
            IReadOnlyList<FunctionInvocation> logs, string error = "", int status = 200)
        {
            var page = FunctionsView.Create(new(ShellModel.For(http, context, database.Name + " · Functions"), database, items, selected,
                selected is null ? null : FunctionBackend.InvokeUrl(context, database, selected.Name), logs, error));
            page.StatusCode = status;
            return page;
        }
    }
}
