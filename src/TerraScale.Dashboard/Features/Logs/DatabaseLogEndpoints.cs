using System.Text.Json;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Logs;

public sealed record DatabaseLogPage(ShellModel Shell, DatabaseRecord Database, string Status, DatabaseLogBatch? Logs, string Error, LogDrain? Drain = null, string DrainError = "",
    IReadOnlyList<TerraBaseQueryShape>? QueryShapes = null, string QueryShapesError = "")
{
    public bool CanManageDrain => Shell.Context.Role == "owner";
}

public static class DatabaseLogEndpoints
{
    public static IServiceCollection AddDatabaseLogs(this IServiceCollection services) => services.AddSingleton<DatabaseLogBackend>()
        .AddSingleton<ILogDrainSource, LogDrainSource>().AddSingleton<LogDrains>().AddHostedService<LogDrainWorker>();

    public static IEndpointRouteBuilder MapDatabaseLogs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/databases/{databaseId}/logs", async (HttpContext http, string databaseId, DashboardSessions sessions,
            AccountService accounts, DatabaseService databases, DatabaseLogBackend backend, LogDrains drains) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            return await PageAsync(http, context, databaseId, http.Request.Query["status"].ToString(), databases, backend, drains);
        });
        foreach (var delete in new[] { false, true })
        {
            endpoints.MapPost("/api/v1/dashboard/log-drain" + (delete ? "/delete" : ""), async (HttpContext http, DashboardSessions sessions,
                AccountService accounts, DatabaseService databases, DatabaseLogBackend backend, LogDrains drains) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null) return Results.Unauthorized();
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                var databaseId = form["databaseId"].ToString();
                try
                {
                    if (delete) await drains.DeleteAsync(context, http.RequestAborted);
                    else await drains.SaveAsync(context, form["url"], form["headerName"], form["headerValue"], http.RequestAborted);
                }
                catch (Exception error) when (error is AccountAccessException or ArgumentException)
                {
                    return await PageAsync(http, context, databaseId, "", databases, backend, drains, error.Message, error is AccountAccessException access ? access.StatusCode : 422);
                }
                return HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "logs") + "#log-drain");
            });
        }
        return endpoints;
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
        DatabaseRegion? home = null;
        try
        {
            if (http.Request.Query["status"].Count > 1 || status is not ("" or "2xx" or "3xx" or "4xx" or "5xx"))
                throw new AccountAccessException("Choose a valid status filter.", 400);
            drain = await drains.ReadAsync(database.ProjectId, http.RequestAborted);
            home = await databases.HomeAsync(context, database, http.RequestAborted);
            logs = await backend.ReadAsync(DatabaseService.Scope(context), home, databaseId, status, http.RequestAborted);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or AccountAccessException || DatabaseResults.KnownFailure(error) ||
            error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            code = error is AccountAccessException { StatusCode: 400 } ? 400 : 503;
            message = code == 400 ? "Choose a valid status filter." : "Request logs are unavailable. Retry to load this database’s recorded requests.";
        }
        IReadOnlyList<TerraBaseQueryShape>? shapes = null;
        var shapesError = "Slow queries are unavailable. Retry to load query shapes.";
        if (home is not null && code != 400)
        {
            try
            {
                shapes = await backend.ReadQueryShapesAsync(DatabaseService.Scope(context), home, databaseId, http.RequestAborted);
                shapesError = "";
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException || DatabaseResults.KnownFailure(error) ||
                error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { }
        }
        var page = DatabaseLogView.Create(new(ShellModel.For(http, context, database.Name + " · Logs"), database, status, logs, message, drain, drainError, shapes, shapesError));
        page.StatusCode = drainStatus != 200 ? drainStatus : code;
        return (IResult)page;
    }
}
