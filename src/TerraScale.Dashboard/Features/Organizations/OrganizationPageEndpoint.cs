using RazorSlices;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Organizations.Members;
using TerraScale.Dashboard.Features.Organizations.Wizard;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class OrganizationPageEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement,
        StripeBilling billing, SpendingService spending, TimeProvider time, InternalBillingService internalBilling, UsageOverageReporter reporter) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/organization");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            if (context.Organization is not null && context.Role.Length == 0)
            {
                return Notice(http, context, "organization", "Organization membership is required. Ask an owner or admin for access.", 403);
            }

            if (http.Request.Query["tab"] == "billing" && http.Request.Query["payment"] == "success" && context.Role == "owner")
            {
                try
                {
                    await billing.ConfirmAsync(context, http.RequestAborted);
                    context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
                }
                catch (AccountAccessException error) { return Notice(http, context, "billing", error.Message + (error.StatusCode == 403 ? " Ask an owner or admin for access." : ""), error.StatusCode); }
                catch (Exception error) when (error is IOException or HttpRequestException or Stripe.StripeException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                { return Notice(http, context, "billing", "Billing information could not be loaded. Your organization and projects are still available.", 503); }
            }
            else if (http.Request.Query["tab"] == "billing" && context.Organization is not null)
            {
                try
                {
                    if (await billing.ReconcileCheckoutAsync(context, http.RequestAborted))
                    {
                        context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
                    }
                }
                catch (Exception error) when (error is AccountAccessException or IOException or HttpRequestException or Stripe.StripeException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested) { }
            }
            if (http.Request.Query["tab"] == "audit" && !context.CanManage)
            {
                return Notice(http, context, "audit", "Organization management is required to view audit events. Ask an owner or admin for access.", 403);
            }

            context = await OrganizationWizard.PrepareAsync(http, context, accounts);
            var page = await OrganizationEndpoints.PageModelAsync(http, context, placement);
            if (page.Tab == "billing" && context.Organization is not null)
            {
                try { page = page with { Billing = await BillingEndpoints.LoadAsync(page.Shell, billing, spending, http.Request.Query, time.GetUtcNow(), internalBilling, reporter, http.RequestAborted) }; }
                catch (AccountAccessException error) { return Notice(http, context, "billing", error.Message + (error.StatusCode == 403 ? " Ask an owner or admin for access." : ""), error.StatusCode); }
                catch (Exception error) when (error is IOException or HttpRequestException or Stripe.StripeException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                { return Notice(http, context, "billing", "Billing information could not be loaded. Your organization and projects are still available.", 503); }
            }
            if (http.Request.Headers["Datastar-Request"] == "true" && page.Tab is "members" or "invitations")
            {
                await DatastarEvents.PatchElementsAsync(http, OrganizationAccessPage.Create(page));
                return Results.Empty;
            }
            return OrganizationView.Create(page);
        }

        private static RazorSlice<OrganizationPage> Notice(HttpContext http, AccountContext context, string tab, string message, int status)
        {
            var view = OrganizationView.Create(new(ShellModel.For(http, context, "Organization"), tab, [], Notice: message, NoticeStatus: status));
            view.StatusCode = status;
            return view;
        }
    }
}
