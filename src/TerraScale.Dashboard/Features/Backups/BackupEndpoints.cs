using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Backups;

public static class BackupEndpoints
{
    // detailPage re-renders the database page's backups tab: (http, context, databaseId, error, status).
    public static IEndpointRouteBuilder MapBackupFeatures(this IEndpointRouteBuilder endpoints, Func<HttpContext, AccountContext, string, string, int, Task<IResult>> detailPage)
    {
        endpoints.MapGet("/api/v1/dashboard/backups", async (HttpContext http, DashboardSessions sessions, AccountService accounts, BackupService backups) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            try { return Results.Json(await backups.ListAsync(context, http.Request.Query["databaseId"].ToString(), http.RequestAborted)); }
            catch (Exception error) when (KnownFailure(error) &&
                (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            { return Failure(error); }
        });
        foreach (var operation in new[] { "create", "policy", "restore" })
        {
            var action = operation;
            endpoints.MapPost(action == "create" ? "/api/v1/dashboard/backups" : "/api/v1/dashboard/backups/" + action,
                async (HttpContext http, DashboardSessions sessions, AccountService accounts, BackupService backups) =>
                {
                    var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                    if (context is null) return Results.Unauthorized();
                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                    var databaseId = form["databaseId"].ToString();
                    try
                    {
                        if (action == "create")
                        {
                            var backup = await backups.CreateAsync(context, databaseId, http.RequestAborted);
                            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "backups")) : Results.Json(backup, statusCode: 201);
                        }
                        if (action == "policy")
                        {
                            var days = int.TryParse(form["retentionDays"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                            var policy = await backups.SetPolicyAsync(context, databaseId, form["fullBackupCron"].ToString(),
                                form["incrementalBackupCron"].ToString(), days, form["disabled"] == "1", http.RequestAborted);
                            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "backups")) : Results.Json(policy);
                        }
                        if (form["replace"] == "1") BackupService.RejectOverwrite();
                        var restored = await backups.RestoreAsync(context, databaseId, form["backupId"].ToString(), form["name"].ToString(), http.RequestAborted);
                        return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(restored.DatabaseId)) :
                            Results.Json(new { id = restored.DatabaseId, restored.Name }, statusCode: 201);
                    }
                    catch (Exception error) when (KnownFailure(error) &&
                        (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
                    {
                        if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404) return Failure(error);
                        return await detailPage(http, context, databaseId, Message(error), DatabaseResults.Status(error));
                    }
                });
        }
        return endpoints;
    }

    internal static async Task<BackupPage> PageAsync(HttpContext http, AccountContext context, DatabaseRecord database, string error)
    {
        BackupListing? listing = null;
        var unavailable = "";
        try { listing = await http.RequestServices.GetRequiredService<BackupService>().ListAsync(context, database.Id, http.RequestAborted); }
        catch (Exception failure) when (KnownFailure(failure) &&
            (failure is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
        { unavailable = Message(failure); }
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
        return new(ShellModel.For(http, context, database.Name + " · backups"), database, listing, unavailable, error,
            form?["backupId"].ToString() ?? http.Request.Query["restore"].ToString(), form?["name"].ToString() ?? http.Request.Query["name"].ToString(),
            form is not null && form.ContainsKey("fullBackupCron") ? form["fullBackupCron"].ToString() : null,
            form is not null && form.ContainsKey("retentionDays") ? form["retentionDays"].ToString() : null);
    }
    internal static bool KnownFailure(Exception error) => DatabaseResults.KnownFailure(error) || error is HttpRequestException or JsonException or OperationCanceledException;
    internal static string Message(Exception error) => error is AccountAccessException ? error.Message : DatabaseResults.Message(error);
    private static IResult Failure(Exception error) => Results.Problem(Message(error), statusCode: DatabaseResults.Status(error));
}
