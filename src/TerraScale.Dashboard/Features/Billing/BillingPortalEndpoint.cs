using TerraScale.TerraBase.Contracts;
using System.Diagnostics;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed class BillingPortalEndpoint(DashboardSessions sessions, AccountService accounts, StripeBilling billing) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/billing/portal");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountEndpoints.LoginRedirect(http);
            }

            var portal = await billing.PortalAsync(context, http.RequestAborted);
            return portal switch
            {
                PortalSessionUrl session => HttpRedirects.SeeOther(session.Url),
                Validation error when DatabaseEndpoints.WantsHtml(http) => BillingRecoveryModel.Response(http, 422, "Billing portal unavailable", error.Message + " Return to billing to choose a plan or finish checkout."),
                Validation error => Results.Problem(error.Message, statusCode: 422),
                _ => throw new UnreachableException()
            };
        }
    }
}
