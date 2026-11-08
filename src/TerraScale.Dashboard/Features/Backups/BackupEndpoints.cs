using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Backups
{
    public static class BackupEndpoints
    {
        // detailPage re-renders the database page's backups tab: (http, context, databaseId, error, status).
        public static IEndpointRouteBuilder MapBackupFeatures(this IEndpointRouteBuilder endpoints, Func<HttpContext, AccountContext, string, string, int, Task<IResult>> detailPage)
        {
            endpoints.MapGet("/api/v1/dashboard/backups", async (HttpContext http, DashboardSessions sessions, AccountService accounts, BackupService backups) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var (limit, after) = CursorPaging.Read(http.Request);
                    var id = http.Request.Query["databaseId"].ToString();
                    var slice = await backups.ListPageAsync(context, id, after.Length == 0 ? null : after, limit, http.RequestAborted);
                    return Results.Json(new BackupRestList(slice.Policy, slice.Items, slice.NextCursor, slice.HasMore, limit));
                }
                catch (Exception error) when (KnownFailure(error) &&
                    (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
                { return Failure(error); }
            });
            foreach (var operation in new[] { "create", "policy", "restore", "destination", "destination-remove" })
            {
                var action = operation;
                endpoints.MapPost(action == "create" ? "/api/v1/dashboard/backups" : "/api/v1/dashboard/backups/" + action,
                    async (HttpContext http, DashboardSessions sessions, AccountService accounts, BackupService backups) =>
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
                                    form["incrementalBackupCron"].ToString(), days, form["disabled"] == "1", http.RequestAborted);
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

                            var restored = await backups.RestoreAsync(context, databaseId, form["backupId"].ToString(), form["name"].ToString(), http.RequestAborted);
                            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(restored.DatabaseId)) :
                                Results.Json(new { id = restored.DatabaseId, restored.Name }, statusCode: 201);
                        }
                        catch (Exception error) when (KnownFailure(error) &&
                            (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
                        {
                            if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404 && error is not AccountAccessException { Code: "not_found" })
                            {
                                return Failure(error);
                            }

                            return await detailPage(http, context, databaseId, Message(error), DatabaseResults.Status(error));
                        }
                    });
            }
            return endpoints;
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
            return new(ShellModel.For(http, context, database.Name + " · backups"), database, listing, unavailable, error,
                form?["backupId"].ToString() ?? http.Request.Query["restore"].ToString(), form?["name"].ToString() ?? http.Request.Query["name"].ToString(),
                form is not null && form.ContainsKey("fullBackupCron") ? form["fullBackupCron"].ToString() : null,
                form is not null && form.ContainsKey("retentionDays") ? form["retentionDays"].ToString() : null)
            { Bucket = bucket, BucketRead = export };
        }

        internal static bool KnownFailure(Exception error)
        {
            return DatabaseResults.KnownFailure(error) || error is HttpRequestException or JsonException or OperationCanceledException;
        }

        internal static string Message(Exception error)
        {
            return error is AccountAccessException ? error.Message : DatabaseResults.Message(error);
        }

        private static IResult Failure(Exception error)
        {
            return Results.Problem(Message(error), statusCode: DatabaseResults.Status(error));
        }
    }
}
