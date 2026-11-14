using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Datastar;

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
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "backups"), Results.BadRequest("Invalid form token."));
            }

            var databaseId = form["databaseId"].ToString();
            try
            {
                if (action == "create")
                {
                    await backups.CreateAsync(context, databaseId, http.RequestAborted);
                    if (DatabaseEndpoints.WantsHtml(http) && http.Request.Headers["Datastar-Request"] == "true")
                    {
                        var page = await PageAsync(http, context, DatabaseService.Database(context, databaseId), "");
                        return await PatchBackupsAsync(http, page);
                    }

                    return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "backups")) : Results.NoContent();
                }
                if (action == "policy")
                {
                    var days = int.TryParse(form["retentionDays"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                    var policy = await backups.SetPolicyAsync(context, databaseId, form["fullBackupCron"].ToString(),
                        form["incrementalBackupCron"].ToString(), days, http.RequestAborted);
                    if (DatabaseEndpoints.WantsHtml(http) && http.Request.Headers["Datastar-Request"] == "true")
                    {
                        var page = await PageAsync(http, context, DatabaseService.Database(context, databaseId), "");
                        return await PatchBackupsAsync(http, page);
                    }

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

                    if (DatabaseEndpoints.WantsHtml(http) && http.Request.Headers["Datastar-Request"] == "true")
                    {
                        var page = await PageAsync(http, context, DatabaseService.Database(context, databaseId), "");
                        return await PatchBackupsAsync(http, page);
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

        private static async Task<IResult> PatchBackupsAsync(HttpContext http, BackupPage page)
        {
            await DatastarEvents.PatchElementsAsync(http, BackupSection.Create(page));
            return Results.Empty;
        }

        internal static async Task<BackupPage> PageAsync(HttpContext http, AccountContext context, DatabaseRecord database, string error, bool export = false)
        {
            BackupListing? listing = null;
            try { listing = await http.RequestServices.GetRequiredService<BackupService>().ListAsync(context, database.Id, http.RequestAborted); }
            catch (Exception failure) when (KnownFailure(failure) &&
                (failure is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            { }
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

            WholeDatabaseRecoveryPage? recovery = null;
            PitrCardModel? pitr = null;
            if (context.CanManage && database.Status == "ready")
            {
                pitr = await PitrCardAsync(http, context, database, recovery, listing, bucket, http.RequestAborted);
                recovery = pitr.Recovery;
            }

            return new(ShellModel.For(http, context, database.Name + " · " + DatabaseFrameModel.Title("backups")), database, listing, error,
                form?["backupId"].ToString() ?? "", form?["name"].ToString() ?? http.Request.Query["name"].ToString(),
                form is not null && form.ContainsKey("fullBackupCron") ? form["fullBackupCron"].ToString() : null,
                form is not null && form.ContainsKey("retentionDays") ? form["retentionDays"].ToString() : null)
            { Bucket = bucket, BucketRead = export, Restore = restore, Recovery = recovery };
        }

        internal static async Task<PitrCardModel> PitrCardAsync(HttpContext http, AccountContext context, DatabaseRecord database,
            WholeDatabaseRecoveryPage? recovery, BackupListing? listing, BackupBucketView? bucket, CancellationToken ct)
        {
            var service = http.RequestServices.GetRequiredService<WholeDatabaseRecovery>();
            var destination = http.Request.Query["pitr"].ToString() is { Length: > 0 } picked ? picked : recovery?.Destination is { Length: > 0 } saved ? saved : "platform";
            var page = recovery is null || recovery.Destination != destination
                ? new WholeDatabaseRecoveryPage(ShellModel.For(http, context, database.Name + " · Backups"), database, destination)
                : recovery;
            if (context.CanManage && database.Status == "ready" && page.Available is null)
            {
                try { page = page with { Available = await service.WindowAsync(context, database.Id, destination, ct) }; }
                catch (Exception failure) when (KnownFailure(failure) && (failure is not OperationCanceledException || !ct.IsCancellationRequested))
                { page = page with { Error = page.Error.Length == 0 ? Message(failure) : page.Error }; }
            }

            if (page.Available?.Window is { } bounds && page.Point.Length == 0)
            {
                page = page with { Point = RecoveryPoint.Input(bounds.Latest) };
            }

            if (listing is null)
            {
                try { listing = await http.RequestServices.GetRequiredService<BackupService>().ListAsync(context, database.Id, ct); }
                catch (Exception failure) when (KnownFailure(failure) && (failure is not OperationCanceledException || !ct.IsCancellationRequested))
                { }
            }

            if (bucket is null)
            {
                try { bucket = await http.RequestServices.GetRequiredService<BackupService>().BucketAsync(context, database.Id, ct); }
                catch (Exception failure) when (KnownFailure(failure) && (failure is not OperationCanceledException || !ct.IsCancellationRequested))
                { }
            }

            var window = page.Available?.Window;
            IReadOnlyList<BackupView> points = window is { } span && listing is not null
                ? [.. listing.Backups.Where(backup => backup.CreatedAt >= span.Earliest && backup.CreatedAt <= span.Latest)]
                : [];
            return new(page.Shell, database, page, points, bucket?.Destination is not null);
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
