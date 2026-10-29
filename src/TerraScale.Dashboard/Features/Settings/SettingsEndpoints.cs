using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Settings;

public static class SettingsEndpoints
{
    public static IServiceCollection AddSettingsFeatures(this IServiceCollection services) => services.AddSingleton<ISettingsBackend, SettingsBackend>().AddSingleton<SettingsService>();
    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder endpoints)
    {
        foreach (var operation in new[] { "audit-retention", "delete-project", "backup-policy", "backup-now", "restore" })
        {
            var action = operation;
            endpoints.MapPost("/api/v1/dashboard/settings/" + action, async (HttpContext http, DashboardSessions sessions, AccountService accounts, SettingsService settings) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null) return Results.Unauthorized();
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                try
                {
                    switch (action)
                    {
                        case "audit-retention":
                            _ = int.TryParse(form["retentionDays"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var days);
                            await settings.SetAuditRetentionAsync(context, days, http.RequestAborted); break;
                        case "delete-project": await settings.DeleteProjectAsync(context, form["projectId"].ToString(), form["confirmName"].ToString(), http.RequestAborted); break;
                        case "backup-policy":
                            var retention = int.TryParse(form["retentionDays"], System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                            await settings.SetBackupPolicyAsync(context, form["databaseId"].ToString(), form["fullBackupCron"].ToString(),
                                form["incrementalBackupCron"].ToString(), retention, form["disabled"].ToString() is "on" or "true" or "1", http.RequestAborted); break;
                        case "backup-now": await settings.CreateBackupAsync(context, form["databaseId"].ToString(), http.RequestAborted); break;
                        case "restore":
                            if (form["replace"].ToString() is "on" or "true" or "1") BackupService.RejectOverwrite();
                            await settings.RestoreBackupAsync(context, form["databaseId"].ToString(), form["backupId"].ToString(), http.RequestAborted); break;
                    }
                    return HttpRedirects.SeeOther(action == "delete-project" ? "/organization" : "/");
                }
                catch (AccountAccessException error) when (error.Code == "deletion_pending")
                {
                    return DeletionProgress.Create(http, new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                    { ["_csrf"] = form["_csrf"], ["confirmation"] = form["confirmName"] }),
                        "/api/v1/dashboard/organization/projects/" + Uri.EscapeDataString(form["projectId"].ToString()) + "/delete?organizationId=" + Uri.EscapeDataString(context.Organization!.Id));
                }
                catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
                catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                { return Results.Text("The provider could not complete this settings change. Try again shortly.", statusCode: 503); }
            });
        }
        return endpoints;
    }
}
