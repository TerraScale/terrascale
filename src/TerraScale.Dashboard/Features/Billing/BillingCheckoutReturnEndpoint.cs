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

            var completion = await billing.ResumeOrganizationAsync(context.Session, http);
            if (completion.OrganizationId is { } organizationId)
            {
                return HttpRedirects.SeeOther("/organization?newOrganization=project&organizationId=" + Uri.EscapeDataString(organizationId));
            }
            if (completion.Notice.Length > 0 || await billing.PendingOrganizationAsync(context.Session.UserId, cancellationToken) is not null)
            {
                return HttpRedirects.SeeOther("/organization");
            }

            try { return HttpRedirects.SeeOther(await billing.CheckoutReturnAsync(context, http.Request.Query["session_id"].ToString(), http.RequestAborted)); }
            catch (Exception error) when (BillingMutations.BillingFailure(error, http)) { return BillingMutations.Failure(error, http); }
        }
    }
}
