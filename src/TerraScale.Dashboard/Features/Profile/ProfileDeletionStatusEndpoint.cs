using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class ProfileDeletionStatusEndpoint(IDashboardState state, IDashboardIdentity identity, ITsAuthPlatform platform, DashboardSessions sessions, AccessTokens tokens) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/delete/status");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AccountDeletion.StatusAsync(HttpContext, state, identity, platform, sessions, tokens));
    }
}
