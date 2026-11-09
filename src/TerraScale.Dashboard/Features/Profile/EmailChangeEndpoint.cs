using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class EmailChangeEndpoint(DashboardSessions sessions, AccountService accounts, IAuthEmailChangeGateway gateway) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/email-change");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(EmailChange.SubmitAsync(HttpContext, sessions, accounts, gateway, false));
    }
}
