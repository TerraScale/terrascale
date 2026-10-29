using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Features.Accounts;

public static class AccountEndpoints
{
    public static IServiceCollection AddAccountFeatures(this IServiceCollection services) => services.AddSingleton<AccountService>();

    public static IEndpointRouteBuilder MapAccountFeatures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/auth/me", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            // Who is signed in stays readable while the selected organization requires two-factor.
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null) return Results.Unauthorized();
            return Results.Json(new AccountIdentityResponse(context.UserId, context.Account.Id,
                context.Account.Name, context.Account.Email, context.Account.Phone, context.Account.Locale,
                context.Account.OnboardingCompleted, context.Organization?.Id, context.Project?.Id, context.Role),
                AccountContractJson.Default.AccountIdentityResponse);
        }).WithGroupName("integration").WithName("getDashboardIdentity").WithTags("Session")
            .WithSummary("Read the current dashboard session and workspace")
            .Produces<AccountIdentityResponse>().Produces(StatusCodes.Status401Unauthorized);
        endpoints.MapPost("/api/v1/dashboard/switch-organization", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var session = await sessions.ReadAsync(http);
            if (session is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            AccountContext selected;
            try { selected = await accounts.SwitchOrganizationAsync(session, form["organizationId"].ToString(), http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther(SwitchReturnPath(form["returnUrl"].ToString(), selected));
        }).WithGroupName("integration").WithName("switchDashboardOrganization").WithTags("Workspace")
            .WithSummary("Select an organization the current user can access")
            .Accepts<SwitchOrganizationRequest>("application/x-www-form-urlencoded")
            .Produces(StatusCodes.Status303SeeOther).Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
        endpoints.MapPost("/api/v1/dashboard/switch-project", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var session = await sessions.ReadAsync(http);
            if (session is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            AccountContext selected;
            try { selected = await accounts.SwitchProjectAsync(session, form["projectId"].ToString(), http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther(SwitchReturnPath(form["returnUrl"].ToString(), selected));
        }).WithGroupName("integration").WithName("switchDashboardProject").WithTags("Workspace")
            .WithSummary("Select a project the current user can access")
            .Accepts<SwitchProjectRequest>("application/x-www-form-urlencoded")
            .Produces(StatusCodes.Status303SeeOther).Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
        return endpoints;
    }

    private static string SwitchReturnPath(string? returnUrl, AccountContext selected)
    {
        var path = Csrf.LocalRedirect(returnUrl);
        var route = path.Split('?', '#')[0];
        const string prefix = "/databases/";
        if (!route.StartsWith(prefix, StringComparison.Ordinal)) return WorkspaceUrl.For(selected, path, replace: true);
        var databaseId = Uri.UnescapeDataString(route[prefix.Length..].Split('/')[0]);
        return WorkspaceUrl.For(selected, selected.Project?.Databases.ContainsKey(databaseId) == true ? path : "/databases", replace: true);
    }

    public static async Task<AccountContext?> RequireAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
    {
        return await AccountRequest.RequireAsync(http, sessions, accounts);
    }

    public static IResult LoginRedirect(HttpContext http) => AccountRequest.LoginRedirect(http);
}

public sealed record AccountIdentityResponse(string UserId, string AccountId, string Name, string Email,
    string PhoneNumber, string Locale, bool OnboardingCompleted, string? OrganizationId, string? ProjectId, string Role);

public sealed record SwitchOrganizationRequest(
    [property: JsonPropertyName("_csrf")] string Csrf, string OrganizationId, string? ReturnUrl);

public sealed record SwitchProjectRequest(
    [property: JsonPropertyName("_csrf")] string Csrf, string ProjectId, string? ReturnUrl);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AccountIdentityResponse))]
[JsonSerializable(typeof(SwitchOrganizationRequest))]
[JsonSerializable(typeof(SwitchProjectRequest))]
internal sealed partial class AccountContractJson : JsonSerializerContext;
