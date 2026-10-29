using RazorSlices;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Databases;

public static class DatabaseSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseSettings(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/databases/{databaseId}/settings", async (HttpContext http, string databaseId, DashboardSessions sessions, AccountService accounts, DatabaseService databases) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            await databases.SyncAsync(context, http.RequestAborted);
            return Page(http, context, databaseId);
        });
        endpoints.MapPost("/api/v1/dashboard/databases/rename", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            var id = form["databaseId"].ToString();
            try
            {
                var database = await databases.RenameAsync(context, id, form["name"].ToString(), form["confirmName"].ToString(), http.RequestAborted);
                return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "settings") + "?renamed=1") : Results.Json(database);
            }
            catch (Exception error) when (!http.RequestAborted.IsCancellationRequested && (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException))
            {
                var message = DatabaseResults.Status(error) >= 500 ? "The rename result could not be confirmed. Reload Settings to check the current name before retrying." : DatabaseResults.Message(error);
                return DatabaseResults.WantsHtml(http) && DatabaseResults.Status(error) != 404
                    ? Page(http, context, id, message, form["name"].ToString(), form["confirmName"].ToString(), DatabaseResults.Status(error))
                    : DatabaseResults.PageFailure(http, context, error);
            }
        });
        return endpoints;
    }

    private static IResult Page(HttpContext http, AccountContext context, string id, string error = "", string name = "", string confirmation = "", int status = 200)
    {
        try
        {
            var database = DatabaseService.Database(context, id);
            var result = DatabaseSettingsView.Create(new(ShellModel.For(http, context, database.Name + " · Settings"), database, error, name, confirmation, http.Request.Query["renamed"] == "1"));
            result.StatusCode = status;
            return result;
        }
        catch (AccountAccessException failure) { return DatabaseResults.PageFailure(http, context, failure); }
    }
}

public sealed record DatabaseSettingsPage(ShellModel Shell, DatabaseRecord Database, string Error, string Name, string Confirmation, bool Renamed);
