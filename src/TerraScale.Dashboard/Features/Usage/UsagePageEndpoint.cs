using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Usage
{
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

            try
            {
                var selection = UsageSelection.For(context, http.Request.Query["project"].ToString(), http.Request.Query["database"].ToString(), http.Request.Query["range"].ToString());
                var tab = http.Request.Query["tab"] == "settings" ? "settings" : "overview";
                if (!selection.Member && tab == "settings")
                {
                    return Results.Text("Organization membership is required.", statusCode: 403);
                }
                if (selection.Context.Project is null && tab == "overview")
                {
                    return HttpRedirects.SeeOther("/");
                }

                var page = await usage.LoadAsync(ShellModel.For(http, selection.Context, "Usage"), selection, tab, http.Request.Query["saved"].ToString(), http.RequestAborted);
                if (http.Request.Headers["Datastar-Request"] == "true")
                {
                    await DatastarEvents.PatchElementsAsync(http, UsagePageView.Create(page));
                    return Results.Empty;
                }

                return UsageView.Create(page);
            }
            catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
            catch (IOException) { return Results.Text("Could not load usage.", statusCode: 503); }
        }
    }
}
