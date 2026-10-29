using Stripe;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Features.Billing.Plans;
using TerraScale.Dashboard.Features.Billing.Webhooks;
using TerraScale.Dashboard.Common.Validation;

namespace TerraScale.Dashboard.Features.Billing
{
    public static class BillingEndpoints
    {
        public static IServiceCollection AddBillingFeatures(this IServiceCollection services)
        {
            return services.AddSingleton<StripeBilling>().AddSingleton<InternalBillingService>().AddHostedService<PlanReconciler>()
                .AddSingleton<UsageOverageReporter>().AddHostedService<UsageOverageWorker>();
        }

        public static async Task<BillingPage> LoadAsync(ShellModel shell, StripeBilling billing, SpendingService spending,
            IQueryCollection query, DateTimeOffset now, InternalBillingService internalBilling, CancellationToken cancellationToken)
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
                return page with
                {
                    InternalBill = internalBill,
                    PlanChanges = new(page.Binding, null, null, ""),
                    History = new([], InvoiceHistory.ForBrowser([], query, now), InvoiceHistory.Summarize([], now), now, "", internalControls.OrganizationCapCents)
                };
            }
            // The plan changes, the payment method and the invoices are separate Stripe reads; they run together.
            var changesRead = BillingPlanPresentation.ForAsync(page, billing, query, now, cancellationToken);
            var paymentMethodRead = billing.PaymentMethodAsync(shell.Context, cancellationToken);
            var invoicesRead = billing.InvoicesAsync(shell.Context, cancellationToken);
            var detailsRead = billing.DetailsAsync(shell.Context, cancellationToken);
            var overageRead = page.Binding is { Kind: "stripe", SubscriptionId.Length: > 0 } subscribed && PlanCatalog.For(shell.Context.Organization) is { PriceCents: > 0 }
                ? billing.OperationUnitOverageAsync(subscribed, now, cancellationToken) : Task.FromResult(0L);
            BillingCard? paymentMethod = null;
            var paymentMethodUnavailable = false;
            try { paymentMethod = await paymentMethodRead; }
            catch (Exception error) when (error is HttpRequestException or StripeException or BillingPaymentMethodUnavailableException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { paymentMethodUnavailable = true; }
            IReadOnlyList<BillingInvoice> invoices = [];
            var errorMessage = "";
            try { invoices = await invoicesRead; }
            catch (Exception error) when (error is HttpRequestException or IOException or StripeException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { errorMessage = "Payment history could not be loaded. Apply the filters to retry."; }
            long? overage = null;
            try { overage = await overageRead; }
            catch (Exception error) when (error is HttpRequestException or IOException or StripeException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { }
            var changes = await changesRead;
            BillingDetails? details = null;
            var detailsUnavailable = false;
            try { details = await detailsRead; }
            catch (Exception error) when (error is HttpRequestException or IOException or StripeException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { detailsUnavailable = true; }
            return page with
            {
                Details = details,
                DetailsUnavailable = detailsUnavailable,
                OverageUnits = overage,
                PlanChanges = changes,
                PaymentMethod = paymentMethod,
                PaymentMethodUnavailable = paymentMethodUnavailable,
                History = new(invoices, InvoiceHistory.ForBrowser(invoices, query, now), InvoiceHistory.Summarize(invoices, now, Renewal(page.Binding, changes.State)), now, errorMessage, 0)
            };
        }

        // The next invoice is the renewal charge: the scheduled plan's price when a change applies at renewal, else the current plan's.
        private static InvoiceRenewal? Renewal(BillingBinding? binding, BillingPlanState? state)
        {
            static long Price(string key, string interval)
            {
                return PlanCatalog.Plans.FirstOrDefault(plan => plan.Key == key) is { } plan
                ? plan.IntervalPriceCents(interval) : 0;
            }

            if (state is not null)
            {
                var scheduled = state.ScheduledPlanKey.Length > 0 && (state.ScheduledAt is null || state.ScheduledAt <= state.RenewalAt);
                var interval = scheduled && state.ScheduledInterval.Length > 0 ? state.ScheduledInterval : state.Interval;
                return new(state.RenewalAt, Price(scheduled ? state.ScheduledPlanKey : state.PlanKey, interval), "usd", state.Interval, state.CancelAtPeriodEnd, state.PeriodStart);
            }
            if (binding is not { SubscriptionId.Length: > 0, RenewalAt: { } renews } || binding.Status is "canceled" or "incomplete_expired")
            {
                return null;
            }

            var current = binding.EffectiveInterval.Length > 0 ? binding.EffectiveInterval : binding.Interval;
            return new(renews, Price(binding.EffectivePlan, current), "usd", current);
        }

        public static IEndpointRouteBuilder MapBilling(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/billing", () => Results.Redirect("/organization?tab=billing"));
            endpoints.MapPost("/billing/checkout", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.StatusCode(403);
                }

                try { return HttpRedirects.SeeOther(await billing.StartCheckoutAsync(context, form["plan"].ToString(), form["interval"].ToString(), form["return"].ToString(), http.RequestAborted)); }
                catch (Exception error) when (error is AccountAccessException or OnboardingValidationException)
                {
                    if (DatabaseEndpoints.WantsHtml(http))
                    {
                        return HttpRedirects.SeeOther("/organization?tab=billing&payment=blocked");
                    }

                    return Results.Problem(error.Message, statusCode: error is AccountAccessException access ? access.StatusCode : 422);
                }
                catch (Exception error) when (error is StripeException or HttpRequestException or IOException ||
                    error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                {
                    // The customer sees a generic 503; the cause (such as a Stripe refusal) must reach the host log.
                    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BillingEndpoints)).CheckoutForOrganizationIsUnavailable(context.Organization?.Id, error.GetType().Name, BillingPlanEndpoints.Describe(error));
                    if (!DatabaseEndpoints.WantsHtml(http))
                    {
                        return Results.Problem("Checkout is temporarily unavailable. Return to billing to reconcile before trying again.", statusCode: 503);
                    }

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
                if (context is null)
                {
                    return AccountEndpoints.LoginRedirect(http);
                }

                try { return HttpRedirects.SeeOther(await billing.PortalAsync(context, http.RequestAborted)); }
                catch (AccountAccessException error) when (DatabaseEndpoints.WantsHtml(http))
                { return BillingRecoveryModel.Response(http, error.StatusCode, "Billing portal unavailable", error.Message + " Return to billing to choose a plan or finish checkout."); }
                catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            });
            endpoints.MapStripeWebhooks();
            return endpoints;
        }
    }

    public sealed record CheckoutRequest([property: System.Text.Json.Serialization.JsonPropertyName("_csrf")] string Csrf,
        string Plan, string Interval, [property: System.Text.Json.Serialization.JsonPropertyName("return")] string Return);
}
