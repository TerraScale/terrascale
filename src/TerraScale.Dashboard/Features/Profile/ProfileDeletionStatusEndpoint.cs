using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class ProfileDeletionStatusEndpoint(DashboardDb storage, IDashboardIdentity identity, ITsAuthPlatform platform, DashboardSessions sessions, AccessTokens tokens) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/delete/status").AuthenticateInHandler();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AccountDeletion.StatusAsync(HttpContext, storage, identity, platform, sessions, tokens));
    }
}
