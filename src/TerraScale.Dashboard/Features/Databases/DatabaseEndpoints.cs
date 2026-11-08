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
            endpoints.MapGet("/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases, TerraScale.Dashboard.Features.Backups.BackupService backups) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                context = await backups.SettleRestoresAsync(context, http.RequestAborted);
                return await ListPageAsync(http, context, backend, databases);
            });
            endpoints.MapGet("/api/v1/dashboard/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases) =>
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
            });
            foreach (var tab in new[] { "overview", "tables", "grants", "replication", "conflicts", "backups", "branches", "danger" })
            {
                var section = tab;
                endpoints.MapGet(tab == "overview" ? "/databases/{databaseId}" : "/databases/{databaseId}/" + tab,
                    async (HttpContext http, string databaseId, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) =>
                    {
                        var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                        if (context is null)
                        {
                            return AccountEndpoints.LoginRedirect(http);
                        }
                        // The delete dialog moved to the danger tab; keep old overview links working.
                        if (section == "overview" && http.Request.Query["delete"] == "1")
                        {
                            return HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "danger") + "?delete=1");
                        }
                        // Refresh placement before routing the observations call to its home.
                        if (section == "replication")
                        {
                            await databases.SyncAsync(context, http.RequestAborted);
                        }

                        return await DetailPageAsync(http, context, databaseId, section, backend, databases);
                    });
            }
            endpoints.MapPost("/api/v1/dashboard/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) =>
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

                try
                {
                    var home = form.ContainsKey("homeRegion") ? form["homeRegion"].ToString().Trim() : context.Project?.Region ?? "";
                    if (home.Length == 0)
                    {
                        throw new AccountAccessException("Choose a home region.", 422);
                    }

                    var database = await databases.CreateAsync(context, form["name"].ToString(), home, http.RequestAborted, DatabaseTtl.Hours(form["ttlHours"].ToString()),
                        [.. form["replicaRegions"].OfType<string>()], form["allRegions"].ToString() is "on" or "true");
                    return WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(database.Id) + "?created=1") : Results.Json(DatabaseOutput(database), statusCode: 201);
                }
                catch (Exception error) when (KnownFailure(error))
                {
                    return WantsHtml(http) ? await ListPageAsync(http, context, backend, databases, error: Message(error), openCreate: true, name: form["name"].ToString(), status: Status(error), homeRegion: form.ContainsKey("homeRegion") ? form["homeRegion"].ToString() : null, ttlHours: form["ttlHours"].ToString()) : Failure(error);
                }
            });
            foreach (var operation in new[] { "branch", "delete" })
            {
                var action = operation;
                endpoints.MapPost("/api/v1/dashboard/databases/" + operation,
                    async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) =>
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

                        var id = form["databaseId"].ToString();
                        try
                        {
                            if (action == "branch")
                            {
                                var branch = await databases.BranchAsync(context, id, form["name"].ToString(), http.RequestAborted, DatabaseTtl.Hours(form["ttlHours"].ToString()), form["pointInTime"].ToString());
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
                    });
            }
            endpoints.MapDatabaseSettings();
            endpoints.MapDatabaseGrants();
            endpoints.MapDatabasePlacement();
            endpoints.MapDataFeatures();
            endpoints.MapDatabaseLogs();
            endpoints.MapDatabaseFunctions();
            endpoints.MapApiKeyFeatures();
            endpoints.MapBackupFeatures((http, context, databaseId, error, status) => DetailPageAsync(http, context, databaseId, "backups",
                http.RequestServices.GetRequiredService<IDatabaseBackend>(), http.RequestServices.GetRequiredService<DatabaseService>(), error, status: status));
            return endpoints;
        }

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
            string error = "", bool openCreate = false, string name = "", int status = 200, string? homeRegion = null, string ttlHours = "")
        {
            await databases.SyncAsync(context, http.RequestAborted);
            // Regions and the project metrics are independent reads; they run together.
            var range = HomePage.NormalizeRange(http.Request.Query["range"].ToString());
            var metricsRead = DatabaseListMetrics.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, range, http.RequestAborted);
            var regionsRead = backend.RegionsAsync(http.RequestAborted);
            IReadOnlyList<DatabaseRegion> choices = [];
            var regionsError = "";
            try { choices = await regionsRead; }
            catch (HttpRequestException) { regionsError = "Region discovery is unavailable. Try again shortly."; }
            var search = http.Request.Query["q"].ToString().Trim();
            var all = context.Project?.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).ToArray() ?? [];
            var filtered = all.Where(database => search.Length == 0 || database.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            var metrics = await metricsRead;
            var result = DatabasesView.Create(new(ShellModel.For(http, context, "Databases"), filtered, choices, search, error, openCreate || http.Request.Query["create"] == "1", name, regionsError) { Metrics = metrics, Range = range, HomeSelection = homeRegion, TtlHours = ttlHours });
            result.StatusCode = status;
            return result;
        }

        internal static async Task<IResult> DetailPageAsync(HttpContext http, AccountContext context, string id, string tab, IDatabaseBackend backend, DatabaseService databases, string error = "", bool openDelete = false, int status = 200, string? branchName = null, string ttlHours = "", string pointInTime = "", DatabaseGrantForm? grantForm = null)
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
            var activityRead = tab == "overview"
                ? DatabaseActivity.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, database.Id,
                    key => http.Request.Query[key].ToString() is { Length: > 0 } selected ? HomePage.NormalizeRange(selected) : range, http.RequestAborted) : null;
            // Storage is the metered SQL storage gauge from the latest analytics bucket.
            var storageRead = tab == "overview" ? DatabaseListMetrics.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, "24h", http.RequestAborted) : null;
            var backupsRead = tab is "backups" or "overview" ? BackupEndpoints.PageAsync(http, context, database, error, tab == "backups") : null;
            async Task<(DatabaseTables Tables, string Table, DatabaseSqlResult? Preview)> TablesAsync()
            {
                var scope = DatabaseService.Scope(context);
                var home = await databases.HomeAsync(context, database, http.RequestAborted);
                var tables = await DatabaseTables.LoadAsync(backend, scope, home, database.Id, http.RequestAborted);
                var shown = tables.Tables.FirstOrDefault(table => table.Name == selectedTable) ?? (selectedTable.Length == 0 ? tables.Tables.FirstOrDefault() : null);
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
                    return new(null, table, since, until, before, failure is AccountAccessException { StatusCode: 422 } ? failure.Message : "Conflicts are unavailable. Try again.");
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
                GrantForm = grantForm
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
