using TerraScale.TerraBase.Contracts;
using Stripe;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Common.Validation;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    internal static class BillingMutations
    {
        public static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing, string operation)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/organization?tab=billing", Results.Text("Invalid CSRF token.", statusCode: 403));
            }

            try
            {
                if (operation == "cancel")
                {
                    return HttpRedirects.SeeOther(await billing.CancelCheckoutAsync(context,
                    http.Request.RouteValues["sessionId"]?.ToString() ?? form["sessionId"].ToString(), http.RequestAborted));
                }

                if (context.Organization is null || context.Role != "owner")
                {
                    throw new AccountAccessException("Only organization owners manage billing.");
                }

                var intent = StepUpFlow.Intent(http, context, "change_plan");
                if (intent is null)
                {
                    return await StepUpFlow.StartAsync(http, context, "change_plan", "ts-auth", boundOrganizationId: context.Organization.Id);
                }

                await StepUpFlow.ConsumeAsync(http, context, intent);
                var outcome = operation == "undo" ? await billing.UndoPlanChangeAsync(context, http.RequestAborted)
                    : await billing.ChangePlanAsync(context, form["plan"].ToString(), form["interval"].ToString(), http.RequestAborted);
                return HttpRedirects.SeeOther("/organization?tab=billing" + (outcome == "current" ? "" : "&billing=" + outcome));
            }
            catch (Exception error) when (BillingFailure(error, http)) { return Failure(error, http); }
        }

        internal static bool BillingFailure(Exception error, HttpContext http)
        {
            return error is AccountAccessException or OnboardingValidationException or StripeException or TerraBaseException or HttpRequestException or IOException ||
            error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
        }

        internal static IResult Failure(Exception error, HttpContext http)
        {
            if (error is AccountAccessException access)
            {
                return DatabaseEndpoints.WantsHtml(http)
                ? BillingRecoveryModel.Response(http, access.StatusCode, access.StatusCode == 404 ? "Checkout unavailable" : "Billing request not completed",
                    access.Message + " Return to billing to check your current plan before trying again.")
                : Results.Text(access.Message, statusCode: access.StatusCode);
            }

            if (error is OnboardingValidationException validation)
            {
                return Results.Text(validation.Message, statusCode: 400);
            }

            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TerraScale.Dashboard.Features.Billing.BillingMutations").BillingChangeFailed(error.GetType().Name, Describe(error));
            if (DatabaseEndpoints.WantsHtml(http))
            {
                var page = BillingChangeUnavailable.Create(new DocumentModel("Billing change uncertain", DocumentModel.NonceFor(http)));
                page.StatusCode = StatusCodes.Status502BadGateway;
                return page;
            }
            return Results.Text("Stripe could not complete this billing change. Reload billing and try again.", statusCode: 502);
        }

        internal static string Describe(Exception error)
        {
            return error is StripeException vendor
            ? $"Stripe HTTP {(int)vendor.HttpStatusCode}" : error.GetType().Name;
        }
    }
}
