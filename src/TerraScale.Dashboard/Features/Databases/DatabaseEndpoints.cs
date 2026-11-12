using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Functions;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Databases
{
    public static class DatabaseEndpoints
    {
        public static IServiceCollection AddDatabaseFeatures(this IServiceCollection services)
        {
            return services
            .AddSingleton<IDatabaseBackend, DatabaseBackend>().AddSingleton<DatabaseService>()
            .AddSingleton<DatabaseConflicts>().AddSingleton<DataService>().AddSingleton<SavedQueryService>().AddSingleton<SqlRuns>()
            .AddSingleton<ApiKeyService>().AddSingleton<IWorkspacePlacement, WorkspacePlacement>()
            .AddSingleton<IOnboardingProjectProvisioner, OnboardingProjectProvisioner>()
            .AddSingleton<IBackupBackend, BackupBackend>().AddSingleton<BackupService>().AddSingleton<WholeDatabaseRecovery>().AddDatabaseLogs().AddDatabaseFunctions();
        }

        public static IEndpointRouteBuilder MapDatabaseFeatures(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapWholeDatabaseRecovery();
            return endpoints;
        }

        internal static async Task<IResult> ListAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases,
            TerraScale.Dashboard.Features.Backups.BackupService backups)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            context = await backups.SettleRestoresAsync(context, http.RequestAborted);
            return await ListPageAsync(http, context, backend, databases);
        }

        internal static async Task<IResult> ApiListAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var (limit, after) = CursorPaging.Read(http.Request);
                context = await http.RequestServices.GetRequiredService<TerraScale.Dashboard.Features.Backups.BackupService>().SettleRestoresAsync(context, http.RequestAborted);
                await databases.SyncAsync(context, http.RequestAborted);
                var page = CursorPaging.Page(context.Project?.Databases.Values ?? Enumerable.Empty<DatabaseRecord>(),
                    db => db.Id, CursorPaging.Scope(context, "databases"), limit, after);
                var restricted = context.Organization is { } organization && !organization.ContainsMember(context.UserId);
                return Results.Json(new CursorPage<object>([.. page.Items.Select(database => DatabaseOutput(database, restricted))], page.NextCursor, page.Limit));
            }
            catch (Exception error) when (KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException) { return Failure(error); }
        }

        internal static async Task<IResult> TabAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases, string section)
        {
            var databaseId = http.Request.RouteValues["databaseId"]?.ToString() ?? "";
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountEndpoints.LoginRedirect(http);
            }
            if (section == "overview" && http.Request.Query["delete"] == "1")
            {
                return HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "danger") + "?delete=1");
            }
            if (section == "replication")
            {
                await databases.SyncAsync(context, http.RequestAborted);
            }

            return await DetailPageAsync(http, context, databaseId, section, backend, databases);
        }

        internal static async Task<IResult> CreateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/databases", Results.BadRequest("Invalid form token."));
            }

            try
            {
                var regions = Regions(form, context);
                var database = await databases.CreateAsync(context, form["name"].ToString(), regions[0], http.RequestAborted, DatabaseTtl.Hours(form["ttlHours"].ToString()), [.. regions.Skip(1)]);
                return WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(database.Id) + "?created=1") : Results.Json(DatabaseOutput(database), statusCode: 201);
            }
            catch (Exception error) when (KnownFailure(error))
            {
                return WantsHtml(http) ? await ListPageAsync(http, context, backend, databases, error: Message(error), openCreate: true, name: form["name"].ToString(), status: Status(error), selection: Picked(form) ? Chosen(form) : null) : Failure(error);
            }
        }

        private static bool Picked(IFormCollection form)
        {
            return form.ContainsKey("regions") || form.ContainsKey("regionOrder");
        }

        private static IReadOnlyList<string> Regions(IFormCollection form, AccountContext context)
        {
            return Picked(form) ? DatabaseService.Capped(DatabasePlacementEndpoints.Selection(form, "regionOrder"))
                : context.Project?.Region is { Length: > 0 } region ? [region] : throw new AccountAccessException(DatabasePlacementEndpoints.Minimum, 422);
        }

        private static IReadOnlyList<string> Chosen(IFormCollection form)
        {
            try { return DatabaseService.Capped(DatabasePlacementEndpoints.Selection(form, "regionOrder")); }
            catch (AccountAccessException) { return []; }
        }

        internal static async Task<IResult> OperateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend, string action)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), action == "delete" ? "danger" : "branches"), Results.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            try
            {
                if (action == "branch")
                {
                    var pointInTime = form["pointInTime"].ToString();
                    if (!string.IsNullOrWhiteSpace(pointInTime))
                    {
                        var recovery = http.RequestServices.GetRequiredService<TerraScale.Dashboard.Features.Backups.WholeDatabaseRecovery>();
                        var started = await recovery.StartForkAsync(context, id, pointInTime, form["name"].ToString(), DatabaseTtl.Seconds(DatabaseTtl.Hours(form["ttlHours"].ToString())), http.RequestAborted);
                        return WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "backups") + "?restore=" + started.OperationId) :
                            Results.Json(new { operationId = started.OperationId, status = started.Status }, statusCode: 202);
                    }
                    var branch = await databases.BranchAsync(context, id, form["name"].ToString(), http.RequestAborted, DatabaseTtl.Hours(form["ttlHours"].ToString()));
                    return WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(branch.Id)) : Results.Json(DatabaseOutput(branch), statusCode: 201);
                }
                await databases.DeleteAsync(context, id, form.ContainsKey("confirmName") ? form["confirmName"].ToString() : form["confirm"].ToString(), http.RequestAborted);
                return WantsHtml(http) ? HttpRedirects.SeeOther("/databases") : Results.NoContent();
            }
            catch (Exception error) when (KnownFailure(error))
            {
                if (!WantsHtml(http) || Status(error) == 404)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }

                return await DetailPageAsync(http, context, id, action == "branch" ? "branches" : "danger", backend, databases, Message(error), action == "delete", Status(error), branchName: action == "branch" ? form["name"].ToString() : null, ttlHours: form["ttlHours"].ToString(), pointInTime: form["pointInTime"].ToString());
            }
        }

        internal static Task<IResult> BackupDetailPageAsync(HttpContext http, AccountContext context, string databaseId, string error, int status) =>
            DetailPageAsync(http, context, databaseId, "backups", http.RequestServices.GetRequiredService<IDatabaseBackend>(),
                http.RequestServices.GetRequiredService<DatabaseService>(), error, status: status);

        // Keep the storage revision private; database responses retain their existing fields.
        internal static object DatabaseOutput(DatabaseRecord database, bool restricted = false)
        {
            return new
            {
                database.Id,
                database.ProjectId,
                database.Name,
                database.Region,
                database.Regions,
                database.AllRegions,
                database.Endpoint,
                database.Status,
                SpendCapCents = restricted ? (long?)null : database.SpendCapCents,
                OperationLimits = restricted ? null : database.OperationLimits,
                BackupRetentionDays = restricted ? (int?)null : database.BackupRetentionDays,
                BranchedFrom = restricted ? null : database.BranchedFrom,
                database.CreatedAt,
                database.ExpiresAt
            };
        }

        internal static async Task<IResult> ListPageAsync(HttpContext http, AccountContext context, IDatabaseBackend backend, DatabaseService databases,
            string error = "", bool openCreate = false, string name = "", int status = 200, IReadOnlyList<string>? selection = null)
        {
            var synced = await OptionalPanel.RunAsync(http, "database-sync", () => databases.SyncAsync(context, http.RequestAborted));
            var range = HomePage.NormalizeRange(http.Request.Query["range"].ToString());
            var metricsRead = OptionalPanel.LoadAsync(http, "database-metrics",
                () => DatabaseListMetrics.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, range, http.RequestAborted), DatabaseListMetrics.Failed);
            var regionsRead = backend.DiscoveryAsync(http.RequestAborted);
            IReadOnlyList<DatabaseRegion> choices = [];
            var regionsError = "";
            try
            {
                var discovery = await regionsRead;
                choices = discovery.Regions;
            }
            catch (HttpRequestException) { regionsError = "Region discovery is unavailable. Try again shortly."; }
            var search = http.Request.Query["q"].ToString().Trim();
            var all = context.Project?.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).ToArray() ?? [];
            var filtered = all.Where(database => search.Length == 0 || database.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            var metrics = await metricsRead;
            var result = DatabasesView.Create(new(ShellModel.For(http, context, "Databases"), filtered, choices, search, error, openCreate || http.Request.Query["create"] == "1", name, regionsError) { Metrics = metrics, Range = range, Selection = selection, Notice = synced ? "" : "The database list could not be refreshed. Reload the page to retry." });
            result.StatusCode = status;
            return result;
        }

        internal static async Task<IResult> DetailPageAsync(HttpContext http, AccountContext context, string id, string tab, IDatabaseBackend backend, DatabaseService databases, string error = "", bool openDelete = false, int status = 200, string? branchName = null, string ttlHours = "", string pointInTime = "", DatabaseGrantForm? grantForm = null, DatabaseRegionsForm? regions = null)
        {
            DatabaseRecord database;
            try
            {
                if (tab is "overview" or "backups")
                {
                    context = await http.RequestServices.GetRequiredService<TerraScale.Dashboard.Features.Backups.BackupService>().SettleRestoresAsync(context, http.RequestAborted);
                }

                if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                {
                    (context, database) = await databases.LookupAsync(context, id, http.RequestAborted);
                }
                else
                {
                    database = DatabaseService.Database(context, id);
                }
            }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested &&
                (KnownFailure(failure) || failure is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException))
            { return DatabaseResults.PageFailure(http, context, failure); }
            IReadOnlyList<DatabaseRegion> choices = [];
            var range = HomePage.NormalizeRange(http.Request.Query["range"]);
            var selectedTable = tab == "tables" ? http.Request.Query["table"].ToString() : "";
            // Each tab's reads are independent of one another, so they all start before any is awaited.
            string CardRange(string key)
            {
                return http.Request.Query[key].ToString() is { Length: > 0 } selected ? HomePage.NormalizeRange(selected) : range;
            }

            var activityRead = tab == "overview"
                ? OptionalPanel.LoadAsync(http, "database-activity",
                    () => DatabaseActivity.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, database.Id, CardRange, http.RequestAborted),
                    () => DatabaseActivity.Failed(CardRange)) : null;
            var storageRead = tab == "overview" ? OptionalPanel.LoadAsync(http, "database-storage",
                () => DatabaseListMetrics.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, "24h", http.RequestAborted), DatabaseListMetrics.Failed) : null;
            var backupsRead = tab is "backups" or "overview" ? BackupEndpoints.PageAsync(http, context, database, error, tab == "backups") : null;
            async Task<(DatabaseTables Tables, string Table, DatabaseSqlResult? Preview)> TablesAsync()
            {
                var scope = DatabaseService.Scope(context);
                var home = await databases.HomeAsync(context, database, http.RequestAborted);
                var tables = await DatabaseTables.LoadAsync(backend, scope, home, database.Id, http.RequestAborted);
                var shown = tables.Tables.FirstOrDefault(table => table.Name == selectedTable) ?? (selectedTable.Length == 0 && tables.Tables.Count > 0 ? tables.Tables[0] : null);
                return tab != "tables" || shown is null ? (tables, "", null) : (tables, shown.Name, await DatabaseTables.PreviewAsync(backend, scope, home, database.Id, shown.Name, http.RequestAborted));
            }

            var replicationRead = tab == "replication" ? DatabaseReplication.LoadAsync(http, context, database, databases) : null;
            async Task<DatabaseConflictsPage> ConflictsAsync()
            {
                var table = http.Request.Query["table"].ToString();
                var since = http.Request.Query["since"].ToString();
                var until = http.Request.Query["until"].ToString();
                var before = http.Request.Query["before"].ToString();
                try
                {
                    var listing = await http.RequestServices.GetRequiredService<DatabaseConflicts>()
                        .ListAsync(context, database.Id, table, since, until, 50, before, http.RequestAborted);
                    return new(listing, table, since, until, before);
                }
                catch (Exception failure) when (failure is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException
                    || KnownFailure(failure) || failure is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                {
                    return new(null, table, since, until, before, failure is AccountAccessException { StatusCode: 422 } ? failure.Message : "Conflicts for " + database.Name + " are unavailable.");
                }
            }
            var conflictsRead = tab == "conflicts" ? ConflictsAsync() : null;
            var tablesRead = tab is "overview" or "tables" or "grants" ? TablesAsync() : null;
            var grantsRead = tab == "grants" ? DatabaseGrants.ListAsync(context, database, backend, databases, http.RequestAborted) : null;
            // An all-regions database serves every catalog region; without the catalog it shows its recorded regions.
            if (tab == "replication" || tab == "overview" && database.AllRegions)
            {
                try { choices = await backend.RegionsAsync(http.RequestAborted); }
                catch (HttpRequestException) { }
            }

            DatabaseTables? tables = null;
            DatabaseSqlResult? preview = null;
            var shownTable = "";
            if (tablesRead is not null)
            {
                try { (tables, shownTable, preview) = await tablesRead; }
                catch (Exception failure) when (failure is HttpRequestException or System.Text.Json.JsonException or OverflowException or InvalidOperationException || KnownFailure(failure))
                {
                    if (error.Length == 0)
                    {
                        error = "Tables are unavailable. The data plane did not answer.";
                    }
                }
            }
            IReadOnlyList<DatabaseReadGrant>? grants = null;
            if (grantsRead is not null)
            {
                try { grants = await grantsRead; }
                catch (Exception failure) when (failure is HttpRequestException or System.Text.Json.JsonException || KnownFailure(failure))
                {
                    if (error.Length == 0)
                    {
                        error = "Grants are unavailable. The data plane did not answer.";
                    }
                }
            }
            var activity = activityRead is null ? null : await activityRead;
            var storage = storageRead is null ? null : (await storageRead).Databases.GetValueOrDefault(database.Id)?.StorageBytes;
            // The backups section shows the same page error, which the tables read above may have just set.
            var backups = backupsRead is null ? null : (await backupsRead) with { Error = error };
            var page = new DatabaseDetailPage(ShellModel.For(http, context, database.Name + " · " + DatabaseFrameModel.Title(tab)), database, tab, choices, error, openDelete || http.Request.Query["delete"] == "1", http.Request.Query["created"] == "1", backups, range, activity)
            {
                Conflicts = conflictsRead is null ? null : await conflictsRead,
                Replication = replicationRead is null ? null : await replicationRead,
                BranchName = branchName,
                ShowFork = http.Request.Query["fork"] == "1",
                TtlHours = ttlHours,
                PointInTime = pointInTime,
                Tables = tables,
                Table = shownTable,
                Preview = preview,
                StorageBytes = storage,
                Grants = grants,
                GrantForm = grantForm,
                RegionsForm = regions
            };
            if (tab == "replication" && http.Request.Headers["Datastar-Request"] == "true")
            {
                await DatastarEvents.PatchElementsAsync(http, DatabaseReplicationView.Create(page));
                return Results.Empty;
            }

            if (tab == "backups" && http.Request.Headers["Datastar-Request"] == "true" && page.Backups?.Restore is not null)
            {
                await DatastarEvents.PatchElementsAsync(http, TerraScale.Dashboard.Features.Backups.RestoreStatusView.Create(page.Backups));
                return Results.Empty;
            }

            var result = DatabaseDetailView.Create(page);
            result.StatusCode = status;
            return result;
        }

        internal static bool WantsHtml(HttpContext http)
        {
            return DatabaseResults.WantsHtml(http);
        }

        internal static bool KnownFailure(Exception error)
        {
            return DatabaseResults.KnownFailure(error);
        }

        internal static int Status(Exception error)
        {
            return DatabaseResults.Status(error);
        }

        internal static string Message(Exception error)
        {
            return DatabaseResults.Message(error);
        }

        internal static IResult Failure(Exception error)
        {
            return DatabaseResults.Failure(error);
        }
    }
}
