using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Profile;

public static class AccessTokenEndpoints
{
    public static IEndpointRouteBuilder MapProfileAccessTokens(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/dashboard/profile/access-tokens", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IProfileTokens tokens, IAuthAttemptBudget budgets) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            try
            {
                if (!int.TryParse(form["expiresInDays"], out var days)) throw new AccountAccessException("Choose a valid token expiration.", 422);
                await budgets.ReserveAsync(http, context.Account.Email, AuthBudgetKind.SignIn, http.RequestAborted);
                var created = await tokens.CreateAsync(context, form["name"].ToString(), days, form["password"].ToString(), EnrollmentEndpoints.ClientIp(http), http.RequestAborted);
                await budgets.ForgiveAsync(http, context.Account.Email, http.RequestAborted);
                return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "access-tokens", Tokens: await tokens.ListAsync(context, http.RequestAborted), CreatedToken: created, Sessions: await sessions.ListAsync(http, context.Session)));
            }
            catch (AuthRequestRejectedException error)
            {
                if (error.RetryAfter > 0) http.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Problem(error.Message, statusCode: error.StatusCode);
            }
            catch (AccountAccessException error)
            {
                var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "access-tokens", Error: error.Message, Tokens: await tokens.ListAsync(context, http.RequestAborted), Sessions: await sessions.ListAsync(http, context.Session)));
                view.StatusCode = error.StatusCode;
                return view;
            }
        });
        endpoints.MapPost("/api/v1/dashboard/profile/access-tokens/{id}/revoke", async (HttpContext http, string id, DashboardSessions sessions, AccountService accounts, IProfileTokens tokens) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            try { await tokens.RevokeAsync(context, id, http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther("/profile?tab=access-tokens");
        });
        return endpoints;
    }
}
