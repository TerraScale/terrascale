using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed class RevokeProjectInvitationEndpoint(DashboardSessions sessions, AccountService accounts, ProjectInvitationService invitations) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/project/invitations/revoke");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            IResult Error(string message, int status)
            {
                return ProjectInvitationEndpoints.Page(http, context, [], error: message, status: status);
            }

            if (!Csrf.Validate(http, form))
            {
                return Error("The form expired. Reload project invitations and try again.", 400);
            }

            if (form["invitationId"].Count != 1 || form["after"].Count > 1)
            {
                return Error("Choose one invitation and page.", 400);
            }

            var after = form["after"].ToString();
            try
            {
                await invitations.RevokeAsync(context, form["invitationId"].ToString(), after.Length == 0 ? null : after, http.RequestAborted);
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/project/invitations?revoked=1", replace: true));
            }
            catch (AccountAccessException error) { return Error(error.Message, error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Error("Revocation could not be confirmed. Reload project invitations to check its current status before retrying.", 503); }
        }
    }
}
