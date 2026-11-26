using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeleteProfileEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform, DashboardDb storage, AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/delete").AuthenticateInHandler();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AccountDeletion.DeleteAsync(HttpContext, sessions, accounts, platform, storage, tokens, billing, identity));
    }
}
