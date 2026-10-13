using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Analytics;

public static class AnalyticsEndpoints
{
    public static IServiceCollection AddAnalyticsFeatures(this IServiceCollection services) => services
        .AddSingleton<IAnalyticsBackend, AnalyticsBackend>().AddSingleton<AnalyticsService>();

    public static IEndpointRouteBuilder MapAnalytics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/analytics", async (HttpContext http, DashboardSessions sessions, AccountService accounts, AnalyticsService analytics) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountEndpoints.LoginRedirect(http);
            try
            {
                var selection = AnalyticsSelection.For(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(),
                    http.Request.Query["region"].ToString(), http.Request.Query["range"].ToString());
                return AnalyticsView.Create(await analytics.LoadAsync(ShellModel.For(http, context, "Analytics"), selection, http.RequestAborted));
            }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
        });
        return endpoints;
    }
}
