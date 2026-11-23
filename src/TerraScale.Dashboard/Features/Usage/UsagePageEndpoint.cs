using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Usage
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;
    public sealed class UsagePageEndpoint(DashboardSessions sessions, AccountService accounts, UsageService usage) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/usage");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountEndpoints.LoginRedirect(http);
            }

            if (http.Request.Query["tab"] == "billing")
            {
                return HttpRedirects.SeeOther("/organization" + http.Request.QueryString);
            }

            var tab = http.Request.Query["tab"] == "settings" ? "settings" : "overview";
            try
            {
                return UsageSelection.TryFor(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(), http.Request.Query["range"].ToString()) switch
                {
                    UsageSelection selection => await PageAsync(http, usage, selection, tab),
                    NotFound failure => TypedResults.Text(failure.Message ?? "Not found.", statusCode: 404),
                    Forbidden failure => TypedResults.Text(failure.Message, statusCode: 403),
                };
            }
            catch (AccountAccessException error) { return TypedResults.Text(error.Message, statusCode: error.StatusCode); }
            catch (IOException) { return TypedResults.Text("Could not load usage.", statusCode: 503); }
        }

        private static async Task<IResult> PageAsync(HttpContext http, UsageService usage, UsageSelection selection, string tab)
        {
            if (!selection.Member && tab == "settings")
            {
                return TypedResults.Text("Organization membership is required.", statusCode: 403);
            }
            if (selection.Context.Project is null && tab == "overview")
            {
                return HttpRedirects.SeeOther("/");
            }

            var page = await usage.LoadAsync(ShellModel.For(http, selection.Context, "Usage"), selection, tab, http.Request.Query["saved"].ToString(), http.RequestAborted);
            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, UsagePageView.Create(page));
                return TypedResults.Empty;
            }

            return UsageView.Create(page);
        }
    }
}
