
namespace TerraScale.Dashboard.Features.Accounts;

public static class AccountEndpoints
{
    public static IServiceCollection AddAccountFeatures(this IServiceCollection services) => services.AddSingleton<IWorkspaceGateway, TsWorkspaceGateway>().AddSingleton<AccountService>();

    public static IEndpointRouteBuilder MapAccountFeatures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/auth/me", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            return Results.Json(new
            {
                userId = context.UserId,
                accountId = context.Account.Id,
                name = context.Account.Name,
                email = context.Account.Email,
                phoneNumber = context.Account.Phone,
                locale = context.Account.Locale,
                onboardingCompleted = context.Account.OnboardingCompleted,
                organizationId = context.Organization?.Id,
                projectId = context.Project?.Id,
                role = context.Role
            });
        });
        endpoints.MapPost("/api/v1/dashboard/switch-organization", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var session = await sessions.ReadAsync(http);
            if (session is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            try { await accounts.SwitchOrganizationAsync(session, form["organizationId"].ToString(), http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther(Csrf.LocalRedirect(form["returnUrl"]));
        });
        endpoints.MapPost("/api/v1/dashboard/switch-project", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var session = await sessions.ReadAsync(http);
            if (session is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            try { await accounts.SwitchProjectAsync(session, form["projectId"].ToString(), http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther(Csrf.LocalRedirect(form["returnUrl"]));
        });
        return endpoints;
    }
}
