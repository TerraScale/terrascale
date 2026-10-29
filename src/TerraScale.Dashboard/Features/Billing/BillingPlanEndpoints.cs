using Stripe;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.Dashboard.Shared.Validation;
using AccountService = TerraScale.Dashboard.Shared.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

public static class BillingPlanEndpoints
{
    public static IEndpointRouteBuilder MapBillingPlanChanges(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/billing/change", (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            MutateAsync(http, sessions, accounts, billing, "change"));
        endpoints.MapPost("/billing/change/undo", (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            MutateAsync(http, sessions, accounts, billing, "undo"));
        endpoints.MapPost("/billing/checkout/cancel", (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            MutateAsync(http, sessions, accounts, billing, "cancel"));
        endpoints.MapPost("/checkout/{sessionId}/cancel", (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            MutateAsync(http, sessions, accounts, billing, "cancel"));
        endpoints.MapGet("/billing/checkout/return", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountEndpoints.LoginRedirect(http);
            try { return HttpRedirects.SeeOther(await billing.CheckoutReturnAsync(context, http.Request.Query["session_id"].ToString(), http.RequestAborted)); }
            catch (Exception error) when (BillingFailure(error, http)) { return Failure(error, http); }
        });
        return endpoints;
    }
    private static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing, string operation)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null) return AccountRequest.LoginRedirect(http);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.Text("Invalid CSRF token.", statusCode: 403);
        try
        {
            if (operation == "cancel") return HttpRedirects.SeeOther(await billing.CancelCheckoutAsync(context,
                http.Request.RouteValues["sessionId"]?.ToString() ?? form["sessionId"].ToString(), http.RequestAborted));
            var outcome = operation == "undo" ? await billing.UndoPlanChangeAsync(context, http.RequestAborted)
                : await billing.ChangePlanAsync(context, form["plan"].ToString(), form["interval"].ToString(), http.RequestAborted);
            return HttpRedirects.SeeOther("/organization?tab=billing" + (outcome == "current" ? "" : "&billing=" + outcome));
        }
        catch (Exception error) when (BillingFailure(error, http)) { return Failure(error, http); }
    }
    private static bool BillingFailure(Exception error, HttpContext http) => error is AccountAccessException or OnboardingValidationException or StripeException or HttpRequestException or IOException ||
        error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
    private static IResult Failure(Exception error, HttpContext http)
    {
        if (error is AccountAccessException access) return DatabaseEndpoints.WantsHtml(http)
            ? BillingRecoveryModel.Response(http, access.StatusCode, access.StatusCode == 404 ? "Checkout unavailable" : "Billing request not completed",
                access.Message + " Return to billing to check your current plan before trying again.")
            : Results.Text(access.Message, statusCode: access.StatusCode);
        if (error is OnboardingValidationException validation) return Results.Text(validation.Message, statusCode: 400);
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BillingPlanEndpoints)).LogWarning("Billing change failed ({ErrorType}: {Error})", error.GetType().Name, Describe(error));
        if (DatabaseEndpoints.WantsHtml(http))
        {
            var page = BillingChangeUnavailable.Create(new DocumentModel("Billing change uncertain", DocumentModel.NonceFor(http)));
            page.StatusCode = StatusCodes.Status502BadGateway;
            return page;
        }
        return Results.Text("Stripe could not complete this billing change. Reload billing and try again.", statusCode: 502);
    }
    // Provider messages/parameters and arbitrary exception text can echo credentials or URLs.
    internal static string Describe(Exception error) => error is StripeException vendor
        ? $"Stripe HTTP {(int)vendor.HttpStatusCode}" : error.GetType().Name;
}
