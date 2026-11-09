using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Analytics
{
    public static class AnalyticsEndpoints
    {
        public static IServiceCollection AddAnalyticsFeatures(this IServiceCollection services)
        {
            return services
            .AddSingleton<IAnalyticsBackend, AnalyticsBackend>().AddSingleton<AnalyticsService>();
        }

        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, AnalyticsService analytics)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            try
            {
                var selection = AnalyticsSelection.For(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(),
                    http.Request.Query["region"].ToString(), http.Request.Query["range"].ToString());
                return AnalyticsView.Create(await analytics.LoadAsync(ShellModel.For(http, selection.Context, "Analytics"), selection, http.RequestAborted));
            }
            catch (AccountAccessException error)
            {
                var view = AnalyticsNotice.Create(new(ShellModel.For(http, context, "Analytics"),
                    error.Message + (error.StatusCode == 403 ? " Ask an owner or admin for access." : "")));
                view.StatusCode = error.StatusCode;
                return view;
            }
        }
    }

    public sealed record AnalyticsNoticeModel(ShellModel Shell, string Message);
}
