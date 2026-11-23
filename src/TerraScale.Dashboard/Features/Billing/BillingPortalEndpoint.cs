using TerraScale.Dashboard.Platform.Clients;
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

            try
            {
                var outcome = await billing.PortalAsync(context, http.RequestAborted);
                return outcome switch
                {
                    PortalSessionUrl session => HttpRedirects.SeeOther(session.Url),
                    DashboardValidation error when DatabaseEndpoints.WantsHtml(http) => BillingRecoveryModel.Response(http, 422, "Billing portal unavailable", error.Message + " Return to billing to choose a plan or finish checkout."),
                    DashboardValidation error => TypedResults.Problem(error.Message, statusCode: 422)
                };
            }
            catch (AccountAccessException error) when (DatabaseEndpoints.WantsHtml(http))
            { return BillingRecoveryModel.Response(http, error.StatusCode, "Billing portal unavailable", error.Message + " Return to billing to choose a plan or finish checkout."); }
            catch (AccountAccessException error) { return TypedResults.Problem(error.Message, statusCode: error.StatusCode); }
        }
    }
}
