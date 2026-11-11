using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Invitations
{
    public sealed class InvitationSwitchAccountEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/invitations/{token}/switch-account");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            InvitationEndpoints.Protect(http);
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/invitations/" + Uri.EscapeDataString(http.Request.RouteValues["token"]?.ToString() ?? ""), Results.StatusCode(403));
            }

            return await AuthEndpoints.SignOutAsync(http);
        }
    }
}
