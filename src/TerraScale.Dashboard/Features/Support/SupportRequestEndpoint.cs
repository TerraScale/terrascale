using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Support
{
    public sealed class SupportRequestEndpoint(DashboardSessions sessions, AccountService accounts, ISupportMail mail, ILogger<SupportMail> logger) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/support");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(SupportEndpoints.SendAsync(HttpContext, sessions, accounts, mail, logger));
    }
}
