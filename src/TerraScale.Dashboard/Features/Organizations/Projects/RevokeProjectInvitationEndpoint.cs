using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Datastar;
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
            async Task<IResult> Error(string message, int status)
            {
                if (http.Request.IsDatastarRequest)
                {
                    http.Response.Headers.CacheControl = "no-store";
                    await DatastarEvents.PatchElementsAsync(http, ProjectInvitationPageView.Create(new(ShellModel.For(http, context, "Project invitations"), [], null, message)));
                    return Results.Empty;
                }
                return ProjectInvitationEndpoints.Page(http, context, [], error: message, status: status);
            }

            static async Task<IResult> PatchAsync(HttpContext http, AccountContext context, ProjectInvitationService invitations, string notice, string after)
            {
                var page = await invitations.ReadAsync(context, after.Length == 0 ? null : after, http.RequestAborted);
                http.Response.Headers.CacheControl = "no-store";
                await DatastarEvents.PatchElementsAsync(http, ProjectInvitationPageView.Create(new(ShellModel.For(http, context, "Project invitations"), page.Items, page.Next, Notice: notice)));
                return Results.Empty;
            }

            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/project/invitations", await Error("The form expired. Reload project invitations and try again.", 400));
            }

            if (form["invitationId"].Count != 1 || form["after"].Count > 1)
            {
                return await Error("Choose one invitation and page.", 400);
            }

            var after = form["after"].ToString();
            try
            {
                await invitations.RevokeAsync(context, form["invitationId"].ToString(), after.Length == 0 ? null : after, http.RequestAborted);
                if (http.Request.IsDatastarRequest)
                {
                    return await PatchAsync(http, context, invitations, "Project invitation revoked. Its acceptance link no longer works.", after);
                }
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/project/invitations?revoked=1", replace: true));
            }
            catch (AccountAccessException error) { return await Error(error.Message, error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return await Error("Revocation could not be confirmed. Reload project invitations to check its current status before retrying.", 503); }
        }
    }
}

