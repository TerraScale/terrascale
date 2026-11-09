using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Support
{
    public sealed class SupportPageEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/support");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            var sent = http.Request.Query["sent"].ToString();
            return SupportView.Create(new(ShellModel.For(http, context, "Support"), Sent: sent.Length == 8 && sent.All(char.IsAsciiLetterOrDigit) ? sent : "")
            {
                Draft = new(context.Organization?.Id ?? "", context.Project?.Id ?? "", "", "normal", "", "", AccountService.NewId(), TerraScale.Dashboard.Common.Accounts.TerraScaleIds.IsValid(http.Request.Query["relatedRequestId"].ToString()) ? http.Request.Query["relatedRequestId"].ToString() : "")
            });
        }
    }
}
