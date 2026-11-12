using TerraScale.TerraBase.Contracts;
using Stripe;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class BillingCheckoutReturnEndpoint(DashboardSessions sessions, AccountService accounts, StripeBilling billing) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/billing/checkout/return");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountEndpoints.LoginRedirect(http);
            }

            var draft = await billing.OrganizationDraftForReturnAsync(context.Session.UserId, http.Request.Query["session_id"].ToString(), cancellationToken);
            if (draft is not null && http.Request.Query["session_id"] == draft.CheckoutId)
            {
                var completion = await billing.ResumeOrganizationAsync(context.Session, http);
                return HttpRedirects.SeeOther(completion.OrganizationId is { } organizationId
                    ? "/organization?newOrganization=project&organizationId=" + Uri.EscapeDataString(organizationId)
                    : "/organization");
            }

            try { return HttpRedirects.SeeOther(await billing.CheckoutReturnAsync(context, http.Request.Query["session_id"].ToString(), http.RequestAborted)); }
            catch (Exception error) when (BillingMutations.BillingFailure(error, http)) { return BillingMutations.Failure(error, http); }
        }
    }
}
