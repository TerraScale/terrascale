using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
using TerraScale.Dashboard.Errors;
using System.Diagnostics;

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
                return Results.Unauthorized();
            }

            IFormCollection form;
            switch (await FormRequests.ReadAsync(http, http.RequestAborted))
            {
                case Validation invalid:
                    return FormRequests.Refused(http, invalid);
                case UnsupportedMediaType unsupported:
                    return FormRequests.Refused(http, unsupported);
                case IFormCollection parsed:
                    form = parsed;
                    break;
                case null:
                    throw new UnreachableException();
            }
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, RevokeProfileAgentEndpoint.ReturnTo, Results.StatusCode(403));
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
