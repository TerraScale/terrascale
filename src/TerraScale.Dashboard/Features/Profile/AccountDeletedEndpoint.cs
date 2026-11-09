using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class AccountDeletedEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/account-deleted");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AccountDeletedView.Create(new("Account deleted", DocumentModel.NonceFor(HttpContext))));
    }
}
