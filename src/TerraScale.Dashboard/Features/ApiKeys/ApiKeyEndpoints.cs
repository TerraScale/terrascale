using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.ApiKeys;

public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyFeatures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api-keys", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            return context is null ? AccountRequest.LoginRedirect(http) : await PageAsync(http, context, keys);
        });
        endpoints.MapGet("/api/v1/dashboard/credentials", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            try { return Results.Json((await keys.ListAsync(context, http.RequestAborted)).Select(ApiKeyService.CredentialMetadata).ToArray(), ApiKeyContractJson.Default.ProjectCredentialMetadataArray); }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.Failure(error); }
        }).WithGroupName("integration").WithName("listDashboardCredentials").WithTags("Credentials")
            .WithSummary("List ts-auth project keys without secrets")
            .WithDescription("DatabaseName is the canonical restriction. A missing local DatabaseId does not mean unrestricted access. LastFour and issuer-unavailable metadata remain empty.")
            .Produces<ProjectCredentialMetadata[]>().Produces(401).ProducesProblem(403).ProducesProblem(503);
        endpoints.MapPost("/api/v1/dashboard/credentials/{kind}", async (HttpContext http, string kind, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            try
            {
                var credential = await keys.CreateAsync(context, kind, form["name"].ToString(), form["databaseId"].ToString(), form["scope"].Concat(form["scopes"]).SelectMany(value => (value ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), http.RequestAborted);
                return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, revealed: credential) : Results.Json(new ProjectApiKeySecret(ApiKeyService.Metadata(credential.Key), credential.Secret), ApiKeyContractJson.Default.ProjectApiKeySecret, statusCode: 201);
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                return DatabaseResults.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseResults.Message(error), kind, form["name"].ToString(), status: DatabaseResults.Status(error), draftScopes: form["scopes"].ToString(), draftDatabaseId: form["databaseId"].ToString()) : DatabaseResults.Failure(error);
            }
        }).WithGroupName("integration").WithName("createDashboardCredential").WithTags("Credentials")
            .WithSummary("Create a project key and reveal its secret once")
            .WithDescription("Uses the original user session at ts-auth. Scopes are documents:read/write and databases:read/write (database:read/write are aliases of documents:read/write). Machine-client kind is unavailable. Secrets are never persisted by the dashboard.")
            .Accepts<CreateProjectCredentialRequest>("application/x-www-form-urlencoded")
            .Produces<ProjectApiKeySecret>(201).Produces(200, contentType: "text/html").Produces(401).ProducesProblem(403).ProducesProblem(422).ProducesProblem(503);
        endpoints.MapPost("/api/v1/dashboard/credentials/{keyId}/revoke", async (HttpContext http, string keyId, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            try { await keys.RevokeAsync(context, keyId, http.RequestAborted); }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys") : Results.NoContent();
        }).WithGroupName("integration").WithName("revokeDashboardCredentialById").WithTags("Credentials")
            .WithSummary("Revoke a project key")
            .WithDescription("Deletes the key at ts-auth: token requests then fail with invalid_client. Already-issued tokens are validated offline and stay valid until they expire (15 minutes). A failed revoke call is re-checked, so 204/303 means the key is gone.")
            .Accepts<RevokeProjectCredentialRequest>("application/x-www-form-urlencoded")
            .Produces(204).Produces(303).Produces(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        endpoints.MapPost("/api/v1/dashboard/credentials/revoke", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            try { await keys.RevokeAsync(context, form["keyId"].ToString(), http.RequestAborted); }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys" + http.Request.QueryString) : Results.NoContent();
        }).WithGroupName("integration").WithName("revokeDashboardCredential").WithTags("Credentials")
            .WithSummary("Revoke a key selected by its form ID")
            .Accepts<RevokeProjectCredentialRequest>("application/x-www-form-urlencoded")
            .Produces(204).Produces(303).Produces(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        endpoints.MapPost("/api/v1/dashboard/credentials/rotate", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            if (!context.CanManage) return Results.StatusCode(403);
            const string message = "Key rotation is unavailable in this view. Create a new key and revoke the old one instead. Already issued tokens remain valid until expiry.";
            return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, message, status: 503) : Results.Problem(message, statusCode: 503);
        }).WithGroupName("integration").WithName("rotateDashboardCredential").WithTags("Credentials")
            .WithSummary("Report that key rotation is unavailable in this view")
            .Accepts<RevokeProjectCredentialRequest>("application/x-www-form-urlencoded")
            .Produces(401).Produces(403).Produces(503, contentType: "text/html").ProducesProblem(503);
        return endpoints;
    }

    private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, ApiKeyService service, string error = "", string create = "", string name = "", CreatedProjectCredential? revealed = null, int status = 200, string draftScopes = "", string draftDatabaseId = "")
    {
        IReadOnlyList<ProjectCredential> keys = [];
        try { keys = await service.ListAsync(context, http.RequestAborted); }
        catch (HttpRequestException) { if (error.Length == 0) error = "The live credential issuer is unavailable. Try again shortly."; }
        var result = ApiKeysView.Create(new(ShellModel.For(http, context, "API keys"), keys, http.Request.Query["kind"].ToString(), http.Request.Query["q"].ToString(), http.Request.Query["databaseId"].ToString(), error, create.Length > 0 ? create : http.Request.Query["create"].ToString(), name, revealed, http.Request.Query["status"].ToString(), draftScopes, draftDatabaseId));
        result.StatusCode = status;
        return result;
    }
}
