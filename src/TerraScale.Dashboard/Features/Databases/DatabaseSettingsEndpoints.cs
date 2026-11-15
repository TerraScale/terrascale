using System.Globalization;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.Features.Databases
{
    public static class DatabaseSettingsEndpoints
    {
        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, string databaseId)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            await databases.SyncAsync(context, http.RequestAborted);
            return await PageAsync(http, context, databaseId);
        }

        internal static async Task<IResult> RenameAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "settings"), Results.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            try
            {
                var database = await databases.RenameAsync(context, id, form["name"].ToString(), form["confirmName"].ToString(), http.RequestAborted);
                if (http.Request.Headers["Datastar-Request"] == "true")
                {
                    return await PageAsync(http, await databases.CurrentAsync(context, http.RequestAborted), id, renamed: true);
                }
                return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "settings") + "?renamed=1") : Results.Json(database);
            }
            catch (Exception error) when (!http.RequestAborted.IsCancellationRequested && (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException))
            {
                var message = DatabaseResults.Status(error) >= 500 ? "The rename result could not be confirmed. Reload Settings to check the current name before retrying." : DatabaseResults.Message(error);
                return (DatabaseResults.WantsHtml(http) || http.Request.Headers["Datastar-Request"] == "true") && DatabaseResults.Status(error) != 404
                    ? await PageAsync(http, context, id, message, form["name"].ToString(), form["confirmName"].ToString(), DatabaseResults.Status(error))
                    : DatabaseResults.PageFailure(http, context, error);
            }
        }

        internal static async Task<IResult> PageAsync(HttpContext http, AccountContext context, string id, string error = "", string name = "", string confirmation = "", int status = 200, bool renamed = false, string? ttlHours = null, string expiryError = "", bool expirySaved = false)
        {
            try
            {
                var database = DatabaseService.Database(context, id);
                var hours = ttlHours ?? (database.ExpiresAt is { } expiry
                    ? ((decimal)(expiry - http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()).TotalHours).ToString("0.######", CultureInfo.InvariantCulture)
                    : "");
                var model = new DatabaseSettingsPage(ShellModel.For(http, context, database.Name + " · Settings"), database, error,
                    name.Length == 0 && error.Length == 0 ? database.Name : name, confirmation, renamed || http.Request.Query["renamed"] == "1", hours, expiryError, expirySaved);
                if (http.Request.Headers["Datastar-Request"] == "true")
                {
                    await DatastarEvents.PatchElementsAsync(http, DatabaseSettingsSection.Create(model));
                    return Results.Empty;
                }
                var result = DatabaseSettingsView.Create(model);
                result.StatusCode = status;
                return result;
            }
            catch (AccountAccessException failure) { return DatabaseResults.PageFailure(http, context, failure); }
        }
    }

    public sealed record DatabaseSettingsPage(ShellModel Shell, DatabaseRecord Database, string Error, string Name, string Confirmation, bool Renamed,
        string TtlHours, string ExpiryError, bool ExpirySaved)
    {
        public static readonly IReadOnlyList<(string Hours, string Label)> ExpiryOptions = [("", "Never"), ("24", "1 day"), ("168", "7 days"), ("720", "30 days"), ("2160", "90 days"), ("custom", "Custom")];
        public string ExpiryPreset => ExpiryOptions.Any(option => option.Hours == TtlHours) ? TtlHours : "custom";
        public bool CanManage => Shell.Context.CanManage && !PlatformDatabases.IsPlatform(Shell.Context.Organization?.Id);
        public bool CanRename => CanManage && Database.Status == "ready";
    }
}
