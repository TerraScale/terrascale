using System.Globalization;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed record WholeDatabaseWindow(string Destination, TerraBaseRestoreWindow? Window, int DatabasesInUse, int DatabaseLimit);
    public sealed record WholeDatabaseRecoveryPage(ShellModel Shell, DatabaseRecord Database, string Destination = "platform", string Point = "",
        string Name = "", string ConfirmName = "", WholeDatabaseWindow? Available = null, TerraBasePointInTimePlan? Plan = null, string Error = "");

    public sealed class WholeDatabaseRecovery(IHttpClientFactory factory, IProjectAccessTokens tokens, DatabaseService databases, BackupService backups)
    {
        public async Task<(AccountContext Context, DatabaseRecord Database)> SourceAsync(AccountContext context, string id, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            var source = DatabaseService.Database(context, id);
            if (!context.CanManage)
            {
                throw new AccountAccessException("Organization management access is required.");
            }

            if (PlatformDatabases.IsPlatform(context.Organization!.Id))
            {
                throw PlatformDatabases.ReadOnly();
            }

            if (source.Status != "ready" || context.Organization.DeletionRequested)
            {
                throw new AccountAccessException("Recovery requires an active, ready database.", 409);
            }

            return (context, source);
        }

        public async Task<WholeDatabaseWindow> WindowAsync(AccountContext context, string databaseId, string destination, CancellationToken ct)
        {
            (context, var source) = await SourceAsync(context, databaseId, ct);
            Destination(destination);
            TerraBaseRestoreWindow? window = null;
            try { window = await SafeAsync(async () => await (await ClientAsync(context, source, "databases:read", ct)).Database(context.Project!.Id, source.Name).GetRestoreWindowAsync(destination, ct)); }
            catch (AccountAccessException error) when (error.StatusCode == 404) { }
            if (window is not null && (window.Earliest == default || window.Latest < window.Earliest))
            {
                throw new HttpRequestException("Invalid PITR window.");
            }

            return new(destination, window, context.Organization!.Projects.Values.Sum(project => project.Databases.Count), DatabaseService.DatabaseLimit(context.Organization));
        }

        public async Task<TerraBasePointInTimePlan> PlanAsync(AccountContext context, string databaseId, string destination, string point, CancellationToken ct)
        {
            (context, var source) = await SourceAsync(context, databaseId, ct);
            var instant = Instant(point, await WindowAsync(context, databaseId, destination, ct));
            var plan = await SafeAsync(async () => await (await ClientAsync(context, source, "databases:read", ct)).Database(context.Project!.Id, source.Name).PlanPointInTimeAsync(instant, destination, ct));
            if (plan.HistoryCheck != "metadata" || plan.PointInTime != instant || plan.SnapshotAt == default || plan.SnapshotAt > instant || plan.SegmentCount < 0 ||
                plan.Window is null || plan.Window.Earliest > instant || plan.Window.Latest < instant)
            {
                throw new HttpRequestException("Invalid PITR plan.");
            }

            return plan;
        }

        public async Task<BackupRestoreResult> RestoreAsync(AccountContext context, string databaseId, string destination, string point, string name, string confirmName, CancellationToken ct)
        {
            (context, var source) = await SourceAsync(context, databaseId, ct);
            DatabaseService.DemandProvision(context.Organization!, context.UserId);
            DatabaseService.DemandRoom(context.Organization!);
            name = DatabaseService.ValidName(name);
            DatabaseService.DemandName(context.Project!, name);
            if (confirmName != name)
            {
                throw new AccountAccessException("Type the new database name exactly to confirm restore as a new database.", 422);
            }

            var instant = Instant(point, await WindowAsync(context, databaseId, destination, ct));
            var home = await databases.HomeAsync(context, source, ct);
            var restored = await SafeAsync(async () => await (await ClientAsync(context, source, "databases:write", ct)).Database(context.Project!.Id, source.Name).RestorePointInTimeAsync(instant, name, destination, ct));
            if (restored.Id == source.Id || restored.Name != name || restored.ProjectId != context.Project!.Id || restored.BranchedFrom != source.Id)
            {
                throw new AccountAccessException("Restore could not be confirmed. Check the database list before submitting another restore.", 503);
            }

            var placed = await SafeAsync(async () => await (await ClientAsync(context, source, "databases:read", ct)).Project(context.Project!.Id).GetDatabaseAsync(name, ct));
            if (placed.Id != restored.Id || placed.CreatedAt != restored.CreatedAt || placed.ReplicaRegions is null)
            {
                throw new AccountAccessException("Restore readback could not be confirmed. Check the database list before submitting another restore.", 503);
            }

            return await backups.RecordRestoreAsync(context, source, home, new(placed.Id, placed.ProjectId, placed.Name, placed.CreatedAt,
                placed.BranchedFrom, placed.HomeRegion, placed.ReplicaRegions, placed.AllRegions), name, ct);
        }

        private static void Destination(string destination)
        {
            if (destination is not ("platform" or "customer"))
            {
                throw new AccountAccessException("Choose platform or customer backup history.", 422);
            }
        }
        private static DateTimeOffset Instant(string point, WholeDatabaseWindow available)
        {
            // get_point_in_time_window serializes its bounds with a +00:00 offset; accept them as returned.
            if (!DateTimeOffset.TryParseExact(point,
                ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
            {
                throw new AccountAccessException("Enter an ISO 8601 timestamp such as 2026-10-27T22:59:00Z or 2026-10-27T22:59:00+00:00.", 422);
            }

            if (available.Window is not { } window || instant < window.Earliest || instant > window.Latest)
            {
                throw new AccountAccessException("Choose a UTC timestamp inside the current retained window. Refresh the window if retention has advanced.", 422);
            }

            return instant;
        }
        private async Task<TerraBaseDocumentsClient> ClientAsync(AccountContext context, DatabaseRecord source, string permission, CancellationToken ct)
        {
            var home = await databases.HomeAsync(context, source, ct);
            var access = await tokens.GetAsync(context.Session, context.Organization!.Id, context.Project!.Id, [permission], ct);
            return new(factory.CreateClient("platform"), new TerraBaseDocumentsClientOptions
            { BaseAddress = home.Api, Region = home.Id, AccessTokenProvider = _ => ValueTask.FromResult(access) });
        }
        private static async Task<T> SafeAsync<T>(Func<Task<T>> action)
        {
            try { return await action(); }
            catch (TerraBaseWrongRegionException error) { throw new DatabaseWrongRegionException(error.Region, error.HomeRegion, error.Regions); }
            catch (TerraBaseException error)
            {
                // Only known codes select fixed text. Provider messages can contain object paths or secrets.
                var message = error.ErrorCode switch
                {
                    "backup_history_incomplete" => "Backup history is incomplete for that time. Refresh the window and choose another retained point.",
                    "restore_point_unproven" => "The backup cannot prove that exact restore time. Choose a snapshot time or the window's latest point.",
                    "backup_unreadable" => "Required backup history could not be verified. Contact support with the request ID.",
                    "database_limit" => "Restoring needs one free database slot in your organization allowance. No quota is increased.",
                    "name_taken" => "That database name is already in use. Choose a new name.",
                    _ => error.StatusCode switch
                    {
                        System.Net.HttpStatusCode.BadRequest => "The time is outside retained history or the recovery input is invalid. Refresh the window.",
                        System.Net.HttpStatusCode.NotFound => "No retained PITR history is available for this source bucket.",
                        System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized => "Recovery access was refused. Check your organization and project access.",
                        System.Net.HttpStatusCode.Conflict => "Recovery conflicts with the current database state. Refresh the window and review the new name.",
                        _ => "Recovery is temporarily unavailable. Check the database list before retrying a restore; its outcome may be unknown."
                    }
                };
                throw new AccountAccessException(message, (int?)error.StatusCode ?? 503);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or System.Text.Json.JsonException or OperationCanceledException)
            { throw new AccountAccessException("Recovery could not be confirmed. Check the database list before retrying a restore; its outcome may be unknown.", 503); }
        }
    }

    public static class WholeDatabaseRecoveryEndpoints
    {
        public static IEndpointRouteBuilder MapWholeDatabaseRecovery(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapMethods("/databases/{databaseId}/point-in-time", ["GET", "POST"], async Task<IResult> (HttpContext http, string databaseId,
                DashboardSessions sessions, AccountService accounts, WholeDatabaseRecovery recovery) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                try
                {
                    (context, var database) = await recovery.SourceAsync(context, databaseId, http.RequestAborted);
                    http.Response.Headers.CacheControl = "no-store";
                    var form = HttpMethods.IsPost(http.Request.Method) ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
                    if (form is not null && !Csrf.Validate(http, form))
                    {
                        return Results.BadRequest("Invalid form token.");
                    }

                    var page = new WholeDatabaseRecoveryPage(ShellModel.For(http, context, "Point-in-time recovery"), database,
                        form?["destination"].ToString() ?? http.Request.Query["destination"].FirstOrDefault() ?? "platform",
                        form?["point"].ToString() ?? "", form?["name"].ToString() ?? "", form?["confirmName"].ToString() ?? "");
                    var status = 200;
                    try
                    {
                        page = page with { Available = await recovery.WindowAsync(context, databaseId, page.Destination, http.RequestAborted) };
                        if (form is null && page.Available.Window is { } window)
                        {
                            page = page with { Point = RecoveryWindow.Input(window.Latest) };
                        }

                        if (form?["operation"].ToString() == "plan")
                        {
                            page = page with { Plan = await recovery.PlanAsync(context, databaseId, page.Destination, page.Point, http.RequestAborted) };
                        }

                        if (form?["operation"].ToString() == "restore")
                        {
                            var restored = await recovery.RestoreAsync(context, databaseId, page.Destination, page.Point, page.Name, page.ConfirmName, http.RequestAborted);
                            return HttpRedirects.SeeOther(DatabaseService.Path(restored.DatabaseId));
                        }
                    }
                    catch (Exception error) when (BackupEndpoints.KnownFailure(error) && !http.RequestAborted.IsCancellationRequested)
                    { status = DatabaseResults.Status(error); page = page with { Error = DatabaseResults.Message(error) }; }
                    var view = WholeDatabaseRecoveryView.Create(page); view.StatusCode = status; return view;
                }
                catch (Exception error) when (BackupEndpoints.KnownFailure(error)) { return DatabaseResults.PageFailure(http, context, error); }
            });
            return endpoints;
        }
    }
}
