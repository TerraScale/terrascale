using Stripe;
using AccountService = TerraScale.Dashboard.Shared.Accounts.AccountService;
using TerraScale.Dashboard.UI;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Features.Billing.Plans;
using TerraScale.Dashboard.Features.Billing.Webhooks;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Billing;

public static class BillingEndpoints
{
    public static IServiceCollection AddBillingFeatures(this IServiceCollection services) =>
        services.AddSingleton<StripeBilling>().AddSingleton<InternalBillingService>();

    public static async Task<BillingPage> LoadAsync(ShellModel shell, StripeBilling billing, SpendingService spending,
        IQueryCollection query, DateTimeOffset now, CancellationToken cancellationToken, InternalBillingService internalBilling)
    {
        // All provider work finishes before Razor starts the response. A
        // corrupt binding can then produce a complete service error page.
        var page = new BillingPage(shell, PlanCatalog.Plans)
        {
            Binding = await billing.ReadBindingAsync(shell.Context, cancellationToken)
        };
        if (shell.Context.Organization!.InternalBilling is not null)
        {
            var internalBill = await internalBilling.ReadAsync(shell.Context, cancellationToken);
            var internalControls = await spending.ReadAsync(shell.Context, cancellationToken);
            var internalCap = internalControls.Projects.FirstOrDefault(project => project.Id == shell.Context.Project?.Id)?.EffectiveCapCents ?? internalControls.OrganizationCapCents;
            return page with
            {
                InternalBill = internalBill,
                PlanChanges = new(page.Binding, null, null, ""),
                History = new([], InvoiceHistory.ForBrowser([], query, now), InvoiceHistory.Summarize([], now), now, "", internalCap)
            };
        }
        var changes = await BillingPlanPresentation.ForAsync(page, billing, query, now, cancellationToken);
        BillingCard? paymentMethod = null;
        var paymentMethodUnavailable = false;
        try { paymentMethod = await billing.PaymentMethodAsync(shell.Context, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or StripeException or BillingPaymentMethodUnavailableException ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { paymentMethodUnavailable = true; }
        IReadOnlyList<BillingInvoice> invoices = [];
        var errorMessage = "";
        try { invoices = await billing.InvoicesAsync(shell.Context, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or IOException or StripeException ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { errorMessage = "Payment history could not be loaded. Apply the filters to retry."; }
        var controls = await spending.ReadAsync(shell.Context, cancellationToken);
        var cap = controls.Projects.FirstOrDefault(project => project.Id == shell.Context.Project?.Id)?.EffectiveCapCents ?? controls.OrganizationCapCents;
        return page with
        {
            PlanChanges = changes,
            PaymentMethod = paymentMethod,
            PaymentMethodUnavailable = paymentMethodUnavailable,
            History = new(invoices, InvoiceHistory.ForBrowser(invoices, query, now), InvoiceHistory.Summarize(invoices, now), now, errorMessage, cap)
        };
    }

    public static IEndpointRouteBuilder MapBilling(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/billing", () => Results.Redirect("/organization?tab=billing"));
        endpoints.MapPost("/billing/checkout", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            try { return HttpRedirects.SeeOther(await billing.StartCheckoutAsync(context, form["plan"].ToString(), form["interval"].ToString(), form["return"].ToString(), http.RequestAborted)); }
            catch (Exception error) when (error is AccountAccessException or OnboardingValidationException)
            { return Results.Problem(error.Message, statusCode: error is AccountAccessException access ? access.StatusCode : 422); }
        });
        endpoints.MapGet("/billing/portal", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            try { return HttpRedirects.SeeOther(await billing.PortalAsync(context, http.RequestAborted)); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
        });
        endpoints.MapStripeWebhooks();
        return endpoints;
    }
}
