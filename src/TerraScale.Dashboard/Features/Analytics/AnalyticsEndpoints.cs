using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
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
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (!AccountRequest.TryResolvePage(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            return AnalyticsSelection.TryFor(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(),
                http.Request.Query["region"].ToString(), http.Request.Query["range"].ToString()) switch
            {
                AnalyticsSelection selection => await PageAsync(http, analytics, selection),
                DashboardNotFound failure => Notice(http, context, failure.Message, 404),
                DashboardForbidden failure => Notice(http, context, failure.Message, 403),
                DashboardValidation failure => Notice(http, context, failure.Message, 400),
            };
        }

        private static RazorSlices.RazorSlice<AnalyticsNoticeModel> Notice(HttpContext http, AccountContext context, string message, int status)
        {
            var view = AnalyticsNotice.Create(new(ShellModel.For(http, context, "Analytics"),
                message + (status == 403 ? " Ask an owner or admin for access." : "")));
            view.StatusCode = status;
            return view;
        }

        private static async Task<IResult> PageAsync(HttpContext http, AnalyticsService analytics, AnalyticsSelection selection)
        {
            var page = await analytics.LoadAsync(ShellModel.For(http, selection.Context, "Analytics"), selection, http.RequestAborted);
            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, AnalyticsPageView.Create(page));
                return TypedResults.Empty;
            }

            return AnalyticsView.Create(page);
        }
    }

    public sealed record AnalyticsNoticeModel(ShellModel Shell, string Message);
}
