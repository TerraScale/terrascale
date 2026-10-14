using RazorSlices;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.UI;
using TerraScale.DotNet.TerraBase;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Databases;

public static class DatabaseEndpoints
{
    public static IServiceCollection AddDatabaseFeatures(this IServiceCollection services) => services
        .AddSingleton<IDatabaseBackend, DatabaseBackend>().AddSingleton<DatabaseService>()
        .AddSingleton<DocumentBackend>().AddSingleton<IDocumentBackend>(services => services.GetRequiredService<DocumentBackend>())
        .AddSingleton<DataService>().AddSingleton<SavedQueryService>().AddSingleton<DocumentChangesService>()
        .AddSingleton<ApiKeyService>().AddSingleton<IWorkspacePlacement, WorkspacePlacement>()
        .AddSingleton<IOnboardingProjectProvisioner, OnboardingProjectProvisioner>()
        .AddSingleton<IBackupBackend, BackupBackend>().AddSingleton<BackupService>();

    public static IEndpointRouteBuilder MapDatabaseFeatures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            return context is null ? AccountEndpoints.LoginRedirect(http) : await ListPageAsync(http, context, backend, databases);
        });
        endpoints.MapGet("/api/v1/dashboard/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            return context is null ? Results.Unauthorized() : Results.Json(context.Project?.Databases.Values.OrderBy(database => database.Name).ToArray() ?? []);
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
                    if (section == "overview" && http.Request.Query["delete"] == "1") return AuthEndpoints.SeeOther(DatabaseService.Path(databaseId, "danger") + "?delete=1");
                    return await DetailPageAsync(http, context, databaseId, section, backend, databases);
                });
        }
        endpoints.MapPost("/api/v1/dashboard/databases", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            try
            {
                var database = await databases.CreateAsync(context, form["name"].ToString(), form["region"].Select(value => value ?? ""), http.RequestAborted);
                return WantsHtml(http) ? AuthEndpoints.SeeOther(DatabaseService.Path(database.Id) + "?created=1") : Results.Json(database, statusCode: 201);
            }
            catch (Exception error) when (KnownFailure(error))
            {
                return WantsHtml(http) ? await ListPageAsync(http, context, backend, databases, error: Message(error), openCreate: true, name: form["name"].ToString(), regions: form["region"].Select(value => value ?? "").ToArray(), status: Status(error)) : Failure(error);
            }
        });
        foreach (var operation in new[] { "branch", "regions", "delete" })
        {
            var action = operation;
            endpoints.MapPost("/api/v1/dashboard/databases/" + operation,
                async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) =>
                {
                    var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
                    if (context is null) return Results.Unauthorized();
                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                    var id = form["databaseId"].ToString();
                    try
                    {
                        if (action == "branch")
                        {
                            var branch = await databases.BranchAsync(context, id, form["name"].ToString(), http.RequestAborted);
                            return WantsHtml(http) ? AuthEndpoints.SeeOther(DatabaseService.Path(branch.Id)) : Results.Json(branch, statusCode: 201);
                        }
                        if (action == "regions")
                            await databases.SetRegionsAsync(context, id, form["region"].Select(value => value ?? ""), http.RequestAborted);
                        else
                            await databases.DeleteAsync(context, id, form.ContainsKey("confirmName") ? form["confirmName"].ToString() : form["confirm"].ToString(), http.RequestAborted);
                        return WantsHtml(http) ? AuthEndpoints.SeeOther(action == "delete" ? "/databases" : DatabaseService.Path(id) + "#placement") : Results.NoContent();
                    }
                    catch (Exception error) when (KnownFailure(error))
                    {
                        if (!WantsHtml(http) || Status(error) == 404) return Failure(error);
                        return await DetailPageAsync(http, context, id, action switch { "branch" => "branches", "delete" => "danger", _ => "overview" }, backend, databases, Message(error), action == "delete", Status(error));
                    }
                });
        }
        endpoints.MapDataFeatures();
        endpoints.MapApiKeyFeatures();
        endpoints.MapBackupFeatures();
        return endpoints;
    }

    internal static async Task<IResult> ListPageAsync(HttpContext http, AccountContext context, IDatabaseBackend backend, DatabaseService databases,
        string error = "", bool openCreate = false, string name = "", IReadOnlyList<string>? regions = null, int status = 200)
    {
        IReadOnlyList<DatabaseRegion> choices = [];
        var regionsError = "";
        try { choices = await backend.RegionsAsync(http.RequestAborted); }
        catch (HttpRequestException) { regionsError = "Region discovery is unavailable. Try again shortly."; }
        var contents = new List<CollectionStats>();
        var contentsAvailable = true;
        if (context.Project is not null)
        {
            foreach (var database in context.Project.Databases.Values)
            {
                try
                {
                    var reported = await backend.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, http.RequestAborted), database.Id, http.RequestAborted);
                    DatabaseService.ValidateContents(reported);
                    contents.AddRange(reported);
                    // Collection names can repeat across databases; validate only the combined totals.
                    _ = contents.Sum(item => item.Documents);
                    _ = contents.Sum(item => item.Bytes);
                }
                catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested &&
                    (failure is HttpRequestException or IOException or System.Text.Json.JsonException or OverflowException || KnownFailure(failure)))
                {
                    contentsAvailable = false;
                    if (error.Length == 0) error = "Database counts are unavailable. The data plane did not answer.";
                }
            }
        }
        var search = http.Request.Query["q"].ToString().Trim();
        var all = context.Project?.Databases.Values.OrderBy(database => database.Name, StringComparer.Ordinal).ToArray() ?? [];
        var filtered = all.Where(database => search.Length == 0 || database.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = DatabasesView.Create(new(ShellModel.For(http, context, "Databases"), filtered, choices, contents, search, error, openCreate || http.Request.Query["create"] == "1", name, regions, regionsError) { ContentsAvailable = contentsAvailable });
        result.StatusCode = status;
        return result;
    }

    internal static async Task<IResult> DetailPageAsync(HttpContext http, AccountContext context, string id, string tab, IDatabaseBackend backend, DatabaseService databases, string error = "", bool openDelete = false, int status = 200)
    {
        DatabaseRecord database;
        try { database = DatabaseService.Database(context, id); }
        catch (AccountAccessException failure) { return Failure(failure); }
        IReadOnlyList<DatabaseRegion> choices = [];
        IReadOnlyList<CollectionStats> contents = [];
        var contentsAvailable = false;
        var regionError = "";
        var range = HomePage.NormalizeRange(http.Request.Query["range"]);
        UsageReplication? measurements = null;
        if (tab == "overview")
        {
            try
            {
                choices = await backend.RegionsAsync(http.RequestAborted);
                // The database's own regions stay listed, so saving never drops a replica the catalog does not name.
                var listed = choices;
                choices = [.. listed, .. DatabaseService.Regions(database).Select(code => DatabaseRegion.Resolve(listed, code)).OfType<DatabaseRegion>().Where(region => !listed.Contains(region)).Distinct()];
            }
            catch (HttpRequestException) { regionError = "Region discovery is unavailable. Try again shortly."; }
            try
            {
                contents = await backend.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, http.RequestAborted), id, http.RequestAborted);
                DatabaseService.ValidateContents(contents);
                contentsAvailable = true;
            }
            catch (Exception failure) when (failure is HttpRequestException or System.Text.Json.JsonException or OverflowException || KnownFailure(failure))
            { if (error.Length == 0) error = "Database counts are unavailable. The data plane did not answer."; }
        }
        if (tab == "replication")
            measurements = await http.RequestServices.GetRequiredService<IUsageReplication>().ReadAsync(new(context, range, database.Id, true), http.RequestAborted);
        var backups = tab is "backups" or "overview" ? await BackupEndpoints.PageAsync(http, context, database, error) : null;
        var result = DatabaseDetailView.Create(new(ShellModel.For(http, context, database.Name + " · " + DatabaseFrameModel.Title(tab)), database, tab, choices, contents, error, regionError, openDelete || http.Request.Query["delete"] == "1", http.Request.Query["created"] == "1", backups, contentsAvailable, measurements, range));
        result.StatusCode = status;
        return result;
    }

    internal static bool WantsHtml(HttpContext http) => http.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType.Value == "text/html") == true;
    internal static bool KnownFailure(Exception error) => error is AccountAccessException or TerraScaleAuthException or TerraBaseValidationException or TerraBaseAuthenticationException or TerraBaseForbiddenException or TerraBaseNotFoundException or TerraBaseConflictException or TerraBasePreconditionFailedException or TerraBaseUnavailableException or DatabaseProviderException;
    internal static int Status(Exception error) => error switch { AccountAccessException access => access.StatusCode, DatabaseWrongRegionException => 409, HttpRequestException { StatusCode: { } status } when (int)status is >= 400 and <= 599 => (int)status, _ => 503 };
    internal static string Message(Exception error) => error is AccountAccessException or DatabaseWrongRegionException or DocumentWriteOutcomeUnknownException ? error.Message : Status(error) switch { 401 => "The data plane requires valid authentication.", 404 => "The requested resource is unavailable.", 403 => "Organization access is required.", 409 => "The request conflicts with the current database state.", 412 => "The document changed. Reload it before saving again.", 400 or 422 => "The data plane refused the submitted input.", _ => "The data plane could not complete the request. Try again shortly." };
    internal static IResult Failure(Exception error) => Results.Problem(Message(error), statusCode: Status(error),
        extensions: error is DocumentWriteOutcomeUnknownException unknown ? new Dictionary<string, object?>
        {
            ["operationId"] = unknown.OperationId,
            ["documentId"] = unknown.DocumentId,
            ["outcome"] = "unknown"
        } : null);
}
