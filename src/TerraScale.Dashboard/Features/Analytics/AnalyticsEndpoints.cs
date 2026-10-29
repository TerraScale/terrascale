using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Analytics;

public static class AnalyticsEndpoints
{
    public static IServiceCollection AddAnalyticsFeatures(this IServiceCollection services) => services
        .AddSingleton<IAnalyticsBackend, AnalyticsBackend>().AddSingleton<AnalyticsService>();

    public static IEndpointRouteBuilder MapAnalytics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/analytics", async Task<IResult> (HttpContext http, DashboardSessions sessions, AccountService accounts, AnalyticsService analytics) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            try
            {
                var selection = AnalyticsSelection.For(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(),
                    http.Request.Query["region"].ToString(), http.Request.Query["range"].ToString());
                return AnalyticsView.Create(await analytics.LoadAsync(ShellModel.For(http, context, "Analytics"), selection, http.RequestAborted));
            }
            catch (AccountAccessException error)
            {
                var view = AnalyticsNotice.Create(new(ShellModel.For(http, context, "Analytics"),
                    error.Message + (error.StatusCode == 403 ? " Ask an owner or admin for access." : "")));
                view.StatusCode = error.StatusCode;
                return view;
            }
        });
        return endpoints;
    }
}

public sealed record AnalyticsNoticeModel(ShellModel Shell, string Message);
