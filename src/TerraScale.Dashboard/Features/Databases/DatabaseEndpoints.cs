using RazorSlices;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Functions;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet.TerraBase;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Databases;

public static class DatabaseEndpoints
{
    public static IServiceCollection AddDatabaseFeatures(this IServiceCollection services) => services
        .AddSingleton<IDatabaseBackend, DatabaseBackend>().AddSingleton<DatabaseService>()
        .AddSingleton<DocumentBackend>().AddSingleton<IDocumentBackend>(services => services.GetRequiredService<DocumentBackend>())
        .AddSingleton<DataService>().AddSingleton<SchemaPreview>().AddSingleton<SavedQueryService>().AddSingleton<DocumentChangesService>()
        .AddSingleton<TerraScale.Dashboard.Features.Webhooks.WebhookService>()
        .AddSingleton<ApiKeyService>().AddSingleton<IWorkspacePlacement, WorkspacePlacement>()
        .AddSingleton<IOnboardingProjectProvisioner, OnboardingProjectProvisioner>()
        .AddSingleton<IBackupBackend, BackupBackend>().AddSingleton<BackupService>().AddDatabaseLogs().AddDatabaseFunctions();

    public static IEndpointRouteBuilder MapDatabaseFeatures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            return context is null ? AccountRequest.LoginRedirect(http) : await ListPageAsync(http, context, backend, databases);
        });
        endpoints.MapGet("/api/v1/dashboard/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            await databases.SyncAsync(context, http.RequestAborted);
            return Results.Json(context.Project?.Databases.Values.OrderBy(database => database.Name).ToArray() ?? []);
        });
        foreach (var tab in new[] { "overview", "replication", "backups", "branches", "import", "danger" })
        {
            var section = tab;
            endpoints.MapGet(tab == "overview" ? "/databases/{databaseId}" : "/databases/{databaseId}/" + tab,
                async (HttpContext http, string databaseId, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) =>
                {
                    var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                    if (context is null) return AccountEndpoints.LoginRedirect(http);
                    // The delete dialog moved to the danger tab; keep old overview links working.
                    if (section == "overview" && http.Request.Query["delete"] == "1") return HttpRedirects.SeeOther(DatabaseService.Path(databaseId, "danger") + "?delete=1");
                    return await DetailPageAsync(http, context, databaseId, section, backend, databases);
                });
        }
        endpoints.MapPost("/api/v1/dashboard/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            try
            {
                var regions = form["region"].Select(value => value ?? "").ToArray();
                if (form.ContainsKey("homeRegion"))
                {
                    var home = form["homeRegion"].ToString().Trim();
                    if (home.Length == 0) throw new AccountAccessException("Choose a home region.", 422);
                    if (form["allRegions"] != "true") DatabaseService.RequireReplicas(regions);
                    var index = Array.FindIndex(regions, region => region.Equals(home, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 && form["allRegions"] != "true") throw new AccountAccessException(DatabaseService.HomePlacementMessage, 422);
                    regions = index < 0 ? [home, .. regions] : [home, .. regions[..index], .. regions[(index + 1)..]];
                }
                var database = await databases.CreateAsync(context, form["name"].ToString(), regions, form["allRegions"] == "true", http.RequestAborted);
                return WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(database.Id) + "?created=1") : Results.Json(database, statusCode: 201);
            }
            catch (Exception error) when (KnownFailure(error))
            {
                return WantsHtml(http) ? await ListPageAsync(http, context, backend, databases, error: Message(error), openCreate: true, name: form["name"].ToString(), regions: form["region"].Select(value => value ?? "").ToArray(), status: Status(error), homeRegion: form.ContainsKey("homeRegion") ? form["homeRegion"].ToString() : null, allRegions: form["allRegions"] == "true") : Failure(error);
            }
        });
        foreach (var operation in new[] { "branch", "regions", "delete" })
        {
            var action = operation;
            endpoints.MapPost("/api/v1/dashboard/databases/" + operation,
                async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) =>
                {
                    var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                    if (context is null) return Results.Unauthorized();
                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                    var id = form["databaseId"].ToString();
                    try
                    {
                        if (action == "branch")
                        {
                            var branch = await databases.BranchAsync(context, id, form["name"].ToString(), http.RequestAborted);
                            return WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(branch.Id)) : Results.Json(branch, statusCode: 201);
                        }
                        if (action == "regions")
                            await databases.SetRegionsAsync(context, id, form["region"].Select(value => value ?? ""), form["allRegions"] == "true", http.RequestAborted);
                        else
                            await databases.DeleteAsync(context, id, form.ContainsKey("confirmName") ? form["confirmName"].ToString() : form["confirm"].ToString(), http.RequestAborted);
                        return WantsHtml(http) ? HttpRedirects.SeeOther(action == "delete" ? "/databases" : DatabaseService.Path(id, "replication") + "#placement") : Results.NoContent();
                    }
                    catch (Exception error) when (KnownFailure(error))
                    {
                        if (!WantsHtml(http) || Status(error) == 404) return DatabaseResults.PageFailure(http, context, error);
                        return await DetailPageAsync(http, context, id, action switch { "branch" => "branches", "delete" => "danger", _ => "replication" }, backend, databases, Message(error), action == "delete", Status(error));
                    }
                });
        }
        endpoints.MapDataFeatures();
        endpoints.MapDatabaseLogs();
        endpoints.MapDatabaseFunctions();
        endpoints.MapApiKeyFeatures();
        endpoints.MapBackupFeatures((http, context, databaseId, error, status) => DetailPageAsync(http, context, databaseId, "backups",
            http.RequestServices.GetRequiredService<IDatabaseBackend>(), http.RequestServices.GetRequiredService<DatabaseService>(), error, status: status));
        return endpoints;
    }

    internal static async Task<IResult> ListPageAsync(HttpContext http, AccountContext context, IDatabaseBackend backend, DatabaseService databases,
        string error = "", bool openCreate = false, string name = "", IReadOnlyList<string>? regions = null, int status = 200, string? homeRegion = null, bool allRegions = false)
    {
        await databases.SyncAsync(context, http.RequestAborted);
        // Regions, each database's collection counts and the project metrics are independent reads; they run together.
        var range = HomePage.NormalizeRange(http.Request.Query["range"].ToString());
        var metricsRead = DatabaseListMetrics.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, range, http.RequestAborted);
        var regionsRead = backend.RegionsAsync(http.RequestAborted);
        IReadOnlyList<DatabaseRegion> choices = [];
        var regionsError = "";
        var contents = new List<CollectionStats>();
        var contentsAvailable = true;
        if (context.Project is not null)
        {
            // Explore serves no collection counts.
            if (PlatformDatabases.IsPlatform(context.Organization?.Id)) contentsAvailable = false;
            else
            {
                var reads = await Task.WhenAll(context.Project.Databases.Values.Select(async database =>
                {
                    try
                    {
                        var reported = await backend.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, http.RequestAborted), database.Id, http.RequestAborted);
                        DatabaseService.ValidateContents(reported);
                        return reported;
                    }
                    catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested &&
                        (failure is HttpRequestException or IOException or System.Text.Json.JsonException or OverflowException || KnownFailure(failure)))
                    { return null; }
                }));
                try
                {
                    foreach (var reported in reads.OfType<IReadOnlyList<CollectionStats>>())
                    {
                        contents.AddRange(reported);
                        // Collection names can repeat across databases; validate only the combined totals.
                        _ = contents.Sum(item => item.Documents);
                        _ = contents.Sum(item => item.Bytes);
                    }
                }
                catch (OverflowException) { reads = [null]; }
                if (reads.Any(reported => reported is null))
                {
                    contentsAvailable = false;
                    if (error.Length == 0) error = "Database counts are unavailable. The data plane did not answer.";
                }
            }
        }
        try { choices = await regionsRead; }
        catch (HttpRequestException) { regionsError = "Region discovery is unavailable. Try again shortly."; }
        var search = http.Request.Query["q"].ToString().Trim();
        var all = context.Project?.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).ToArray() ?? [];
        var filtered = all.Where(database => search.Length == 0 || database.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var metrics = await metricsRead;
        var result = DatabasesView.Create(new(ShellModel.For(http, context, "Databases"), filtered, choices, contents, search, error, openCreate || http.Request.Query["create"] == "1", name, regions, regionsError) { ContentsAvailable = contentsAvailable, Metrics = metrics, Range = range, HomeSelection = homeRegion, AllRegions = allRegions });
        result.StatusCode = status;
        return result;
    }

    internal static async Task<IResult> DetailPageAsync(HttpContext http, AccountContext context, string id, string tab, IDatabaseBackend backend, DatabaseService databases, string error = "", bool openDelete = false, int status = 200)
    {
        DatabaseRecord database;
        try
        {
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                (context, database) = await databases.LookupAsync(context, id, http.RequestAborted);
            else database = DatabaseService.Database(context, id);
        }
        catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested &&
            (KnownFailure(failure) || failure is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException))
        { return DatabaseResults.PageFailure(http, context, failure); }
        IReadOnlyList<DatabaseRegion> choices = [];
        IReadOnlyList<CollectionStats> contents = [];
        var contentsAvailable = false;
        var regionError = "";
        var range = HomePage.NormalizeRange(http.Request.Query["range"]);
        // Each tab's reads are independent of one another, so they all start before any is awaited.
        var measurementsRead = tab == "replication"
            ? http.RequestServices.GetRequiredService<IUsageReplication>().ReadAsync(new(context, range, database.Id, true), http.RequestAborted) : null;
        var activityRead = tab is "overview" or "replication"
            ? DatabaseActivity.LoadAsync(http.RequestServices.GetRequiredService<IAnalyticsBackend>(), context, database.Id,
                key => tab == "overview" && http.Request.Query[key].ToString() is { Length: > 0 } selected ? HomePage.NormalizeRange(selected) : range, http.RequestAborted) : null;
        var backupsRead = tab is "backups" or "overview" ? BackupEndpoints.PageAsync(http, context, database, error, tab == "backups") : null;
        var choicesRead = tab == "replication" ? backend.RegionsAsync(http.RequestAborted) : null;
        async Task<IReadOnlyList<CollectionStats>> ContentsAsync() =>
            await backend.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, http.RequestAborted), id, http.RequestAborted);
        var contentsRead = tab == "overview" ? ContentsAsync() : null;
        if (choicesRead is not null)
        {
            try
            {
                choices = await choicesRead;
                // The database's own regions stay listed, so saving never drops a replica the catalog does not name.
                var listed = choices;
                choices = [.. listed, .. DatabaseService.Regions(database).Select(code => DatabaseRegion.Resolve(listed, code)).OfType<DatabaseRegion>().Where(region => !listed.Contains(region)).Distinct()];
            }
            catch (HttpRequestException) { regionError = "Region discovery is unavailable. Try again shortly."; }
        }
        if (contentsRead is not null)
        {
            // An all-regions database serves every catalog region; without the catalog it shows its recorded regions.
            if (database.AllRegions)
                try { choices = await backend.RegionsAsync(http.RequestAborted); }
                catch (HttpRequestException) { }
            try
            {
                contents = await contentsRead;
                DatabaseService.ValidateContents(contents);
                contentsAvailable = true;
            }
            catch (Exception failure) when (failure is HttpRequestException or System.Text.Json.JsonException or OverflowException || KnownFailure(failure))
            { if (error.Length == 0) error = "Database counts are unavailable. The data plane did not answer."; }
        }
        UsageReplication? measurements = measurementsRead is null ? null : await measurementsRead;
        var activity = activityRead is null ? null : await activityRead;
        // The backups section shows the same page error, which the counts read above may have just set.
        var backups = backupsRead is null ? null : (await backupsRead) with { Error = error };
        var result = DatabaseDetailView.Create(new(ShellModel.For(http, context, database.Name + " · " + DatabaseFrameModel.Title(tab)), database, tab, choices, contents, error, regionError, openDelete || http.Request.Query["delete"] == "1", http.Request.Query["created"] == "1", backups, contentsAvailable, measurements, range, activity));
        result.StatusCode = status;
        return result;
    }

    internal static bool WantsHtml(HttpContext http) => DatabaseResults.WantsHtml(http);
    internal static bool KnownFailure(Exception error) => DatabaseResults.KnownFailure(error);
    internal static int Status(Exception error) => DatabaseResults.Status(error);
    internal static string Message(Exception error) => DatabaseResults.Message(error);
    internal static IResult Failure(Exception error) => DatabaseResults.Failure(error);
}
