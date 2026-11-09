using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class ProfileDeletionContinueEndpoint(DashboardSessions sessions, ITsAuthPlatform platform, IDashboardState state, AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/delete/continue");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AccountDeletion.ContinueAsync(HttpContext, sessions, platform, state, tokens, billing, identity));
    }
}
