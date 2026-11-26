using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{

    public sealed class RevokeProjectInvitationEndpoint(DashboardSessions sessions, AccountService accounts, ProjectInvitationService invitations) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/project/invitations/revoke").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            async Task<IResult> Error(string message, int status)
            {
                if (http.Request.IsDatastarRequest)
                {
                    http.Response.Headers.CacheControl = "no-store";
                    await DatastarEvents.PatchElementsAsync(http, ProjectInvitationPageView.Create(new(ShellModel.For(http, context, "Project invitations"), [], null, message)));
                    return TypedResults.Empty;
                }
                return ProjectInvitationEndpoints.Page(http, context, [], error: message, status: status);
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
            ProjectInvitationRevocation revoked;
            try
            {
                revoked = await invitations.RevokeAsync(context, form["invitationId"].ToString(), after.Length == 0 ? null : after, http.RequestAborted);
            }
            catch (AccountAccessException error) { return await Error(error.Message, error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return await Error("Revocation could not be confirmed. Reload project invitations to check its current status before retrying.", 503); }
            IResult? refusal = revoked switch
            {
                DashboardForbidden error => await Error(error.Message, 403),
                DashboardValidation error => await Error(error.Message, 400),
                InvalidInvitation error => await Error(error.Message, 422),
                DashboardNotFound error => await Error(error.Message, 404),
                DashboardConflict error => await Error(error.Message, 409),
                DashboardUnavailable => await Error("Revocation could not be confirmed. Reload project invitations to check its current status before retrying.", 503),
                Done => null,
            };
            if (refusal is not null)
            {
                return refusal;
            }

            try
            {
                if (http.Request.IsDatastarRequest)
                {
                    var outcome = await invitations.ReadAsync(context, after.Length == 0 ? null : after, http.RequestAborted);
                    return outcome switch
                    {
                        TsAuthPlatformPage<TsAuthPlatformInvitation> listed => await PatchedAsync(http, context, listed.Items, listed.Next, "Project invitation revoked. Its acceptance link no longer works."),
                        DashboardForbidden error => await Error(error.Message, 403),
                        DashboardValidation error => await Error(error.Message, 400),
                        DashboardUnavailable => await Error("Revocation could not be confirmed. Reload project invitations to check its current status before retrying.", 503),
                    };
                }
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/project/invitations?revoked=1", replace: true));
            }
            catch (AccountAccessException error) { return await Error(error.Message, error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return await Error("Revocation could not be confirmed. Reload project invitations to check its current status before retrying.", 503); }
        }

        private static async Task<IResult> PatchedAsync(HttpContext http, AccountContext context, IReadOnlyList<TsAuthPlatformInvitation> items, string? next, string notice)
        {
            http.Response.Headers.CacheControl = "no-store";
            await DatastarEvents.PatchElementsAsync(http, ProjectInvitationPageView.Create(new(ShellModel.For(http, context, "Project invitations"), items, next, Notice: notice)));
            return TypedResults.Empty;
        }
    }
}
