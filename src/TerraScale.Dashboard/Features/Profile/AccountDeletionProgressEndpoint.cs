using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class AccountDeletionProgressEndpoint(IDashboardState state, IDashboardIdentity identity, ITsAuthPlatform platform) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/account-deletion");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AccountDeletion.ProgressAsync(HttpContext, state, identity, platform));
    }
}
