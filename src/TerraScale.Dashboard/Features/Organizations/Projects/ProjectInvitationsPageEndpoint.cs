using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed class ProjectInvitationsPageEndpoint(DashboardSessions sessions, AccountService accounts, ProjectInvitationService invitations) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/project/invitations");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            try
            {
                if (http.Request.Query["after"].Count > 1)
                {
                    throw new AccountAccessException("Choose one page cursor. Open the first page.", 400);
                }

                var after = http.Request.Query["after"].Count == 0 ? null : http.Request.Query["after"].ToString();
                var page = await invitations.ReadAsync(context, after, http.RequestAborted);
                return ProjectInvitationEndpoints.Page(http, context, page.Items, page.Next, previous: after is not null, after: after ?? "",
                    notice: http.Request.Query["revoked"] == "1" ? "Project invitation revoked. Its acceptance link no longer works." : "");
            }
            catch (AccountAccessException error) { return ProjectInvitationEndpoints.Page(http, context, [], error: error.Message, status: error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return ProjectInvitationEndpoints.Page(http, context, [], error: "Project invitations could not be loaded. Reload this page to retry.", status: 503); }
        }
    }
}
