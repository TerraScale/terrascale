using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Usage;

public static class UsageEndpoints
{
    public static IServiceCollection AddUsageFeatures(this IServiceCollection services) => services.AddSingleton<SpendingService>()
        .AddSingleton<IUsageMeasurements, UsageBackend>().AddSingleton<IUsageStorage, UsageStorageBackend>().AddSingleton<IUsageReplication, UsageReplicationBackend>().AddSingleton<UsageService>();
    public static IEndpointRouteBuilder MapUsage(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/usage", async (HttpContext http, DashboardSessions sessions, AccountService accounts, UsageService usage) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountEndpoints.LoginRedirect(http);
            if (http.Request.Query["tab"] == "billing") return AuthEndpoints.SeeOther("/organization" + http.Request.QueryString);
            try
            {
                var selection = UsageSelection.For(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(), http.Request.Query["range"].ToString());
                var tab = http.Request.Query["tab"] == "settings" ? "settings" : "overview";
                if (selection.Context.Project is null && tab == "overview") return AuthEndpoints.SeeOther("/");
                return UsageView.Create(await usage.LoadAsync(ShellModel.For(http, context, "Usage"), selection, tab, http.Request.Query["saved"].ToString(), http.RequestAborted));
            }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            catch (IOException) { return Results.Text("Could not load usage.", statusCode: 503); }
        });
        endpoints.MapPost("/api/v1/dashboard/usage/spending-controls", async (HttpContext http, DashboardSessions sessions, AccountService accounts, SpendingService spending) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
            if (!context.CanManage) return Results.Text("Organization management is required.", statusCode: 403);
            if (!SpendingForm.TryParse(form, out var change)) return AuthEndpoints.SeeOther("/usage?tab=settings&saved=invalid");
            try { await spending.SetAsync(context, change, http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            return AuthEndpoints.SeeOther("/usage?tab=settings&saved=ok");
        });
        return endpoints;
    }
}
