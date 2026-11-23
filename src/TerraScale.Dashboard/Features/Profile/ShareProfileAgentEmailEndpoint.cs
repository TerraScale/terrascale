using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class ShareProfileAgentEmailEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/agents/{id}/share-email");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, RevokeProfileAgentEndpoint.ReturnTo, TypedResults.StatusCode(403));
            }

            var id = http.Request.RouteValues["id"]?.ToString() ?? "";
            var share = form.ContainsKey("share_owner_email");
            try { await platform.ShareAccountAgentOwnerEmailAsync(context.Session, id, share, http.RequestAborted); }
            catch (AccountAccessException error)
            { return await ProfileDialogErrors.PageAsync(http, sessions, accounts, "", "agents", error.Message, error.StatusCode); }
            return HttpRedirects.SeeOther(RevokeProfileAgentEndpoint.ReturnTo + (share ? "&status=agent-email-shared" : "&status=agent-email-private"));
        }
    }
}
