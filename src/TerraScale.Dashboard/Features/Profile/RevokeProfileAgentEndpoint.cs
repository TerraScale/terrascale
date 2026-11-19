using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class RevokeProfileAgentEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform) : EndpointWithoutRequest<IResult>
    {
        internal const string ReturnTo = "/profile?tab=agents";

        public override void Configure() => Post("/api/v1/dashboard/profile/agents/{id}/revoke");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, ReturnTo, Results.StatusCode(403));
            }

            var id = http.Request.RouteValues["id"]?.ToString() ?? "";
            try { await platform.RevokeAccountAgentAsync(context.Session, id, http.RequestAborted); }
            catch (AccountAccessException error)
            { return await ProfileDialogErrors.PageAsync(http, sessions, accounts, "agent-revoke-" + id, "agents", error.Message, error.StatusCode); }
            return HttpRedirects.SeeOther(ReturnTo + "&status=agent-revoked");
        }
    }
}
