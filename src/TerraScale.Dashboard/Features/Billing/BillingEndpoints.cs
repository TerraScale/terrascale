using Stripe;
using TerraScale.Dashboard.Features.Onboarding;
using AccountService = TerraScale.Dashboard.Features.Accounts.AccountService;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.UI;
using TerraScale.Dashboard.Features.Databases;

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
            catch (Exception error) when (error is StripeException or HttpRequestException or IOException ||
                error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            {
                if (!DatabaseEndpoints.WantsHtml(http)) return Results.Problem("Checkout is temporarily unavailable. Return to billing to reconcile before trying again.", statusCode: 503);
                var page = BillingCheckoutUnavailable.Create(new DocumentModel("Checkout unavailable", DocumentModel.NonceFor(http)));
                page.StatusCode = 503;
                return (IResult)page;
            }
        }).WithGroupName("integration").WithName("startDashboardCheckout").WithTags("Billing")
            .WithSummary("Start or resume a bound Stripe checkout")
            .WithDescription("Requires the current session, organization-owner permission and a same-origin CSRF form token. An unavailable provider returns 503; return to billing to resume or cancel rather than automatically retrying a mutation.")
            .Accepts<CheckoutRequest>("application/x-www-form-urlencoded")
            .Produces(303).Produces(401).Produces(403).ProducesProblem(422).ProducesProblem(503).Produces(503, contentType: "text/html");
        endpoints.MapGet("/billing/portal", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountEndpoints.LoginRedirect(http);
            try { return HttpRedirects.SeeOther(await billing.PortalAsync(context, http.RequestAborted)); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
        });
        endpoints.MapStripeWebhooks();
        return endpoints;
    }

    private static async Task<IResult> HandleStripeWebhookAsync(HttpContext http, StripeBilling billing)
    {
        const int bodyLimit = 1 << 20;
        const int rejectionDrainAllowance = 64 * 1024;
        // Raise the limit before any early 413 so Kestrel can drain a slightly
        // oversized known-length upload instead of resetting the sender mid-write.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = bodyLimit + rejectionDrainAllowance;
        if (http.Request.ContentLength > bodyLimit) return Results.StatusCode(413);
        using var payload = new MemoryStream();
        var buffer = new byte[8192];
        long received = 0;
        var oversized = false;
        try
        {
            int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) != 0)
            {
                received += count;
                if (received > bodyLimit + rejectionDrainAllowance) return Results.StatusCode(413);
                // A bounded drain lets a slightly oversized chunked sender finish
                // uploading and observe 413. Rejected bytes are never buffered or
                // parsed, and Kestrel still imposes a fixed hard ceiling.
                if (received > bodyLimit) { oversized = true; continue; }
                payload.Write(buffer, 0, count);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Results.StatusCode(413); }
        if (oversized) return Results.StatusCode(413);
        var body = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
        try { await billing.HandleWebhookAsync(body, http.Request.Headers["Stripe-Signature"].ToString(), http.RequestAborted); return Results.Ok(); }
        catch (StripeWebhookRejectedException) { return Results.BadRequest(); }
    }
}

public sealed record CheckoutRequest([property: System.Text.Json.Serialization.JsonPropertyName("_csrf")] string Csrf,
    string Plan, string Interval, [property: System.Text.Json.Serialization.JsonPropertyName("return")] string Return);
