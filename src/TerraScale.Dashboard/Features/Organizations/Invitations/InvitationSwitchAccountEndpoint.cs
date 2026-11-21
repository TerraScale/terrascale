using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;
using TerraScale.Dashboard.Errors;
using System.Diagnostics;

namespace TerraScale.Dashboard.Features.Organizations.Invitations
{
    public sealed class InvitationSwitchAccountEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/invitations/{token}/switch-account");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            InvitationEndpoints.Protect(http);
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
                return RequestError.FormExpired(http, "/invitations/" + Uri.EscapeDataString(http.Request.RouteValues["token"]?.ToString() ?? ""), Results.StatusCode(403));
            }

            return await AuthEndpoints.SignOutAsync(http);
        }
    }
}
