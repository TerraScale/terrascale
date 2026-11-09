using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Backups
{
    public static class BackupEndpoints
    {
        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, BackupService backups, string action)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

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
                        form["incrementalBackupCron"].ToString(), days, http.RequestAborted);
                    return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "backups")) : Results.Json(policy);
                }
                if (action is "destination" or "destination-remove")
                {
                    object? answer = action == "destination" ? await backups.SetDestinationAsync(context, databaseId, form["endpoint"].ToString(), form["region"].ToString(),
                        form["bucket"].ToString(), form["accessKeyId"].ToString(), form["secretKey"].ToString(), http.RequestAborted) : null;
                    if (action == "destination-remove")
                    {
                        await backups.RemoveDestinationAsync(context, databaseId, http.RequestAborted);
                    }

                    return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "backups")) : answer is null ? Results.NoContent() : Results.Json(answer);
                }
                if (form["replace"] == "1")
                {
                    BackupService.RejectOverwrite();
                }

                var started = await backups.StartRestoreAsync(context, databaseId, form["backupId"].ToString(), form["name"].ToString(), http.RequestAborted);
                return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "backups") + "?restore=" + started.OperationId) :
                    Results.Json(new { operationId = started.OperationId, status = started.Status }, statusCode: 202);
            }
            catch (Exception error) when (KnownFailure(error) &&
                (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            {
                if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404 && error is not AccountAccessException { Code: "not_found" })
                {
                    return FailureResult(error);
                }

                return await DatabaseEndpoints.BackupDetailPageAsync(http, context, databaseId, Message(error), DatabaseResults.Status(error));
            }
        }

        internal static async Task<BackupPage> PageAsync(HttpContext http, AccountContext context, DatabaseRecord database, string error, bool export = false)
        {
            BackupListing? listing = null;
            var unavailable = "";
            try { listing = await http.RequestServices.GetRequiredService<BackupService>().ListAsync(context, database.Id, http.RequestAborted); }
            catch (Exception failure) when (KnownFailure(failure) &&
                (failure is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            { unavailable = Message(failure); }
            BackupBucketView? bucket = null;
            if (export)
            {
                try { bucket = await http.RequestServices.GetRequiredService<BackupService>().BucketAsync(context, database.Id, http.RequestAborted); }
                catch (Exception failure) when (KnownFailure(failure) &&
                    (failure is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
                { }
            }

            var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
            RestoreProgress? restore = null;
            var restoreOperation = http.Request.Query["restore"].ToString();
            if (restoreOperation.Length > 0)
            {
                try { restore = await http.RequestServices.GetRequiredService<BackupService>().RestoreStatusAsync(context, database.Id, restoreOperation, http.RequestAborted); }
                catch (Exception failure) when (KnownFailure(failure) &&
                    (failure is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
                { restore = new(restoreOperation, "failed", ErrorMessage: Message(failure)); }
            }

            return new(ShellModel.For(http, context, database.Name + " · backups"), database, listing, unavailable, error,
                form?["backupId"].ToString() ?? "", form?["name"].ToString() ?? http.Request.Query["name"].ToString(),
                form is not null && form.ContainsKey("fullBackupCron") ? form["fullBackupCron"].ToString() : null,
                form is not null && form.ContainsKey("retentionDays") ? form["retentionDays"].ToString() : null)
            { Bucket = bucket, BucketRead = export, Restore = restore };
        }

        internal static bool KnownFailure(Exception error)
        {
            return DatabaseResults.KnownFailure(error) || error is HttpRequestException or JsonException or OperationCanceledException;
        }

        internal static string Message(Exception error)
        {
            return error is AccountAccessException ? error.Message : DatabaseResults.Message(error);
        }

        internal static IResult FailureResult(Exception error)
        {
            return Results.Problem(Message(error), statusCode: DatabaseResults.Status(error));
        }
    }
}
