using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Logs;

public sealed record DatabaseLogPage(ShellModel Shell, DatabaseRecord Database, string Status, DatabaseLogBatch? Logs, string Error)
{
    public IReadOnlyList<DatabaseLogEntry> SlowQueries => Logs?.Items.Where(item => item.IsQuery && item.LatencyMs >= 100).ToArray() ?? [];
}

public static class DatabaseLogEndpoints
{
    public static IServiceCollection AddDatabaseLogs(this IServiceCollection services) => services.AddSingleton<DatabaseLogBackend>();

    public static IEndpointRouteBuilder MapDatabaseLogs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/databases/{databaseId}/logs", async (HttpContext http, string databaseId, DashboardSessions sessions,
            AccountService accounts, DatabaseService databases, DatabaseLogBackend backend) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            DatabaseRecord database;
            try { database = DatabaseService.Database(context, databaseId); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var status = http.Request.Query["status"].ToString();
            DatabaseLogBatch? logs = null;
            var message = "";
            var code = 200;
            try
            {
                if (http.Request.Query["status"].Count > 1 || status is not ("" or "2xx" or "3xx" or "4xx" or "5xx"))
                    throw new AccountAccessException("Choose a valid status filter.", 400);
                var home = await databases.HomeAsync(context, database, http.RequestAborted);
                logs = await backend.ReadAsync(DatabaseService.Scope(context), home, databaseId, status, http.RequestAborted);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException or AccountAccessException || DatabaseResults.KnownFailure(error) ||
                error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            {
                code = error is AccountAccessException { StatusCode: 400 } ? 400 : 503;
                message = code == 400 ? "Choose a valid status filter." : "Request logs are unavailable. Retry to load this database’s recorded requests.";
            }
            var page = DatabaseLogView.Create(new(ShellModel.For(http, context, database.Name + " · Logs"), database, status, logs, message));
            page.StatusCode = code;
            return (IResult)page;
        });
        return endpoints;
    }
}
