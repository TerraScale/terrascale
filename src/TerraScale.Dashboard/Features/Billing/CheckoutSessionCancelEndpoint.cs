using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class CheckoutSessionCancelEndpoint(DashboardSessions sessions, AccountService accounts, StripeBilling billing) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/checkout/{sessionId}/cancel");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(BillingMutations.MutateAsync(HttpContext, sessions, accounts, billing, "cancel"));
    }
}
