using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class AccountDeletionProgressEndpoint(DashboardDb storage, IDashboardIdentity identity, ITsAuthPlatform platform) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/account-deletion").AuthenticateInHandler();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AccountDeletion.ProgressAsync(HttpContext, storage, identity, platform));
    }
}
